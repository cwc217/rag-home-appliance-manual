using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using HomeApplianceManualRag.Web.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomeApplianceManualRag.Web.Documents;

public sealed class DocumentChunker(
    IOptions<RetrievalOptions> options,
    IChunkTextAnalyzer? textAnalyzer = null)
{
    private const string ChunkerVersion = "structure-v2";
    private readonly IChunkTextAnalyzer analyzer = textAnalyzer ?? new MultilingualTextAnalyzer();
    private static readonly Regex SentenceBoundaries = new(
        """(?<=[.!?。！？；;])\s+|[\r\n]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ChunkDraft> Chunk(ParsedDocument document)
    {
        return Chunk(document, null);
    }

    public IReadOnlyList<ChunkDraft> Chunk(ParsedDocument document, string? documentLanguage)
    {
        var maxTokens = options.Value.MaxChunkTokens;
        var drafts = new List<ChunkDraft>();
        var ordinal = 0;
        var headings = new List<(int Level, string Text)>();
        var pending = new List<PendingPart>();
        Guid? currentParentGroupId = null;

        foreach (var page in document.Pages)
        {
            foreach (var block in page.Blocks.OrderBy(x => x.Order))
            {
                if (IsHeading(block))
                {
                    FlushPending();
                    AddHeading(block);
                    continue;
                }

                var parentHeaders = GetParentHeaders(block);
                if (block.Table is not null)
                {
                    FlushPending();
                    AddTableChunks(block, page.PageNumber, parentHeaders);
                    continue;
                }

                if (IsBlockType(block, "picture"))
                {
                    FlushPending();
                    if (HasUsefulImageText(block.Text))
                    {
                        foreach (var piece in SplitIntoPieces(block.Text.Trim(), maxTokens, parentHeaders))
                            AddDraft(piece, [page.PageNumber], parentHeaders,
                                ManualChunkType.ImageDescription, [block], document.ParserVersion);
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(block.Text))
                    AppendText(block, page.PageNumber, parentHeaders);
            }
        }

        FlushPending();
        BuildParentChunks();
        return drafts;

        void AddHeading(ParsedBlock block)
        {
            var text = block.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;
            var level = Math.Max(1, block.HeadingLevel ?? (headings.Count == 0 ? 1 : headings[^1].Level + 1));
            while (headings.Count > 0 && headings[^1].Level >= level)
                headings.RemoveAt(headings.Count - 1);
            headings.Add((level, text));
            currentParentGroupId = Guid.NewGuid();
        }

        string[] GetParentHeaders(ParsedBlock block)
        {
            if (headings.Count > 0)
                return headings.Select(heading => heading.Text).ToArray();
            return string.IsNullOrWhiteSpace(block.HeadingPath) ||
                   string.Equals(block.HeadingPath.Trim(), block.Text.Trim(), StringComparison.Ordinal)
                ? []
                : [block.HeadingPath.Trim()];
        }

        void AppendText(ParsedBlock block, int pageNumber, string[] parentHeaders)
        {
            var text = block.Text.Trim();
            foreach (var piece in SplitIntoPieces(text, maxTokens, parentHeaders))
            {
                var part = new PendingPart(piece, pageNumber, parentHeaders, block, currentParentGroupId);
                if (pending.Count > 0 &&
                    (!pending[^1].ParentHeaders.SequenceEqual(parentHeaders, StringComparer.Ordinal) ||
                     EstimateChunkTokens(
                         string.Join("\n\n", pending.Select(x => x.Text).Append(piece)), parentHeaders) > maxTokens))
                    FlushPending();
                pending.Add(part);
            }
        }

        void FlushPending()
        {
            if (pending.Count == 0)
                return;
            var content = string.Join("\n\n", pending.Select(part => part.Text));
            var pages = pending.Select(part => part.PageNumber).Distinct().Order().ToArray();
            var sourceBlocks = pending.Select(part => part.Block).Distinct().ToArray();
            AddDraft(content, pages, pending[0].ParentHeaders, ManualChunkType.Text, sourceBlocks,
                document.ParserVersion, pending[0].ParentGroupId);
            pending.Clear();
        }

        void AddTableChunks(ParsedBlock block, int pageNumber, string[] parentHeaders)
        {
            var table = block.Table!;
            var rows = BuildTableRows(table);
            if (rows.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(block.Text))
                    AppendText(block, pageNumber, parentHeaders);
                return;
            }

            var headerRows = rows.Where(row => row.IsHeader).Select(row => row.Text).ToArray();
            var dataRows = rows.Where(row => !row.IsHeader).Select(row => row.Text).ToArray();
            var currentLines = new List<string> { "[Table]" };
            currentLines.AddRange(headerRows);
            var rowCount = 0;

            foreach (var row in dataRows)
            {
                if (rowCount > 0 &&
                    EstimateChunkTokens(string.Join('\n', currentLines.Append(row)), parentHeaders) > maxTokens)
                {
                    AddTableDraft();
                    currentLines = ["[Table]", .. headerRows];
                    rowCount = 0;
                }

                currentLines.Add(row);
                rowCount++;
            }

            if (rowCount > 0 || headerRows.Length > 0)
                AddTableDraft();

            void AddTableDraft() =>
                AddDraft(string.Join('\n', currentLines), [pageNumber], parentHeaders,
                    ManualChunkType.Table, [block], document.ParserVersion);
        }

        void AddDraft(
            string content,
            int[] pageNumbers,
            string[] parentHeaders,
            ManualChunkType chunkType,
            IReadOnlyList<ParsedBlock> sourceBlocks,
            string parserVersion,
            Guid? parentGroupId = null)
        {
            var embeddingText = BuildEmbeddingText(parentHeaders, content);
            var analysis = analyzer.Analyze(embeddingText, documentLanguage);
            var metadataJson = BuildMetadata(sourceBlocks);
            drafts.Add(new ChunkDraft(
                ordinal++,
                content,
                embeddingText,
                analysis.SearchText,
                pageNumbers,
                parentHeaders,
                chunkType,
                analysis.LanguageCode,
                analysis.EstimatedTokenCount,
                parserVersion,
                ChunkerVersion,
                metadataJson,
                parentGroupId ?? currentParentGroupId));
        }

        void BuildParentChunks()
        {
            var children = drafts.ToArray();
            foreach (var group in children
                         .Where(draft => draft.ParentGroupId is not null)
                         .GroupBy(draft => draft.ParentGroupId!.Value))
            {
                var batch = new List<ChunkDraft>();
                var batchTokens = 0;
                foreach (var child in group)
                {
                    if (batch.Count > 0 &&
                        batchTokens + child.TokenCount > options.Value.MaxParentChunkTokens)
                        AddParent(batch);
                    batch.Add(child);
                    batchTokens += child.TokenCount;
                }
                AddParent(batch);

                void AddParent(List<ChunkDraft> batchChildren)
                {
                    if (batchChildren.Count == 0)
                        return;
                    if (batchChildren.Count < 2)
                    {
                        var onlyChild = batchChildren[0];
                        var index = drafts.FindIndex(draft => draft.Ordinal == onlyChild.Ordinal);
                        drafts[index] = onlyChild with { ParentGroupId = null };
                        batchChildren.Clear();
                        batchTokens = 0;
                        return;
                    }

                    var groupId = Guid.NewGuid();
                    foreach (var child in batchChildren)
                    {
                        var index = drafts.FindIndex(draft => draft.Ordinal == child.Ordinal);
                        drafts[index] = child with { ParentGroupId = groupId };
                    }
                    var first = batchChildren[0];
                    var content = string.Join("\n\n", batchChildren.Select(child => child.Content));
                    var pages = batchChildren.SelectMany(child => child.PageNumbers).Distinct().Order().ToArray();
                    var tokenCount = EstimateChunkTokens(content, first.ParentHeaders);
                    drafts.Add(new ChunkDraft(
                        ordinal++,
                        content,
                        content,
                        string.Empty,
                        pages,
                        first.ParentHeaders,
                        ManualChunkType.SectionParent,
                        first.LanguageCode,
                        tokenCount,
                        first.ParserVersion,
                        ChunkerVersion,
                        null,
                        groupId));
                    batchChildren.Clear();
                    batchTokens = 0;
                }
            }
        }
    }

    private int EstimateChunkTokens(string content, string[] parentHeaders) =>
        analyzer.Analyze(BuildEmbeddingText(parentHeaders, content)).EstimatedTokenCount;

    private static string BuildEmbeddingText(string[] parentHeaders, string content) =>
        parentHeaders.Length == 0
            ? content
            : $"Section: {string.Join(" > ", parentHeaders)}\nContent:\n{content}";

    private static bool IsHeading(ParsedBlock block) =>
        block.BlockType.Contains("heading", StringComparison.OrdinalIgnoreCase) ||
        block.BlockType.Contains("section_header", StringComparison.OrdinalIgnoreCase) ||
        block.BlockType.Contains("title", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockType(ParsedBlock block, string blockType) =>
        string.Equals(block.BlockType, blockType, StringComparison.OrdinalIgnoreCase);

    private static bool HasUsefulImageText(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !string.Equals(text.Trim(), "[picture]", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> SplitIntoPieces(string text, int maxTokens, string[] parentHeaders)
    {
        var pieces = SentenceBoundaries.Split(text)
            .Select(piece => piece.Trim())
            .Where(piece => !string.IsNullOrWhiteSpace(piece))
            .ToArray();
        var current = new List<string>();

        foreach (var piece in pieces)
        {
            if (EstimateChunkTokens(piece, parentHeaders) > maxTokens)
            {
                foreach (var boundedPiece in SplitOversizedPiece(piece, maxTokens, parentHeaders))
                {
                    if (current.Count > 0)
                    {
                        yield return string.Join(' ', current);
                        current.Clear();
                    }
                    yield return boundedPiece;
                }
                continue;
            }

            if (current.Count > 0 &&
                EstimateChunkTokens(string.Join(' ', current.Append(piece)), parentHeaders) > maxTokens)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }
            current.Add(piece);
        }

        if (current.Count > 0)
            yield return string.Join(' ', current);
    }

    private IEnumerable<string> SplitOversizedPiece(string piece, int maxTokens, string[] parentHeaders)
    {
        var words = piece.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>();
        foreach (var word in words)
        {
            if (EstimateChunkTokens(word, parentHeaders) > maxTokens)
            {
                if (current.Count > 0)
                {
                    yield return string.Join(' ', current);
                    current.Clear();
                }
                var runeChunk = new StringBuilder();
                foreach (var rune in word.EnumerateRunes())
                {
                    var candidate = runeChunk.ToString() + rune;
                    if (runeChunk.Length > 0 && EstimateChunkTokens(candidate, parentHeaders) > maxTokens)
                    {
                        yield return runeChunk.ToString();
                        runeChunk.Clear();
                    }
                    runeChunk.Append(rune);
                }
                if (runeChunk.Length > 0)
                    yield return runeChunk.ToString();
                continue;
            }

            if (current.Count > 0 &&
                EstimateChunkTokens(string.Join(' ', current.Append(word)), parentHeaders) > maxTokens)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }
            current.Add(word);
        }
        if (current.Count > 0)
            yield return string.Join(' ', current);
    }

    private static List<(string Text, bool IsHeader)> BuildTableRows(ParsedTable table)
    {
        var rowCount = Math.Max(
            table.RowCount,
            table.Cells.Count == 0 ? 0 : table.Cells.Max(cell => cell.Row + cell.RowSpan));
        var columnCount = Math.Max(
            table.ColumnCount,
            table.Cells.Count == 0 ? 0 : table.Cells.Max(cell => cell.Column + cell.ColumnSpan));
        if (rowCount <= 0 || columnCount <= 0)
            return [];

        var values = new string?[rowCount, columnCount];
        var headerRows = new HashSet<int>();
        foreach (var cell in table.Cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column))
        {
            if (cell.Row < 0 || cell.Row >= rowCount ||
                cell.Column < 0 || cell.Column >= columnCount)
                continue;
            if (cell.IsColumnHeader)
                headerRows.Add(cell.Row);

            var lastRow = Math.Min(rowCount, cell.Row + cell.RowSpan);
            for (var row = cell.Row; row < lastRow; row++)
                values[row, cell.Column] ??= cell.Text;
        }

        var rows = new List<(string Text, bool IsHeader)>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var cells = Enumerable.Range(0, columnCount)
                .Select(column => values[row, column] ?? string.Empty);
            var text = string.Join(" | ", cells);
            if (!string.IsNullOrWhiteSpace(text.Replace("|", string.Empty, StringComparison.Ordinal)))
                rows.Add((text, headerRows.Contains(row)));
        }
        return rows;
    }

    private static string? BuildMetadata(IReadOnlyList<ParsedBlock> blocks)
    {
        var sources = blocks
            .Where(block => block.ItemReference is not null || block.BoundingBoxJson is not null)
            .Select(block => new
            {
                item = block.ItemReference,
                page = block.SourceReference,
                bbox = block.BoundingBoxJson
            })
            .ToArray();
        return sources.Length == 0
            ? null
            : JsonSerializer.Serialize(new { schemaVersion = 1, sources });
    }

    private sealed record PendingPart(
        string Text,
        int PageNumber,
        string[] ParentHeaders,
        ParsedBlock Block,
        Guid? ParentGroupId);
}

public sealed record ChunkDraft(
    int Ordinal,
    string Content,
    string EmbeddingText,
    string SearchText,
    int[] PageNumbers,
    string[] ParentHeaders,
    ManualChunkType ChunkType,
    string LanguageCode,
    int TokenCount,
    string ParserVersion,
    string ChunkerVersion,
    string? MetadataJson,
    Guid? ParentGroupId = null)
{
    public int PageNumber => PageNumbers.FirstOrDefault();
    public string Text => Content;
}

public sealed class IndexingPipeline(
    ApplicationDbContext db,
    IFileStorage storage,
    IDocumentParser parser,
    DocumentChunker chunker,
    IEmbeddingProvider embedding,
    IManualClassifier classifier,
    IManualMetadataExtractor metadataExtractor,
    IImageAnalyzer imageAnalyzer,
    IOptions<ImageAnalysisOptions> imageAnalysisOptions,
    IOptions<TypeSafeOptions> typeSafeOptions,
    DocumentProgressNotifier progressNotifier,
    ILogger<IndexingPipeline> logger)
{
    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.IndexingJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new DocumentParseException("Indexing job was deleted.");
        if (job.Status == IndexingJobStatus.Cancelled)
            throw new IndexingJobCancelledException();
        var document = await db.ManualDocuments.SingleOrDefaultAsync(x => x.Id == job.DocumentId, cancellationToken)
            ?? throw new DocumentParseException("Document was deleted before indexing.");
        if (document.DeletedAtUtc is not null)
            throw new DocumentParseException("Indexing job belongs to a deleted document.");
        if (document.DocumentVersion != job.DocumentVersion)
            throw new DocumentParseException("Indexing job is stale and was superseded.");

        document.Status = DocumentStatus.Processing;
        document.LastError = null;
        await ReportProgressAsync(job.Id, document, DocumentProcessingStage.ExtractingText, cancellationToken);

        await using var content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        var parsed = await parser.ParseAsync(new DocumentParseRequest(document.Id, document.DisplayName, content, cancellationToken));
        await ReportProgressAsync(job.Id, document, DocumentProcessingStage.VerifyingManual, cancellationToken);
        var classification = await classifier.ClassifyAsync(document.DisplayName, parsed, cancellationToken);
        if (!classification.IsManual ||
            classification.Confidence < typeSafeOptions.Value.MinimumConfidence)
            throw new DocumentRejectedException(
                "This file does not appear to be a household appliance manual.");
        await ReportProgressAsync(job.Id, document, DocumentProcessingStage.AnalyzingImages, cancellationToken);
        parsed = await AnalyzeImagesAsync(parsed, cancellationToken);
        await ReportProgressAsync(job.Id, document, DocumentProcessingStage.ExtractingMetadata, cancellationToken);
        var metadata = await metadataExtractor.ExtractAsync(
            document.DisplayName, parsed, cancellationToken);
        document.ProductName = metadata.ProductName;
        document.ApplianceCategory = metadata.ApplianceCategory;
        document.Brand = metadata.Brand;
        document.ModelNumber = metadata.ModelNumber;
        document.Language = metadata.Language;
        document.MetadataConfidence = metadata.Confidence;
        document.MetadataStatus = metadata.Status;
        document.MetadataExtractedAtUtc = DateTime.UtcNow;
        await ReportProgressAsync(job.Id, document, DocumentProcessingStage.PreparingChunks, cancellationToken);
        var drafts = chunker.Chunk(parsed, document.Language);
        if (drafts.Count == 0)
            throw new DocumentParseException("The document has no extractable text.");

        var searchableDrafts = drafts
            .Where(draft => draft.ChunkType != ManualChunkType.SectionParent)
            .ToArray();
        var parentIds = drafts
            .Where(draft => draft.ChunkType == ManualChunkType.SectionParent)
            .Where(draft => draft.ParentGroupId is not null)
            .ToDictionary(draft => draft.ParentGroupId!.Value, _ => Guid.NewGuid());
        var chunks = new List<ManualChunk>(drafts.Count);
        await ReportProgressAsync(
            job.Id,
            document,
            DocumentProcessingStage.CreatingEmbeddings,
            cancellationToken,
            completedUnits: 0,
            totalUnits: searchableDrafts.Length);
        var progressInterval = Math.Max(1, (searchableDrafts.Length + 19) / 20);
        for (var index = 0; index < searchableDrafts.Length; index++)
        {
            var draft = searchableDrafts[index];
            var values = await embedding.EmbedAsync(draft.EmbeddingText, cancellationToken);
            if (values.Length != ApplicationDbContext.EmbeddingDimensions)
                throw new DocumentParseException(
                    $"Embedding dimension {values.Length} does not match {ApplicationDbContext.EmbeddingDimensions}.");
            chunks.Add(new ManualChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                ChunkOrdinal = draft.Ordinal,
                PageNumbers = draft.PageNumbers,
                Content = draft.Content,
                EmbeddingText = draft.EmbeddingText,
                SearchText = draft.SearchText,
                ParentHeaders = draft.ParentHeaders,
                ChunkType = draft.ChunkType,
                LanguageCode = draft.LanguageCode,
                TokenCount = draft.TokenCount,
                ParserVersion = draft.ParserVersion,
                ChunkerVersion = draft.ChunkerVersion,
                EmbeddingModel = embedding.ModelId,
                MetadataJson = draft.MetadataJson,
                Embedding = new Vector(values.ToArray()),
                ParentChunkId = draft.ParentGroupId is Guid parentGroupId &&
                                parentIds.TryGetValue(parentGroupId, out var parentId)
                    ? parentId
                    : null,
                CreatedAtUtc = DateTime.UtcNow
            });
            var completedUnits = index + 1;
            if (completedUnits % progressInterval == 0 || completedUnits == searchableDrafts.Length)
            {
                await ReportProgressAsync(
                    job.Id,
                    document,
                    DocumentProcessingStage.CreatingEmbeddings,
                    cancellationToken,
                    completedUnits,
                    searchableDrafts.Length);
            }
        }
        foreach (var parent in drafts.Where(draft => draft.ChunkType == ManualChunkType.SectionParent))
        {
            if (parent.ParentGroupId is not Guid parentGroupId ||
                !parentIds.TryGetValue(parentGroupId, out var parentId))
                continue;
            chunks.Add(new ManualChunk
            {
                Id = parentId,
                DocumentId = document.Id,
                ChunkOrdinal = parent.Ordinal,
                PageNumbers = parent.PageNumbers,
                Content = parent.Content,
                EmbeddingText = parent.EmbeddingText,
                SearchText = string.Empty,
                ParentHeaders = parent.ParentHeaders,
                ChunkType = parent.ChunkType,
                LanguageCode = parent.LanguageCode,
                TokenCount = parent.TokenCount,
                ParserVersion = parent.ParserVersion,
                ChunkerVersion = parent.ChunkerVersion,
                EmbeddingModel = embedding.ModelId,
                MetadataJson = parent.MetadataJson,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await ReportProgressAsync(
            job.Id,
            document,
            DocumentProcessingStage.SavingIndex,
            cancellationToken,
            searchableDrafts.Length,
            searchableDrafts.Length);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (!await db.ManualDocuments.AnyAsync(
                x => x.Id == job.DocumentId &&
                     x.DeletedAtUtc == null &&
                     x.DocumentVersion == job.DocumentVersion,
                cancellationToken))
            throw new DocumentParseException("Indexing job is stale and was superseded.");
        var existingChunks = await db.ManualChunks
            .Where(x => x.DocumentId == document.Id)
            .ToListAsync(cancellationToken);
        db.ManualChunks.RemoveRange(existingChunks);
        db.ManualChunks.AddRange(chunks);
        document.Status = DocumentStatus.Ready;
        document.ProcessingStage = DocumentProcessingStage.Completed;
        document.ProgressCompletedUnits = searchableDrafts.Length;
        document.ProgressTotalUnits = searchableDrafts.Length;
        document.IndexedAtUtc = DateTime.UtcNow;
        document.UpdatedAtUtc = DateTime.UtcNow;
        document.LastError = null;
        if (db.Database.IsRelational())
        {
            var completed = await db.IndexingJobs
                .Where(x => x.Id == job.Id && x.Status == IndexingJobStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, IndexingJobStatus.Completed)
                    .SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow), cancellationToken);
            if (completed == 0)
                throw new IndexingJobCancelledException();
        }
        else
        {
            if (job.Status != IndexingJobStatus.Processing)
                throw new IndexingJobCancelledException();
            job.Status = IndexingJobStatus.Completed;
            job.CompletedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        await progressNotifier.PublishAsync(new(
            document.Id,
            document.OwnerUserId,
            document.Status,
            document.LastError,
            document.ProcessingStage,
            document.ProgressCompletedUnits,
            document.ProgressTotalUnits));
        logger.LogInformation("Indexed document {DocumentId} with {ChunkCount} searchable chunks and {ParentCount} parent chunks.",
            document.Id, searchableDrafts.Length, chunks.Count - searchableDrafts.Length);
    }

    private async Task ReportProgressAsync(
        Guid jobId,
        ManualDocument document,
        DocumentProcessingStage stage,
        CancellationToken cancellationToken,
        int? completedUnits = null,
        int? totalUnits = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await db.IndexingJobs
                .Where(x => x.Id == jobId)
                .Select(x => x.Status == IndexingJobStatus.Cancelled)
                .SingleOrDefaultAsync(cancellationToken))
            throw new IndexingJobCancelledException();
        document.ProcessingStage = stage;
        document.ProgressCompletedUnits = completedUnits;
        document.ProgressTotalUnits = totalUnits;
        document.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await progressNotifier.PublishAsync(new(
            document.Id,
            document.OwnerUserId,
            document.Status,
            document.LastError,
            stage,
            completedUnits,
            totalUnits));
    }

    private async Task<ParsedDocument> AnalyzeImagesAsync(
        ParsedDocument document,
        CancellationToken cancellationToken)
    {
        if (!imageAnalysisOptions.Value.Enabled ||
            imageAnalysisOptions.Value.MaxImagesPerDocument == 0)
            return document;

        var analyzed = 0;
        var pages = new List<ParsedPage>(document.Pages.Count);
        foreach (var page in document.Pages)
        {
            var blocks = new List<ParsedBlock>(page.Blocks.Count);
            foreach (var block in page.Blocks)
            {
                if (block.Image is null || analyzed >= imageAnalysisOptions.Value.MaxImagesPerDocument)
                {
                    blocks.Add(block);
                    continue;
                }

                var result = await imageAnalyzer.AnalyzeAsync(
                    block.Image, page.PageNumber, cancellationToken);
                analyzed++;
                var parts = new[] { result.OcrText, result.Description }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                blocks.Add(parts.Length == 0
                    ? block with { Image = null }
                    : block with
                    {
                        Text = "[Picture]\n" + string.Join("\n", parts),
                        Image = null
                    });
            }
            pages.Add(new ParsedPage(page.PageNumber, blocks));
        }
        return new ParsedDocument(pages, document.ParserVersion);
    }
}

public sealed class DocumentRejectedException(string message) : Exception(message);

public sealed class IndexingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DoclingOptions> options,
    IndexingCancellationRegistry cancellationRegistry,
    DocumentProgressNotifier progressNotifier,
    ILogger<IndexingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Indexing cycle failed."); }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var staleBefore = now - LeaseTimeout;
        var jobs = await db.IndexingJobs
            .Where(x =>
                x.Document != null &&
                x.Document.DeletedAtUtc == null &&
                (x.Status == IndexingJobStatus.Pending &&
                 (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)) ||
                (x.Status == IndexingJobStatus.Processing &&
                 x.StartedAtUtc < staleBefore))
            .OrderBy(x => x.CreatedAtUtc)
            .Take(options.Value.MaxConcurrentJobs)
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            try
            {
                if (!await TryClaimAsync(db, job.Id, ct))
                    continue;
                using var cancellation = cancellationRegistry.Register(job.Id, ct);
                var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
                await pipeline.ProcessAsync(job.Id, cancellation.Token);
            }
            catch (IndexingJobCancelledException)
            {
                if (await IsCancelledAsync(job.Id, ct))
                    await MarkCancelledAsync(job.Id, ct);
            }
            catch (DocumentParseException ex)
            {
                await MarkFailureAsync(job.Id, ex, ex.Retryable, ct);
            }
            catch (DocumentRejectedException ex)
            {
                await MarkRejectedAsync(job.Id, ex, ct);
            }
            catch (ManualClassificationException ex)
            {
                await MarkFailureAsync(job.Id, ex, ex.Retryable, ct);
            }
            catch (ImageAnalysisException ex)
            {
                await MarkFailureAsync(job.Id, ex, ex.Retryable, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                await MarkFailureAsync(job.Id, ex, retryable: true, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (await IsCancelledAsync(job.Id, ct))
                    await MarkCancelledAsync(job.Id, ct);
                else
                    await MarkFailureAsync(job.Id, ex, retryable: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkFailureAsync(job.Id, ex, retryable: true, ct);
            }
        }
    }

    private static async Task<bool> TryClaimAsync(
            ApplicationDbContext db,
            Guid jobId,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var staleBefore = now - LeaseTimeout;
            if (db.Database.IsRelational())
            {
                var updated = await db.IndexingJobs
                    .Where(x => x.Id == jobId &&
                                ((x.Status == IndexingJobStatus.Pending &&
                                  (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)) ||
                                 (x.Status == IndexingJobStatus.Processing &&
                                  x.StartedAtUtc < staleBefore)))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, IndexingJobStatus.Processing)
                        .SetProperty(x => x.StartedAtUtc, now)
                        .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
                return updated == 1;
            }

            var job = await db.IndexingJobs.SingleOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null ||
                (job.Status != IndexingJobStatus.Pending &&
                 (job.Status != IndexingJobStatus.Processing ||
                  job.StartedAtUtc >= staleBefore)))
                return false;
            job.Status = IndexingJobStatus.Processing;
            job.StartedAtUtc = now;
            job.Attempts++;
            await db.SaveChangesAsync(ct);
            return true;
        }

    private async Task MarkFailureAsync(Guid jobId, Exception exception, bool retryable, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.IndexingJobs.Include(x => x.Document).SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null) return;
        if (job.Status == IndexingJobStatus.Cancelled) return;
        var safeErrorMessage = IndexingErrorMessages.ForException(exception);
        var status = retryable && job.Attempts < 3 ? IndexingJobStatus.Pending : IndexingJobStatus.Failed;
        var nextAttempt = status == IndexingJobStatus.Pending
            ? DateTime.UtcNow.AddMinutes(Math.Pow(2, job.Attempts))
            : (DateTime?)null;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (db.Database.IsRelational())
        {
            var updated = await db.IndexingJobs
                .Where(x => x.Id == jobId &&
                            (x.Status == IndexingJobStatus.Pending ||
                             x.Status == IndexingJobStatus.Processing))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.LastError, safeErrorMessage)
                    .SetProperty(x => x.NextAttemptAtUtc, nextAttempt), ct);
            if (updated == 0)
                return;
            db.Entry(job).State = EntityState.Detached;
        }
        else
        {
            job.Status = status;
            job.LastError = safeErrorMessage;
            job.NextAttemptAtUtc = nextAttempt;
        }
        if (job.Document is not null &&
            job.Document.DeletedAtUtc is null &&
            job.Document.DocumentVersion == job.DocumentVersion)
        {
            job.Document.Status = DocumentStatus.Failed;
            job.Document.ProcessingStage = DocumentProcessingStage.Failed;
            job.Document.ProgressCompletedUnits = null;
            job.Document.ProgressTotalUnits = null;
            job.Document.LastError = safeErrorMessage;
            job.Document.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        var service = exception switch
        {
            DocumentParseException => "Docling",
            ManualClassificationException => "Manual classification",
            ImageAnalysisException => "Image analysis",
            _ => "Document indexing"
        };
        await scope.ServiceProvider.GetRequiredService<IOperationalErrorRecorder>()
            .RecordAsync(
                service,
                "Index document",
                exception,
                status == IndexingJobStatus.Pending ? "Warning" : "Error",
                job.DocumentId,
                job.Id,
                ct);
        if (job.Document is not null)
        {
            await progressNotifier.PublishAsync(new(
                job.Document.Id,
                job.Document.OwnerUserId,
                job.Document.Status,
                job.Document.LastError,
                job.Document.ProcessingStage,
                job.Document.ProgressCompletedUnits,
                job.Document.ProgressTotalUnits));
        }
        logger.LogWarning(exception, "Indexing job {JobId} failed on attempt {Attempt}.", jobId, job.Attempts);
    }

    private async Task MarkRejectedAsync(Guid jobId, Exception exception, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.IndexingJobs.Include(x => x.Document).SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null) return;
        if (job.Status == IndexingJobStatus.Cancelled) return;
        var safeErrorMessage = IndexingErrorMessages.ForException(exception);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (db.Database.IsRelational())
        {
            var updated = await db.IndexingJobs
                .Where(x => x.Id == jobId &&
                            (x.Status == IndexingJobStatus.Pending ||
                             x.Status == IndexingJobStatus.Processing))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, IndexingJobStatus.Failed)
                    .SetProperty(x => x.LastError, safeErrorMessage)
                    .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null), ct);
            if (updated == 0)
                return;
            db.Entry(job).State = EntityState.Detached;
        }
        else
        {
            job.Status = IndexingJobStatus.Failed;
            job.LastError = safeErrorMessage;
            job.NextAttemptAtUtc = null;
        }
        if (job.Document is not null &&
            job.Document.DeletedAtUtc is null &&
            job.Document.DocumentVersion == job.DocumentVersion)
        {
            job.Document.Status = DocumentStatus.Rejected;
            job.Document.ProcessingStage = DocumentProcessingStage.Failed;
            job.Document.ProgressCompletedUnits = null;
            job.Document.ProgressTotalUnits = null;
            job.Document.LastError = safeErrorMessage;
            job.Document.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        if (job.Document is not null)
            await progressNotifier.PublishAsync(new(
                job.Document.Id,
                job.Document.OwnerUserId,
                job.Document.Status,
                job.Document.LastError,
                job.Document.ProcessingStage,
                job.Document.ProgressCompletedUnits,
                job.Document.ProgressTotalUnits));
        logger.LogInformation("Indexing job {JobId} rejected because the document is not a supported manual.", jobId);
    }

    private async Task MarkCancelledAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.IndexingJobs.Include(x => x.Document)
            .SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null || job.Status == IndexingJobStatus.Completed)
            return;

        job.Status = IndexingJobStatus.Cancelled;
        job.NextAttemptAtUtc = null;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.LastError = null;
        if (job.Document is not null &&
            job.Document.DeletedAtUtc is null &&
            job.Document.DocumentVersion == job.DocumentVersion)
        {
            job.Document.Status = DocumentStatus.Cancelled;
            job.Document.ProcessingStage = DocumentProcessingStage.Cancelled;
            job.Document.ProgressCompletedUnits = null;
            job.Document.ProgressTotalUnits = null;
            job.Document.LastError = null;
            job.Document.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        if (job.Document is not null)
            await progressNotifier.PublishAsync(new(
                job.Document.Id,
                job.Document.OwnerUserId,
                job.Document.Status,
                job.Document.LastError,
                job.Document.ProcessingStage,
                job.Document.ProgressCompletedUnits,
                job.Document.ProgressTotalUnits));
    }

    private async Task<bool> IsCancelledAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.IndexingJobs
            .Where(x => x.Id == jobId)
            .Select(x => x.Status == IndexingJobStatus.Cancelled)
            .SingleOrDefaultAsync(ct);
    }
}
