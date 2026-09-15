using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Tests;

public sealed class IndexingTests
{
    [Fact]
    public void Chunker_preserves_page_and_heading_provenance()
    {
        var chunker = new DocumentChunker(Options.Create(new RetrievalOptions
        {
            ChunkSize = 40,
            ChunkOverlap = 5
        }));
        var parsed = new ParsedDocument(
        [
            new ParsedPage(3,
            [
                new ParsedBlock("Safety", "heading", 0, "Safety"),
                new ParsedBlock("Disconnect power before cleaning.", "paragraph", 1, "Safety")
            ])
        ], "fixture");

        var chunks = chunker.Chunk(parsed);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal(3, chunk.PageNumber));
        Assert.Contains(chunks, chunk => chunk.Text.Contains("Safety", StringComparison.Ordinal));
        Assert.All(chunks, chunk => Assert.Equal("fixture", chunk.ParserVersion));
    }

    [Fact]
    public void Chunker_returns_no_chunks_for_textless_pages()
    {
        var chunker = new DocumentChunker(Options.Create(new RetrievalOptions()));
        var parsed = new ParsedDocument(
        [
            new ParsedPage(1, [new ParsedBlock(" ", "image", 0)])
        ], "fixture");

        Assert.Empty(chunker.Chunk(parsed));
    }

    [Fact]
    public async Task Pipeline_persists_chunks_and_completes_job()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        var pipeline = Pipeline(db, new FixedParser(), new FixedEmbedding(1536));

        await pipeline.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(DocumentStatus.Ready, document.Status);
        Assert.Equal(IndexingJobStatus.Completed, job.Status);
        Assert.Single(db.ManualChunks);
    }

    [Fact]
    public async Task Pipeline_rejects_embedding_dimension_mismatch()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        var pipeline = Pipeline(db, new FixedParser(), new FixedEmbedding(3));

        await Assert.ThrowsAsync<DocumentParseException>(() => pipeline.ProcessAsync(job.Id, CancellationToken.None));

        Assert.Empty(db.ManualChunks);
        Assert.Equal(DocumentStatus.Processing, document.Status);
    }

    [Fact]
    public async Task Pipeline_rejects_a_stale_document_version_before_publishing_chunks()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        document.DocumentVersion = Guid.NewGuid();
        await db.SaveChangesAsync();
        var pipeline = Pipeline(db, new FixedParser(), new FixedEmbedding(1536));

        await Assert.ThrowsAsync<DocumentParseException>(() => pipeline.ProcessAsync(job.Id, CancellationToken.None));

        Assert.Empty(db.ManualChunks);
    }

    private static ApplicationDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ManualDocument NewDocument() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "manual.pdf",
        StorageKey = "manual.pdf",
        ContentType = "application/pdf",
        DocumentVersion = Guid.NewGuid()
    };

    private static IndexingJob NewJob(ManualDocument document) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = document.Id,
        DocumentVersion = document.DocumentVersion,
        Status = IndexingJobStatus.Processing
    };

    private static IndexingPipeline Pipeline(
        ApplicationDbContext db,
        IDocumentParser parser,
        IEmbeddingProvider embedding) =>
        new(db, new TestStorage(), parser,
            new DocumentChunker(Options.Create(new RetrievalOptions { ChunkSize = 100, ChunkOverlap = 10 })),
            embedding, NullLogger<IndexingPipeline>.Instance);

    private sealed class FixedParser : IDocumentParser
    {
        public Task<ParsedDocument> ParseAsync(DocumentParseRequest request) =>
            Task.FromResult(new ParsedDocument(
                [new ParsedPage(2, [new ParsedBlock("Keep this text.", "paragraph", 0, "Instructions")])],
                "test-parser"));
    }

    private sealed class FixedEmbedding(int dimensions) : IEmbeddingProvider
    {
        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(Enumerable.Repeat(0.1f, dimensions).ToArray());
    }

    private sealed class TestStorage : IFileStorage
    {
        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<StoredFile>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<StoredFile>>([]);
    }
}
