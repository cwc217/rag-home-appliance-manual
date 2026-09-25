using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Diagnostics;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using System.Collections.Concurrent;
using System.Text;

namespace HomeApplianceManualRag.Web.Chat;

public enum RetrievalScope { AnonymousAdminPublic, UserOwn, AdminAll }

public sealed record RetrievedChunk(
    Guid DocumentId, string DocumentName, int PageNumber, string Text, double Similarity);

public sealed record ChatCitation(Guid DocumentId, string DocumentName, int PageNumber)
{
    public string Label => $"[{DocumentName}, p. {PageNumber}]";
}

public static class CitationSelector
{
    public static IReadOnlyList<ChatCitation> SelectReferenced(
        string answer,
        IReadOnlyList<ChatCitation> candidates) =>
        candidates
            .Where(citation => answer.Contains(citation.Label, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(citation => (citation.DocumentId, citation.PageNumber))
            .ToArray();

    public static string RemoveMarkers(string answer, IReadOnlyList<ChatCitation> citations)
    {
        foreach (var citation in citations)
            answer = answer.Replace(citation.Label, string.Empty, StringComparison.OrdinalIgnoreCase);

        return answer;
    }
}

public sealed record ChatResponse(
    IReadOnlyList<RetrievedChunk> Context,
    IReadOnlyList<ChatCitation> Citations,
    string? NoContextMessage = null)
{
    public bool HasContext => Context.Count > 0;
}

public sealed class ChatRateLimitException(TimeSpan retryAfter)
    : InvalidOperationException("Too many chat requests. Please try again shortly.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class ChatProviderException(Exception inner)
    : InvalidOperationException("The answer service is temporarily unavailable.", inner);

public sealed class ChatRateLimiter(IOptions<RateLimitingOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> requests = new();
    private readonly int limit = options.Value.RequestsPerMinute;

    public void EnsureAllowed(string key)
    {
        var now = DateTime.UtcNow;
        var queue = requests.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1))
                queue.Dequeue();
            if (queue.Count >= limit)
            {
                var retry = TimeSpan.FromMinutes(1) - (now - queue.Peek());
                throw new ChatRateLimitException(retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1));
            }
            queue.Enqueue(now);
        }
    }
}

public sealed class ManualRetriever(
    ApplicationDbContext db,
    IEmbeddingProvider embeddings,
    IOptions<RetrievalOptions> options)
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question,
        RetrievalScope scope,
        string? userId,
        IReadOnlyCollection<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        var selectedIds = documentIds?.Distinct().ToArray();
        if (selectedIds is { Length: 0 })
            return [];

        var query = db.ManualChunks.AsNoTracking()
            .Where(x => x.Document != null &&
                        x.Document.DeletedAtUtc == null &&
                        x.Document.Status == DocumentStatus.Ready);
        if (selectedIds is not null)
            query = query.Where(x => selectedIds.Contains(x.DocumentId));
        query = scope switch
        {
            RetrievalScope.AnonymousAdminPublic => query.Where(x => x.Document!.OwnerUserId == null),
            RetrievalScope.UserOwn when !string.IsNullOrWhiteSpace(userId) => query.Where(x => x.Document!.OwnerUserId == userId),
            RetrievalScope.AdminAll => query,
            _ => query.Where(_ => false)
        };
        if (!await query.AnyAsync(ct))
            return [];

        var queryVector = await embeddings.EmbedAsync(question, ct);
        if (queryVector.Length != ApplicationDbContext.EmbeddingDimensions)
            throw new InvalidOperationException("The query embedding has an invalid dimension.");

        if (db.Database.IsNpgsql())
        {
            var vector = new Vector(queryVector.ToArray());
            var restrictToSelected = selectedIds is not null;
            var selectedDocumentIds = selectedIds ?? [];
            // Keep the visibility predicate in SQL before pgvector's cosine operator.
            var chunks = scope == RetrievalScope.UserOwn
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" = {userId}
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.TopK}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : scope == RetrievalScope.AnonymousAdminPublic
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.TopK}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.TopK}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct);
            return chunks.Select(x => new RetrievedChunk(
                x.DocumentId, x.Document!.DisplayName, x.PageNumber, x.Text,
                1d - CosineDistance(x.Embedding, queryVector))).ToArray();
        }

        // In-memory providers are used by unit tests; production always takes the SQL path above.
        var candidates = await query.Include(x => x.Document).ToListAsync(ct);
        return candidates
            .Select(x => new RetrievedChunk(x.DocumentId, x.Document!.DisplayName, x.PageNumber, x.Text,
                CosineSimilarity(x.Embedding, queryVector)))
            .OrderByDescending(x => x.Similarity)
            .Take(options.Value.TopK)
            .ToArray();
    }

    private static double CosineSimilarity(Vector? left, ReadOnlyMemory<float> right)
    {
        if (left is null || left.ToArray().Length != right.Length) return 0;
        var a = left.ToArray();
        var b = right.Span;
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            leftLength += a[i] * a[i];
            rightLength += b[i] * b[i];
        }
        return leftLength == 0 || rightLength == 0 ? 0 : dot / Math.Sqrt(leftLength * rightLength);
    }

    private static double CosineDistance(Vector? left, ReadOnlyMemory<float> right) =>
        1d - CosineSimilarity(left, right);
}

public static class GroundedPrompt
{
    public static string Build(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        IReadOnlyCollection<Guid>? selectedDocumentIds = null)
    {
        var context = new StringBuilder();
        foreach (var chunk in chunks)
            context.AppendLine($"[{chunk.DocumentName}, p. {chunk.PageNumber}] {chunk.Text}");
        var selectedManuals = selectedDocumentIds is null
            ? string.Empty
            : $$"""
                <selected_manual_ids>
                {{string.Join(Environment.NewLine, selectedDocumentIds)}}
                </selected_manual_ids>

                Search only these selected manual IDs. Do not use any other manual.

                """;
        return $$"""
            {{selectedManuals}}
            <manual_excerpts>
            {{context}}
            </manual_excerpts>

            <user_question>
            {{question}}
            </user_question>
            """;
    }
}

public sealed class ChatService(
    IChatProvider provider,
    ChatToolsService tools,
    ChatRateLimiter rateLimiter,
    ICurrentUser currentUser,
    IActiveAccountGuard accountGuard,
    IHttpContextAccessor httpContextAccessor,
    IOptions<ChatOptions> chatOptions,
    IOperationalErrorRecorder errorRecorder)
{
    public async Task<(ChatResponse Response, IAsyncEnumerable<string>? Stream)> StartAsync(
        string question,
        CancellationToken ct = default,
        IReadOnlyCollection<Guid>? selectedDocumentIds = null)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Ask a question first.", nameof(question));
        var normalizedQuestion = question.Trim();
        if (normalizedQuestion.Length > chatOptions.Value.MaxQuestionLength)
            throw new ArgumentException(
                $"Question cannot exceed {chatOptions.Value.MaxQuestionLength} characters.",
                nameof(question));
        if (selectedDocumentIds is { Count: 0 })
            throw new ArgumentException("Select at least one available manual.", nameof(selectedDocumentIds));

        var selectedIds = selectedDocumentIds?.Distinct().ToArray();
        if (selectedIds is not null)
            await tools.ValidateSelectedManualsAsync(selectedIds, ct);
        var key = currentUser.UserId ?? httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        rateLimiter.EnsureAllowed(key);
        var prompt = GroundedPrompt.Build(normalizedQuestion, [], selectedIds);
        var context = new List<RetrievedChunk>();
        var citations = new List<ChatCitation>();
        var response = new ChatResponse(context, citations);
        return (response, StreamToolsAsync(prompt, context, citations, selectedIds, ct));
    }

    private async IAsyncEnumerable<string> StreamToolsAsync(
        string prompt,
        List<RetrievedChunk> context,
        List<ChatCitation> citations,
        IReadOnlyCollection<Guid>? selectedDocumentIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rounds = new List<ChatToolRound>();
        var callsMade = 0;
        while (true)
        {
            IReadOnlyList<ChatToolCall>? requestedCalls = null;
            await foreach (var update in StreamProviderUpdatesAsync(
                               prompt, ChatToolDefinitions.All, rounds, ct).WithCancellation(ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    yield return update.Text;
                if (update.ToolCalls is { Count: > 0 } toolCalls)
                    requestedCalls = toolCalls;
            }

            if (requestedCalls is null)
                yield break;

            var results = new List<ChatToolResult>(requestedCalls.Count);
            foreach (var call in requestedCalls)
            {
                ct.ThrowIfCancellationRequested();
                if (callsMade >= 4)
                {
                    results.Add(new ChatToolResult(
                        call.Id, """{"error":"The per-question tool-call limit has been reached."}"""));
                    continue;
                }

                var execution = await ExecuteToolAsync(call, selectedDocumentIds, ct);
                callsMade++;
                results.Add(new ChatToolResult(call.Id, execution.Content));
                foreach (var chunk in execution.Chunks)
                {
                    if (!context.Any(existing => existing.DocumentId == chunk.DocumentId &&
                                                 existing.PageNumber == chunk.PageNumber))
                        context.Add(chunk);
                }
            }

            rounds.Add(new ChatToolRound(requestedCalls, results));
            var citationsForContext = context
                .Select(x => new ChatCitation(x.DocumentId, x.DocumentName, x.PageNumber))
                .DistinctBy(x => (x.DocumentId, x.PageNumber));
            foreach (var citation in citationsForContext)
            {
                if (!citations.Any(existing => existing.DocumentId == citation.DocumentId &&
                                               existing.PageNumber == citation.PageNumber))
                    citations.Add(citation);
            }

            if (callsMade >= 4)
            {
                await foreach (var update in StreamProviderUpdatesAsync(
                                   prompt, [], rounds, ct).WithCancellation(ct))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        yield return update.Text;
                    if (update.ToolCalls is { Count: > 0 })
                        throw new ChatProviderException(
                            new InvalidOperationException("The provider returned tools after tools had been disabled."));
                }
                yield break;
            }
        }
    }

    private async IAsyncEnumerable<ChatProviderUpdate> StreamProviderUpdatesAsync(
        string prompt,
        IReadOnlyList<ChatToolDefinition> definitions,
        IReadOnlyList<ChatToolRound> rounds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerable<ChatProviderUpdate> stream;
        try
        {
            stream = provider.StreamWithToolsAsync(prompt, definitions, rounds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await errorRecorder.RecordAsync("Chat provider", "Generate answer", ex, cancellationToken: ct);
            throw new ChatProviderException(ex);
        }

        await using var enumerator = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await errorRecorder.RecordAsync("Chat provider", "Generate answer", ex, cancellationToken: ct);
                throw new ChatProviderException(ex);
            }

            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
    }

    private async Task<ChatToolExecution> ExecuteToolAsync(
        ChatToolCall call,
        IReadOnlyCollection<Guid>? selectedDocumentIds,
        CancellationToken ct)
    {
        try
        {
            return await tools.ExecuteAsync(call, ct, selectedDocumentIds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await errorRecorder.RecordAsync("Chat tools", call.Name, ex, cancellationToken: ct);
            throw new ChatProviderException(ex);
        }
    }
}
