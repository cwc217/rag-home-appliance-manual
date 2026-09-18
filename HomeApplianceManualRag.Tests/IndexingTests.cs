using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using HomeApplianceManualRag.Web.Documents;
using HomeApplianceManualRag.Web.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
    public async Task Docling_parser_maps_page_and_block_provenance()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "status": "success",
              "document": {
                "json_content": {
                  "version": "1.8.0",
                  "texts": [
                    {
                      "label": "section_header",
                      "text": "Safety",
                      "prov": [{ "page_no": 2 }]
                    },
                    {
                      "label": "text",
                      "text": "Disconnect power before cleaning.",
                      "prov": [{ "page_no": 2 }]
                    }
                  ],
                  "tables": [
                    {
                      "label": "table",
                      "prov": [{ "page_no": 3 }]
                    }
                  ]
                }
              },
              "errors": []
            }
            """))
        {
            BaseAddress = new Uri("http://docling:5001/")
        };
        var parser = new DoclingHttpParser(
            client,
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001" }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));

        Assert.Equal("docling-1.8.0", result.ParserVersion);
        Assert.Equal([2, 3], result.Pages.Select(page => page.PageNumber).ToArray());
        Assert.Equal("Safety", result.Pages[0].Blocks[0].HeadingPath);
        Assert.Equal("page:2", result.Pages[0].Blocks[1].SourceReference);
        Assert.Equal("[table]", result.Pages[1].Blocks[0].Text);
    }

    [Fact]
    public async Task Docling_parser_rejects_failed_response()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "status": "failure",
              "document": {},
              "errors": ["unsupported PDF"]
            }
            """))
        {
            BaseAddress = new Uri("http://docling:5001/")
        };
        var parser = new DoclingHttpParser(
            client,
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001" }));

        var exception = await Assert.ThrowsAsync<DocumentParseException>(() =>
            parser.ParseAsync(new(Guid.NewGuid(), "manual.pdf", new MemoryStream([1]))));

        Assert.Contains("unsupported PDF", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
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
            embedding,
            new DocumentProgressNotifier(NullLogger<DocumentProgressNotifier>.Instance),
            NullLogger<IndexingPipeline>.Instance);

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

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/v1/convert/file", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(JsonDocument.Parse(response).RootElement)
            });
        }
    }
}
