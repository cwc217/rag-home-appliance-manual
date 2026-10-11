using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Diagnostics;
using HomeApplianceManualRag.Web.Documents;
using HomeApplianceManualRag.Web.Providers;
using HomeApplianceManualRag.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeApplianceManualRag.Web.Chat;

public enum RetrievalScope { AnonymousAdminPublic, UserOwn, AdminAll }

public sealed record RetrievedChunk(
    Guid DocumentId, string DocumentName, int PageNumber, string Text, double Similarity)
{
    public Guid ChunkId { get; init; }
    public int[] PageNumbers { get; init; } = PageNumber > 0 ? [PageNumber] : [];
    public string[] ParentHeaders { get; init; } = [];
    public ManualChunkType? ChunkType { get; init; }
}

public interface IManualReranker
{
    Task<IReadOnlyList<Guid>> RerankAsync(
        string question,
        IReadOnlyList<RetrievedChunk> candidates,
        int resultCount,
        CancellationToken cancellationToken = default);
}

public sealed record ChatCitation(Guid DocumentId, string DocumentName, int PageNumber)
{
    public string Label => $"[{DocumentName}, p. {PageNumber}]";
}

public static class CitationSelector
{
    private static readonly Regex PageReferencePattern = new(
        @"(?<first>\d+)(?:\s*[-–—]\s*(?<last>\d+))?",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<ChatCitation> SelectReferenced(
        string answer,
        IReadOnlyList<ChatCitation> candidates)
    {
        var references = FindReferences(answer, candidates.Select(citation => citation.DocumentName));
        return candidates
            .Where(citation => references.Any(reference =>
                string.Equals(reference.DocumentName, citation.DocumentName, StringComparison.OrdinalIgnoreCase) &&
                reference.PageRanges.Any(range => range.Contains(citation.PageNumber))))
            .DistinctBy(citation => (citation.DocumentId, citation.PageNumber))
            .ToArray();
    }

    public static string RemoveMarkers(string answer, IReadOnlyList<ChatCitation> citations)
    {
        foreach (var documentName in citations
                     .Select(citation => citation.DocumentName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pattern = CreateCitationPattern(documentName);
            answer = pattern.Replace(answer, match =>
            {
                var pageRanges = ParsePageRanges(match.Groups["pages"].Value);
                return citations.Any(citation =>
                           string.Equals(citation.DocumentName, documentName, StringComparison.OrdinalIgnoreCase) &&
                           pageRanges.Any(range => range.Contains(citation.PageNumber)))
                    ? string.Empty
                    : match.Value;
            });
        }

        return answer;
    }

    private static IReadOnlyList<CitationReference> FindReferences(
        string answer,
        IEnumerable<string> documentNames)
    {
        var references = new List<CitationReference>();
        foreach (var documentName in documentNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pattern = CreateCitationPattern(documentName);
            references.AddRange(pattern.Matches(answer)
                .Select(match => new CitationReference(
                    documentName,
                    ParsePageRanges(match.Groups["pages"].Value))));
        }

        return references;
    }

    private static Regex CreateCitationPattern(string documentName) => new(
        $@"\[\s*{Regex.Escape(documentName)}\s*,\s*pp?\.?\s*(?<pages>\d+(?:\s*[-–—]\s*\d+)?(?:\s*[,;]\s*\d+(?:\s*[-–—]\s*\d+)?)*)\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<PageRange> ParsePageRanges(string pages)
    {
        var ranges = new List<PageRange>();
        foreach (Match match in PageReferencePattern.Matches(pages))
        {
            if (!int.TryParse(match.Groups["first"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var first))
                continue;

            var last = first;
            if (match.Groups["last"].Success &&
                !int.TryParse(match.Groups["last"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out last))
                continue;

            ranges.Add(new PageRange(Math.Min(first, last), Math.Max(first, last)));
        }

        return ranges;
    }

    private sealed record CitationReference(string DocumentName, IReadOnlyList<PageRange> PageRanges);

    private readonly record struct PageRange(int First, int Last)
    {
        public bool Contains(int page) => page >= First && page <= Last;
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
    IOptions<RetrievalOptions> options,
    IChunkTextAnalyzer? textAnalyzer = null,
    IManualReranker? reranker = null)
{
    private readonly IChunkTextAnalyzer analyzer = textAnalyzer ?? new MultilingualTextAnalyzer();

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question,
        RetrievalScope scope,
        string? userId,
        IReadOnlyCollection<Guid>? documentIds = null,
        CancellationToken ct = default)
    {
        var selectedIds = documentIds?.Distinct().ToArray();
        if (selectedIds is { Length: 0 })
            selectedIds = null;

        var query = ApplyVisibilityFilters(
            db.ManualChunks.AsNoTracking(), scope, userId, selectedIds);
        if (!await query.AnyAsync(ct))
            return [];

        var analyzedQuery = analyzer.Analyze(question);
        var queryVector = await embeddings.EmbedAsync(question, ct);
        if (queryVector.Length != ApplicationDbContext.EmbeddingDimensions)
            throw new InvalidOperationException("The query embedding has an invalid dimension.");

        List<ManualChunk> vectorCandidates;
        List<ManualChunk> lexicalCandidates;
        if (db.Database.IsNpgsql())
        {
            var vector = new Vector(queryVector.ToArray());
            var restrictToSelected = selectedIds is not null;
            var selectedDocumentIds = selectedIds ?? [];
            // Visibility and manual-selection filters are applied before candidate ranking.
            vectorCandidates = scope == RetrievalScope.UserOwn
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" = {userId}
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : scope == RetrievalScope.AnonymousAdminPublic
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    ORDER BY c."Embedding" <=> {vector} LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct);

            var tsQuery = analyzer.BuildTsQuery(question);
            lexicalCandidates = string.IsNullOrWhiteSpace(tsQuery)
                ? []
                : scope == RetrievalScope.UserOwn
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" = {userId}
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    AND c."SearchVector" @@ to_tsquery('simple', {tsQuery})
                    ORDER BY ts_rank_cd(c."SearchVector", to_tsquery('simple', {tsQuery})) DESC
                    LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : scope == RetrievalScope.AnonymousAdminPublic
                ? await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL AND d."OwnerUserId" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    AND c."SearchVector" @@ to_tsquery('simple', {tsQuery})
                    ORDER BY ts_rank_cd(c."SearchVector", to_tsquery('simple', {tsQuery})) DESC
                    LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct)
                : await db.ManualChunks.FromSqlInterpolated($"""
                    SELECT c.* FROM "ManualChunks" c
                    INNER JOIN "ManualDocuments" d ON d."Id" = c."DocumentId"
                    WHERE d."Status" = {(int)DocumentStatus.Ready} AND d."DeletedAtUtc" IS NULL
                    AND (NOT {restrictToSelected} OR c."DocumentId" = ANY({selectedDocumentIds}))
                    AND c."Embedding" IS NOT NULL
                    AND c."SearchVector" @@ to_tsquery('simple', {tsQuery})
                    ORDER BY ts_rank_cd(c."SearchVector", to_tsquery('simple', {tsQuery})) DESC
                    LIMIT {options.Value.CandidateCount}
                    """).AsNoTracking().Include(x => x.Document).ToListAsync(ct);
        }
        else
        {
            // In-memory providers are used by unit tests; production uses PostgreSQL ranking.
            var candidates = await query.Include(x => x.Document).ToListAsync(ct);
            vectorCandidates = candidates
                .Where(chunk => chunk.ChunkType != ManualChunkType.SectionParent)
                .OrderByDescending(chunk => CosineSimilarity(chunk.Embedding, queryVector))
                .Take(options.Value.CandidateCount)
                .ToList();
            var terms = analyzedQuery.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            lexicalCandidates = terms.Count == 0
                ? []
                : candidates
                    .Where(chunk => chunk.ChunkType != ManualChunkType.SectionParent &&
                                    chunk.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                        .Any(terms.Contains))
                    .OrderByDescending(chunk => chunk.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.Ordinal).Count(terms.Contains))
                    .Take(options.Value.CandidateCount)
                    .ToList();
        }

        var ranked = FuseRanks(vectorCandidates, lexicalCandidates)
            .Take(options.Value.EnableReranking
                ? options.Value.RerankerCandidateCount
                : options.Value.TopK)
            .ToArray();
        if (ranked.Length == 0)
            return [];

        var rankedById = ranked.ToDictionary(candidate => candidate.Chunk.Id);
        var rerankInputs = ranked.Select(candidate => ToRetrievedChunk(candidate.Chunk, candidate.Score))
            .ToArray();
        var selectedChunkIds = options.Value.EnableReranking
            ? reranker is null
                ? throw new InvalidOperationException("Reranking is enabled but no reranker is registered.")
                : await reranker.RerankAsync(question, rerankInputs, options.Value.TopK, ct)
            : rerankInputs.Take(options.Value.TopK).Select(candidate => candidate.ChunkId).ToArray();
        if (selectedChunkIds.Count == 0)
            throw new InvalidOperationException("The reranker returned no ranked candidates.");
        if (selectedChunkIds.Distinct().Count() != selectedChunkIds.Count)
            throw new InvalidOperationException("The reranker returned duplicate chunk IDs.");
        var selected = selectedChunkIds
            .Select(id => rankedById.TryGetValue(id, out var candidate)
                ? candidate
                : throw new InvalidOperationException("The reranker returned an unknown chunk ID."))
            .Take(options.Value.TopK)
            .ToArray();
        var parentIds = selected
            .Where(candidate => candidate.Chunk.ParentChunkId is not null)
            .Select(candidate => candidate.Chunk.ParentChunkId!.Value)
            .Distinct()
            .ToArray();
        var parents = parentIds.Length == 0
            ? []
            : await ApplyVisibilityFilters(
                    db.ManualChunks.AsNoTracking(), scope, userId, selectedIds)
                .Where(chunk => parentIds.Contains(chunk.Id) &&
                                chunk.ChunkType == ManualChunkType.SectionParent)
                .ToDictionaryAsync(chunk => chunk.Id, ct);

        return selected.Select(candidate =>
        {
            var chunk = candidate.Chunk;
            var result = ToRetrievedChunk(chunk, candidate.Score);
            if (chunk.ParentChunkId is not Guid parentId ||
                !parents.TryGetValue(parentId, out var parent) ||
                parent.TokenCount > options.Value.MaxExpandedParentTokens)
                return result;

            return result with
            {
                Text = parent.Content,
                PageNumber = parent.PageNumber,
                PageNumbers = parent.PageNumbers,
                ParentHeaders = parent.ParentHeaders,
                ChunkType = parent.ChunkType
            };
        }).DistinctBy(chunk => (
            chunk.DocumentId,
            string.Join(',', chunk.PageNumbers),
            chunk.Text)).ToArray();
    }

    private static RetrievedChunk ToRetrievedChunk(ManualChunk chunk, double score) =>
        new(chunk.DocumentId, chunk.Document!.DisplayName, chunk.PageNumber, chunk.Content, score)
        {
            ChunkId = chunk.Id,
            PageNumbers = chunk.PageNumbers,
            ParentHeaders = chunk.ParentHeaders,
            ChunkType = chunk.ChunkType
        };

    private IQueryable<ManualChunk> ApplyVisibilityFilters(
        IQueryable<ManualChunk> query,
        RetrievalScope scope,
        string? userId,
        Guid[]? selectedIds)
    {
        query = query.Where(chunk => chunk.Document != null &&
                                     chunk.Document.DeletedAtUtc == null &&
                                     chunk.Document.Status == DocumentStatus.Ready);
        if (selectedIds is not null)
            query = query.Where(chunk => selectedIds.Contains(chunk.DocumentId));
        return scope switch
        {
            RetrievalScope.AnonymousAdminPublic =>
                query.Where(chunk => chunk.Document!.OwnerUserId == null),
            RetrievalScope.UserOwn when !string.IsNullOrWhiteSpace(userId) =>
                query.Where(chunk => chunk.Document!.OwnerUserId == userId),
            RetrievalScope.AdminAll => query,
            _ => query.Where(_ => false)
        };
    }

    private static IReadOnlyList<(ManualChunk Chunk, double Score)> FuseRanks(
        IReadOnlyList<ManualChunk> vectorCandidates,
        IReadOnlyList<ManualChunk> lexicalCandidates)
    {
        const double rankConstant = 60;
        var scores = new Dictionary<Guid, (ManualChunk Chunk, double Score)>();
        AddRankedCandidates(vectorCandidates);
        AddRankedCandidates(lexicalCandidates);
        return scores.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Chunk.ChunkOrdinal)
            .ToArray();

        void AddRankedCandidates(IReadOnlyList<ManualChunk> candidates)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                var chunk = candidates[index];
                scores.TryGetValue(chunk.Id, out var current);
                scores[chunk.Id] = (chunk, current.Score + 1d / (rankConstant + index + 1));
            }
        }
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

}

public static class GroundedPrompt
{
    public static string Build(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        IReadOnlyCollection<Guid>? selectedDocumentIds = null)
    {
        if (selectedDocumentIds is { Count: 0 })
            selectedDocumentIds = null;

        var context = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var sourcePages = chunk.PageNumbers.Length == 0
                ? [chunk.PageNumber]
                : chunk.PageNumbers;
            var sourceLabels = sourcePages.Distinct().Order()
                .Select(page => $"[{chunk.DocumentName}, p. {page}]");
            context.Append(string.Join(' ', sourceLabels));
            if (chunk.ParentHeaders.Length > 0)
                context.Append($" Section: {string.Join(" > ", chunk.ParentHeaders)}");
            context.AppendLine($" {chunk.Text}");
        }
        var selectedManuals = selectedDocumentIds is null
            ? string.Empty
            : selectedDocumentIds.Count == 0
                ? "No manuals are selected. Do not search manuals; tell the user to select one to get a manual-based answer."
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
    IChatRiskClassifier riskClassifier,
    ChatToolsService tools,
    IChatConversationContext conversationContext,
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
        IReadOnlyCollection<Guid>? selectedDocumentIds = null,
        Guid? conversationId = null,
        string? anonymousAccessKey = null,
        int contextExchangeCount = ChatHistoryService.DefaultContextExchangeCount)
    {
        await accountGuard.EnsureEnabledAsync(ct);
        if (contextExchangeCount is < ChatHistoryService.MinContextExchangeCount or
            > ChatHistoryService.MaxContextExchangeCount)
            throw new ArgumentOutOfRangeException(
                nameof(contextExchangeCount),
                $"Context exchange count must be between {ChatHistoryService.MinContextExchangeCount} and " +
                $"{ChatHistoryService.MaxContextExchangeCount}.");

        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Ask a question first.", nameof(question));
        var normalizedQuestion = question.Trim();
        if (normalizedQuestion.Length > chatOptions.Value.MaxQuestionLength)
            throw new ArgumentException(
                $"Question cannot exceed {chatOptions.Value.MaxQuestionLength} characters.",
                nameof(question));
        var selectedIds = selectedDocumentIds?.Distinct().ToArray();
        if (selectedIds is { Length: 0 })
            selectedIds = null;
        if (selectedIds is { Length: > 0 })
            await tools.ValidateSelectedManualsAsync(selectedIds, ct);
        var key = currentUser.UserId ?? httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        rateLimiter.EnsureAllowed(key);
        ChatRiskClassificationResult risk;
        try
        {
            risk = await riskClassifier.ClassifyAsync(normalizedQuestion, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await errorRecorder.RecordAsync(
                "Chat risk classifier", "Classify user question", ex, cancellationToken: ct);
            return (
                new ChatResponse([], [], ChatMessages.RiskCheckUnavailable(
                    System.Globalization.CultureInfo.CurrentUICulture.Name)),
                null);
        }

        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (!risk.IsAllowed)
        {
            var message = risk.IsPromptInjection
                ? ChatMessages.PromptInjectionBlocked(culture)
                : ChatMessages.QuestionOutOfScope(culture);
            return (new ChatResponse([], [], message), null);
        }

        var conversationHistory = conversationId is Guid id
            ? await conversationContext.GetRecentHistoryForModelAsync(
                id, anonymousAccessKey, ct, contextExchangeCount)
            : [];
        var prompt = GroundedPrompt.Build(normalizedQuestion, [], selectedIds);
        var context = new List<RetrievedChunk>();
        var citations = new List<ChatCitation>();
        var response = new ChatResponse(context, citations);
        return (response, StreamToolsAsync(
            prompt, context, citations, selectedIds, conversationHistory, ct));
    }

    private async IAsyncEnumerable<string> StreamToolsAsync(
        string prompt,
        List<RetrievedChunk> context,
        List<ChatCitation> citations,
        IReadOnlyCollection<Guid>? selectedDocumentIds,
        IReadOnlyList<ChatHistoryMessage> conversationHistory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rounds = new List<ChatToolRound>();
        var callsMade = 0;
        while (true)
        {
            IReadOnlyList<ChatToolCall>? requestedCalls = null;
            await foreach (var update in StreamProviderUpdatesAsync(
                               prompt, ChatToolDefinitions.All, rounds, conversationHistory, ct).WithCancellation(ct))
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
                    if (!context.Any(existing =>
                            chunk.ChunkId != Guid.Empty
                                ? existing.ChunkId == chunk.ChunkId
                                : existing.DocumentId == chunk.DocumentId &&
                                  existing.PageNumbers.SequenceEqual(chunk.PageNumbers) &&
                                  existing.Text == chunk.Text))
                        context.Add(chunk);
                }
            }

            rounds.Add(new ChatToolRound(requestedCalls, results));
            var citationsForContext = context
                .SelectMany(chunk => (chunk.PageNumbers.Length == 0
                        ? [chunk.PageNumber]
                        : chunk.PageNumbers)
                    .Where(page => page > 0)
                    .Select(page => new ChatCitation(chunk.DocumentId, chunk.DocumentName, page)))
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
                                   prompt, [], rounds, conversationHistory, ct).WithCancellation(ct))
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
        IReadOnlyList<ChatHistoryMessage> conversationHistory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerable<ChatProviderUpdate> stream;
        try
        {
            stream = provider.StreamWithToolsAsync(
                prompt, definitions, rounds, conversationHistory, ct);
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
