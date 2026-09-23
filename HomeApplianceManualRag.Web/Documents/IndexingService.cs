using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Providers;
using HomeApplianceManualRag.Web.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace HomeApplianceManualRag.Web.Documents;

public sealed class DocumentChunker(IOptions<RetrievalOptions> options)
{
    public IReadOnlyList<ChunkDraft> Chunk(ParsedDocument document)
    {
        var size = options.Value.ChunkSize;
        var overlap = Math.Min(options.Value.ChunkOverlap, size - 1);
        var drafts = new List<ChunkDraft>();
        var ordinal = 0;
        foreach (var page in document.Pages)
        {
            var text = string.Join("\n\n", page.Blocks
                .OrderBy(x => x.Order)
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => string.IsNullOrWhiteSpace(x.HeadingPath) ? x.Text.Trim() : $"{x.HeadingPath}\n{x.Text.Trim()}"));
            for (var start = 0; start < text.Length; start += Math.Max(1, size - overlap))
            {
                var length = Math.Min(size, text.Length - start);
                if (length == 0) break;
                drafts.Add(new ChunkDraft(ordinal++, page.PageNumber, text.Substring(start, length), document.ParserVersion));
                if (start + length >= text.Length) break;
            }
        }
        return drafts;
    }
}

public sealed record ChunkDraft(int Ordinal, int PageNumber, string Text, string ParserVersion);

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
        var document = await db.ManualDocuments.SingleOrDefaultAsync(x => x.Id == job.DocumentId, cancellationToken)
            ?? throw new DocumentParseException("Document was deleted before indexing.");
        if (document.DeletedAtUtc is not null)
            throw new DocumentParseException("Indexing job belongs to a deleted document.");
        if (document.DocumentVersion != job.DocumentVersion)
            throw new DocumentParseException("Indexing job is stale and was superseded.");

        document.Status = DocumentStatus.Processing;
        document.LastError = null;
        await ReportProgressAsync(document, DocumentProcessingStage.ExtractingText, cancellationToken);

        await using var content = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
        var parsed = await parser.ParseAsync(new DocumentParseRequest(document.Id, document.DisplayName, content, cancellationToken));
        await ReportProgressAsync(document, DocumentProcessingStage.VerifyingManual, cancellationToken);
        var classification = await classifier.ClassifyAsync(document.DisplayName, parsed, cancellationToken);
        if (!classification.IsManual ||
            classification.Confidence < typeSafeOptions.Value.MinimumConfidence)
            throw new DocumentRejectedException(
                "This file does not appear to be a household appliance manual.");
        await ReportProgressAsync(document, DocumentProcessingStage.AnalyzingImages, cancellationToken);
        parsed = await AnalyzeImagesAsync(parsed, cancellationToken);
        await ReportProgressAsync(document, DocumentProcessingStage.ExtractingMetadata, cancellationToken);
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
        await ReportProgressAsync(document, DocumentProcessingStage.PreparingChunks, cancellationToken);
        var drafts = chunker.Chunk(parsed);
        if (drafts.Count == 0)
            throw new DocumentParseException("The document has no extractable text.");

        var vectors = new List<ManualChunk>(drafts.Count);
        await ReportProgressAsync(
            document,
            DocumentProcessingStage.CreatingEmbeddings,
            cancellationToken,
            completedUnits: 0,
            totalUnits: drafts.Count);
        var progressInterval = Math.Max(1, (drafts.Count + 19) / 20);
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            var values = await embedding.EmbedAsync(draft.Text, cancellationToken);
            if (values.Length != ApplicationDbContext.EmbeddingDimensions)
                throw new DocumentParseException(
                    $"Embedding dimension {values.Length} does not match {ApplicationDbContext.EmbeddingDimensions}.");
            vectors.Add(new ManualChunk
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                ChunkOrdinal = draft.Ordinal,
                PageNumber = draft.PageNumber,
                Text = draft.Text,
                Embedding = new Vector(values.ToArray()),
                CreatedAtUtc = DateTime.UtcNow
            });
            var completedUnits = index + 1;
            if (completedUnits % progressInterval == 0 || completedUnits == drafts.Count)
            {
                await ReportProgressAsync(
                    document,
                    DocumentProcessingStage.CreatingEmbeddings,
                    cancellationToken,
                    completedUnits,
                    drafts.Count);
            }
        }

        await ReportProgressAsync(
            document,
            DocumentProcessingStage.SavingIndex,
            cancellationToken,
            drafts.Count,
            drafts.Count);
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
        db.ManualChunks.AddRange(vectors);
        document.Status = DocumentStatus.Ready;
        document.ProcessingStage = DocumentProcessingStage.Completed;
        document.ProgressCompletedUnits = drafts.Count;
        document.ProgressTotalUnits = drafts.Count;
        document.IndexedAtUtc = DateTime.UtcNow;
        document.UpdatedAtUtc = DateTime.UtcNow;
        document.LastError = null;
        job.Status = IndexingJobStatus.Completed;
        job.CompletedAtUtc = DateTime.UtcNow;
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
        logger.LogInformation("Indexed document {DocumentId} with {ChunkCount} chunks.", document.Id, vectors.Count);
    }

    private async Task ReportProgressAsync(
        ManualDocument document,
        DocumentProcessingStage stage,
        CancellationToken cancellationToken,
        int? completedUnits = null,
        int? totalUnits = null)
    {
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
                var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
                await pipeline.ProcessAsync(job.Id, ct);
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
        job.Status = retryable && job.Attempts < 3 ? IndexingJobStatus.Pending : IndexingJobStatus.Failed;
        job.LastError = exception.Message;
        job.NextAttemptAtUtc = job.Status == IndexingJobStatus.Pending ? DateTime.UtcNow.AddMinutes(Math.Pow(2, job.Attempts)) : null;
        if (job.Document is not null &&
            job.Document.DeletedAtUtc is null &&
            job.Document.DocumentVersion == job.DocumentVersion)
        {
            job.Document.Status = DocumentStatus.Failed;
            job.Document.ProcessingStage = DocumentProcessingStage.Failed;
            job.Document.ProgressCompletedUnits = null;
            job.Document.ProgressTotalUnits = null;
            job.Document.LastError = exception.Message;
            job.Document.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
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
                job.Status == IndexingJobStatus.Pending ? "Warning" : "Error",
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
        job.Status = IndexingJobStatus.Failed;
        job.LastError = exception.Message;
        job.NextAttemptAtUtc = null;
        if (job.Document is not null &&
            job.Document.DeletedAtUtc is null &&
            job.Document.DocumentVersion == job.DocumentVersion)
        {
            job.Document.Status = DocumentStatus.Rejected;
            job.Document.ProcessingStage = DocumentProcessingStage.Failed;
            job.Document.ProgressCompletedUnits = null;
            job.Document.ProgressTotalUnits = null;
            job.Document.LastError = exception.Message;
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
        logger.LogInformation("Indexing job {JobId} rejected because the document is not a supported manual.", jobId);
    }
}
