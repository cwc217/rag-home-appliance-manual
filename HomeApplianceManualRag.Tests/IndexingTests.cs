using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Chat;
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
    public void Cancellation_registry_signals_the_active_job_and_unregisters_it_on_dispose()
    {
        var registry = new IndexingCancellationRegistry();
        var jobId = Guid.NewGuid();
        using var registration = registry.Register(jobId, CancellationToken.None);

        Assert.False(registration.Token.IsCancellationRequested);
        Assert.True(registry.Cancel(jobId));
        Assert.True(registration.Token.IsCancellationRequested);

        registration.Dispose();

        Assert.False(registry.Cancel(jobId));
    }

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
        Assert.Contains(chunks, chunk => chunk.ParentHeaders.Contains("Safety"));
        Assert.Contains(chunks, chunk => chunk.EmbeddingText.Contains("Section: Safety", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Content.Contains("Disconnect power", StringComparison.Ordinal));
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
    public void Chunker_keeps_table_rows_together_repeats_headers_and_preserves_row_spans()
    {
        var chunker = new DocumentChunker(Options.Create(new RetrievalOptions
        {
            MaxChunkTokens = 12,
            MaxParentChunkTokens = 32
        }));
        var table = new ParsedTable(
            4,
            2,
            [
                new ParsedTableCell(0, 0, "Code", IsColumnHeader: true),
                new ParsedTableCell(0, 1, "Cause", IsColumnHeader: true),
                new ParsedTableCell(1, 0, "E:24", RowSpan: 2),
                new ParsedTableCell(1, 1, "Drain pipe blocked"),
                new ParsedTableCell(2, 1, "Drain pipe bent"),
                new ParsedTableCell(3, 0, "E:25"),
                new ParsedTableCell(3, 1, "Pump cover stuck")
            ]);
        var parsed = new ParsedDocument(
        [
            new ParsedPage(31,
            [
                new ParsedBlock("E:18 inlet hose issue", "text", 0),
                new ParsedBlock("[Table]", "table", 1, Table: table)
            ])
        ], "fixture");

        var chunks = chunker.Chunk(parsed);
        var tableChunks = chunks.Where(chunk => chunk.Text.StartsWith("[Table]", StringComparison.Ordinal)).ToArray();

        Assert.Contains(chunks, chunk => chunk.Text.Contains("E:18 inlet hose issue", StringComparison.Ordinal));
        Assert.NotEmpty(tableChunks);
        Assert.All(tableChunks, chunk =>
        {
            Assert.Equal(31, chunk.PageNumber);
            Assert.Contains("Code | Cause", chunk.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("E:18", chunk.Text, StringComparison.Ordinal);
        });
        Assert.Contains(tableChunks, chunk =>
            chunk.Text.Contains("E:24 | Drain pipe blocked", StringComparison.Ordinal));
        Assert.Contains(tableChunks, chunk =>
            chunk.Text.Contains("E:24 | Drain pipe bent", StringComparison.Ordinal));
        Assert.Contains(tableChunks, chunk =>
            chunk.Text.Contains("E:25 | Pump cover stuck", StringComparison.Ordinal));
        Assert.All(tableChunks, chunk =>
            Assert.False(
                chunk.Text.Contains("Drain pipe blocked", StringComparison.Ordinal) &&
                chunk.Text.Contains("Drain pipe bent", StringComparison.Ordinal)));
    }

    [Fact]
    public void Chunker_creates_bounded_section_parents_for_children_across_pages()
    {
        var chunker = new DocumentChunker(Options.Create(new RetrievalOptions
        {
            MaxChunkTokens = 12,
            MaxParentChunkTokens = 1000
        }));
        var parsed = new ParsedDocument(
        [
            new ParsedPage(3,
            [
                new ParsedBlock("Troubleshooting", "heading", 0, HeadingLevel: 1),
                new ParsedBlock("Check the water supply.", "paragraph", 1)
            ]),
            new ParsedPage(4,
            [
                new ParsedBlock("Clean the inlet filter.", "paragraph", 0)
            ])
        ], "fixture");

        var chunks = chunker.Chunk(parsed);
        var parent = Assert.Single(chunks, chunk => chunk.ChunkType == ManualChunkType.SectionParent);
        var children = chunks.Where(chunk => chunk.ChunkType != ManualChunkType.SectionParent).ToArray();

        Assert.True(children.Length >= 2);
        Assert.All(children, child => Assert.True(child.TokenCount <= 12));
        Assert.All(children, child => Assert.Equal(parent.ParentGroupId, child.ParentGroupId));
        Assert.Equal([3, 4], parent.PageNumbers);
        Assert.Contains("Troubleshooting", parent.ParentHeaders);
        Assert.Contains("water", parent.Content, StringComparison.Ordinal);
        Assert.Contains("supply", parent.Content, StringComparison.Ordinal);
        Assert.Contains("inlet", parent.Content, StringComparison.Ordinal);
        Assert.Contains("filter", parent.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Multilingual_analyzer_creates_searchable_cjk_bigrams_and_preserves_model_codes()
    {
        var analyzer = new MultilingualTextAnalyzer();

        var result = analyzer.Analyze("故障代碼 型號", "zh-TW");

        Assert.Equal("zh-TW", result.LanguageCode);
        Assert.Contains("故障", result.SearchText, StringComparison.Ordinal);
        Assert.Contains("代碼", result.SearchText, StringComparison.Ordinal);

        var mixed = analyzer.Analyze("故障代碼 H11 型號 ABC-123", "zh-TW");
        Assert.Equal("mul", mixed.LanguageCode);
        Assert.Contains("h11", mixed.SearchText, StringComparison.Ordinal);
        Assert.Contains("abc123", mixed.SearchText, StringComparison.Ordinal);
        Assert.True(mixed.EstimatedTokenCount > 0);
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
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001", EnableOcrFallback = false }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));

        Assert.Equal("docling-1.8.0", result.ParserVersion);
        Assert.Equal([2, 3], result.Pages.Select(page => page.PageNumber).ToArray());
        Assert.Equal("Safety", result.Pages[0].Blocks[0].HeadingPath);
        Assert.Equal("page:2", result.Pages[0].Blocks[1].SourceReference);
        Assert.Equal("[table]", result.Pages[1].Blocks[0].Text);
    }

    [Fact]
    public async Task Docling_parser_removes_page_furniture_and_header_prefixes()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "status": "success",
              "document": {
                "json_content": {
                  "texts": [
                    { "label": "page_header", "text": "Appliance Guide", "prov": [{ "page_no": 1 }] },
                    { "label": "section_header", "text": "Appliance Guide 1. Safety", "level": 1, "prov": [{ "page_no": 1 }] },
                    { "label": "text", "text": "Appliance Guide\nUnplug before service.", "prov": [{ "page_no": 1 }] },
                    { "label": "page_footer", "text": "Appliance Guide | 1", "prov": [{ "page_no": 1 }] }
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
            Options.Create(new DoclingOptions
            {
                ServiceUrl = "http://docling:5001",
                EnableOcrFallback = false
            }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));
        var blocks = result.Pages.Single().Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Equal("1. Safety", blocks[0].Text);
        Assert.Equal("1. Safety", blocks[0].HeadingPath);
        Assert.Equal("Unplug before service.", blocks[1].Text);
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
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001", EnableOcrFallback = false }));

        var exception = await Assert.ThrowsAsync<DocumentParseException>(() =>
            parser.ParseAsync(new(Guid.NewGuid(), "manual.pdf", new MemoryStream([1]))));

        Assert.Contains("unsupported PDF", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task Docling_parser_retries_text_poor_pdfs_with_ocr()
    {
        var handler = new OcrFallbackStubHandler(
        [
            """
            {"status":"success","document":{"json_content":{"version":"1.8.0","texts":[]}},"errors":[]}
            """,
            """
            {"status":"success","document":{"json_content":{"version":"1.8.0","texts":[
              {"label":"text","text":"OCR extracted operating instructions","prov":[{"page_no":1}]}
            ]}},"errors":[]}
            """
        ]);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docling:5001/")
        };
        var parser = new DoclingHttpParser(
            client,
            Options.Create(new DoclingOptions
            {
                ServiceUrl = "http://docling:5001",
                MinTextCharactersPerPage = 40,
                OcrFallbackPageRatio = 0.5
            }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "scanned-manual.pdf", new MemoryStream([1, 2, 3])));

        Assert.Equal([false, true], handler.OcrFlags);
        Assert.EndsWith("-ocr", result.ParserVersion, StringComparison.Ordinal);
        Assert.Contains("OCR extracted operating instructions", result.Pages.Single().Blocks.Single().Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docling_parser_maps_structured_table_cells_and_removes_duplicate_blocks()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "status": "success",
              "document": {
                "json_content": {
                  "version": "1.8.0",
                  "texts": [
                    { "label": "section_header", "text": "故障檢修", "prov": [{ "page_no": 18 }] },
                    { "label": "section_header", "text": "故障檢修", "prov": [{ "page_no": 18 }] }
                  ],
                  "tables": [
                    {
                      "label": "table",
                      "data": {
                        "num_rows": 3,
                        "num_cols": 2,
                        "table_cells": [
                          { "text": "診斷顯示", "start_row_offset_idx": 0, "start_col_offset_idx": 0, "column_header": true },
                          { "text": "異常/保護控制", "start_row_offset_idx": 0, "start_col_offset_idx": 1, "column_header": true },
                          { "text": "H 11", "start_row_offset_idx": 1, "start_col_offset_idx": 0, "row_span": 2 },
                          { "text": "室內/室外異常通訊", "start_row_offset_idx": 1, "start_col_offset_idx": 1 }
                        ]
                      },
                      "prov": [{ "page_no": 18 }]
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
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001", EnableOcrFallback = false }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));
        var blocks = result.Pages.Single().Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Single(blocks, block => block.Text == "故障檢修");
        Assert.Contains("[Table]", blocks[1].Text, StringComparison.Ordinal);
        Assert.Contains("H 11 | 室內/室外異常通訊", blocks[1].Text, StringComparison.Ordinal);
        var table = Assert.IsType<ParsedTable>(blocks[1].Table);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(2, Assert.Single(table.Cells, cell => cell.Text == "H 11").RowSpan);
        Assert.Equal(2, table.Cells.Count(cell => cell.IsColumnHeader));
    }

    [Fact]
    public async Task Docling_parser_removes_consecutive_duplicate_lines_inside_a_block()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "status": "success",
              "document": {
                "json_content": {
                  "texts": [
                    {
                      "text": "清洗空調器\n清洗空調器\n\n室內機\n室內機\n\n清洗空調器",
                      "prov": [{ "page_no": 15 }]
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
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001", EnableOcrFallback = false }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));

        var text = Assert.Single(result.Pages.Single().Blocks).Text;
        Assert.Equal("清洗空調器\n\n室內機\n\n清洗空調器", text);
    }

    [Fact]
    public async Task Docling_parser_extracts_embedded_picture_data_without_persisting_it()
    {
        var imageData = Convert.ToBase64String([1, 2, 3, 4]);
        using var client = new HttpClient(new StubHandler($$"""
            {
              "status": "success",
              "document": {
                "json_content": {
                  "version": "1.10.0",
                  "texts": [
                    { "label": "text", "text": "Operating instructions", "prov": [{ "page_no": 1 }] }
                  ],
                  "pictures": [
                    {
                      "label": "picture",
                      "image": {
                        "uri": "data:image/png;base64,{{imageData}}",
                        "mimetype": "image/png"
                      },
                      "prov": [{ "page_no": 1 }]
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
            Options.Create(new DoclingOptions { ServiceUrl = "http://docling:5001", EnableOcrFallback = false }));

        var result = await parser.ParseAsync(new(
            Guid.NewGuid(), "manual.pdf", new MemoryStream([1, 2, 3])));

        var picture = Assert.Single(result.Pages.Single().Blocks, block => block.BlockType == "picture");
        Assert.Equal("[picture]", picture.Text);
        Assert.NotNull(picture.Image);
        Assert.Equal("image/png", picture.Image!.MimeType);
        Assert.Equal([1, 2, 3, 4], picture.Image.Data);
    }

    [Fact]
    public async Task TypeSafe_classifier_reads_choice_and_confidence()
    {
        using var client = new HttpClient(new TypeSafeStubHandler("""
            {
              "model": "jev-1.13.0",
              "answers": {
                "document_type": {
                  "type": "choice",
                  "choice": "appliance_manual",
                  "confidence": 0.94,
                  "probabilities": {
                    "appliance_manual": 0.97,
                    "other_document": 0.03
                  }
                }
              },
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """))
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };
        var classifier = new TypeSafeManualClassifier(
            client,
            Options.Create(new TypeSafeOptions { ApiKey = "test-key" }));

        var result = await classifier.ClassifyAsync(
            "washer.pdf",
            new ParsedDocument(
                [new ParsedPage(1, [new ParsedBlock("Operating instructions", "text", 0)])],
                "fixture"));

        Assert.True(result.IsManual);
        Assert.Equal(0.94, result.Confidence);
    }

    [Fact]
    public async Task Metadata_extractor_parses_structured_response_and_prioritizes_first_pages()
    {
        var extractor = new AzureOpenAiManualMetadataExtractor(
            new FixedChatProvider("""
                Here is the result:
                {"productName":"Smart Washer","applianceCategory":"Washing Machine","brand":"Acme","modelNumber":"AC-100","language":"en","confidence":0.93}
                """),
            Options.Create(new MetadataExtractionOptions { MinimumConfidence = 0.85 }),
            NullLogger<AzureOpenAiManualMetadataExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            "washer.pdf",
            new ParsedDocument(
                [
                    new ParsedPage(1, [new ParsedBlock("Acme Smart Washer AC-100", "text", 0)]),
                    new ParsedPage(20, [new ParsedBlock("Operating instructions", "text", 0)])
                ],
                "fixture"));

        Assert.Equal("Acme", result.Brand);
        Assert.Equal("AC-100", result.ModelNumber);
        Assert.Equal(MetadataExtractionStatus.Completed, result.Status);
        Assert.Equal(0.93, result.Confidence);
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
        var chunk = Assert.Single(db.ManualChunks);
        Assert.Equal("Keep this text.", chunk.Content);
        Assert.Contains("Section: Instructions", chunk.EmbeddingText, StringComparison.Ordinal);
        Assert.Equal("test-embedding", chunk.EmbeddingModel);
        Assert.Equal([2], chunk.PageNumbers);
        Assert.Equal("en", chunk.LanguageCode);
    }

    [Fact]
    public async Task Pipeline_publishes_each_processing_stage_and_embedding_count()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        var notifier = new DocumentProgressNotifier(NullLogger<DocumentProgressNotifier>.Instance);
        var updates = new List<DocumentProgressUpdate>();
        using var subscription = notifier.Subscribe(null, true, update =>
        {
            updates.Add(update);
            return Task.CompletedTask;
        });
        var pipeline = Pipeline(
            db,
            new FixedParser(),
            new FixedEmbedding(1536),
            progressNotifier: notifier);

        await pipeline.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(
            [
                DocumentProcessingStage.ExtractingText,
                DocumentProcessingStage.VerifyingManual,
                DocumentProcessingStage.AnalyzingImages,
                DocumentProcessingStage.ExtractingMetadata,
                DocumentProcessingStage.PreparingChunks,
                DocumentProcessingStage.CreatingEmbeddings,
                DocumentProcessingStage.SavingIndex,
                DocumentProcessingStage.Completed
            ],
            updates.Select(update => update.Stage).Distinct().ToArray());
        Assert.Contains(updates, update =>
            update.Stage == DocumentProcessingStage.CreatingEmbeddings &&
            update.CompletedUnits == 0 &&
            update.TotalUnits == 1);
        Assert.Contains(updates, update =>
            update.Stage == DocumentProcessingStage.CreatingEmbeddings &&
            update.CompletedUnits == 1 &&
            update.TotalUnits == 1);
        Assert.Equal(DocumentProcessingStage.Completed, document.ProcessingStage);
        Assert.Equal(1, document.ProgressCompletedUnits);
        Assert.Equal(1, document.ProgressTotalUnits);
    }

    [Fact]
    public async Task Pipeline_analyzes_picture_and_indexes_ocr_and_description()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        var analyzer = new FixedImageAnalyzer(new("Error code H11", "Indoor and outdoor units are not communicating."));
        var pipeline = Pipeline(
            db,
            new PictureParser(),
            new FixedEmbedding(1536),
            imageAnalyzer: analyzer);

        await pipeline.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Contains("Error code H11", db.ManualChunks.Single().Text, StringComparison.Ordinal);
        Assert.Contains("Indoor and outdoor units", db.ManualChunks.Single().Text, StringComparison.Ordinal);
        Assert.Equal(1, analyzer.CallCount);
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
    public async Task Pipeline_rejects_non_manual_before_generating_embeddings()
    {
        await using var db = CreateDb();
        var document = NewDocument();
        var job = NewJob(document);
        db.AddRange(document, job);
        await db.SaveChangesAsync();
        var embedding = new RecordingEmbedding(1536);
        var pipeline = Pipeline(db, new FixedParser(), embedding,
            new FixedClassifier(isManual: false, confidence: 0.99));

        await Assert.ThrowsAsync<DocumentRejectedException>(() =>
            pipeline.ProcessAsync(job.Id, CancellationToken.None));

        Assert.Equal(DocumentStatus.Processing, document.Status);
        Assert.Empty(db.ManualChunks);
        Assert.Equal(0, embedding.CallCount);
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
        IEmbeddingProvider embedding,
        IManualClassifier? classifier = null,
        IImageAnalyzer? imageAnalyzer = null,
        DocumentProgressNotifier? progressNotifier = null) =>
        new(db, new TestStorage(), parser,
            new DocumentChunker(Options.Create(new RetrievalOptions { ChunkSize = 100, ChunkOverlap = 10 })),
            embedding,
            classifier ?? new FixedClassifier(isManual: true, confidence: 1),
            new FixedMetadataExtractor(),
            imageAnalyzer ?? new NoOpImageAnalyzer(),
            Options.Create(new ImageAnalysisOptions { Enabled = true }),
            Options.Create(new TypeSafeOptions()),
            progressNotifier ?? new DocumentProgressNotifier(NullLogger<DocumentProgressNotifier>.Instance),
            NullLogger<IndexingPipeline>.Instance);

    private sealed class FixedParser : IDocumentParser
    {
        public Task<ParsedDocument> ParseAsync(DocumentParseRequest request) =>
            Task.FromResult(new ParsedDocument(
                [new ParsedPage(2, [new ParsedBlock("Keep this text.", "paragraph", 0, "Instructions")])],
                "test-parser"));
    }

    private sealed class PictureParser : IDocumentParser
    {
        public Task<ParsedDocument> ParseAsync(DocumentParseRequest request) =>
            Task.FromResult(new ParsedDocument(
                [new ParsedPage(2, [
                    new ParsedBlock(
                        "[picture]",
                        "picture",
                        0,
                        Image: new ParsedImage([1, 2, 3], "image/png"))
                ])],
                "test-parser"));
    }

    private sealed class FixedImageAnalyzer(ImageAnalysisResult result) : IImageAnalyzer
    {
        public int CallCount { get; private set; }

        public Task<ImageAnalysisResult> AnalyzeAsync(
            ParsedImage image,
            int pageNumber,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedEmbedding(int dimensions) : IEmbeddingProvider
    {
        public string ModelId => "test-embedding";

        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(Enumerable.Repeat(0.1f, dimensions).ToArray());
    }

    private sealed class RecordingEmbedding(int dimensions) : IEmbeddingProvider
    {
        public string ModelId => "test-embedding";
        public int CallCount { get; private set; }

        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ReadOnlyMemory<float>>(Enumerable.Repeat(0.1f, dimensions).ToArray());
        }
    }

    private sealed class FixedClassifier(bool isManual, double confidence) : IManualClassifier
    {
        public Task<ManualClassificationResult> ClassifyAsync(
            string displayName,
            ParsedDocument document,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManualClassificationResult(isManual, confidence));
    }

    private sealed class FixedMetadataExtractor : IManualMetadataExtractor
    {
        public Task<ManualMetadataExtractionResult> ExtractAsync(
            string displayName,
            ParsedDocument document,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManualMetadataExtractionResult(
                "Test appliance",
                "Other",
                "Test brand",
                "TEST-1",
                "en",
                1,
                MetadataExtractionStatus.Completed));
    }

    private sealed class FixedChatProvider(string response) : IChatProvider
    {
        public async IAsyncEnumerable<string> StreamAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return response;
        }

        public async IAsyncEnumerable<ChatProviderUpdate> StreamWithToolsAsync(
            string prompt,
            IReadOnlyList<ChatToolDefinition> tools,
            IReadOnlyList<ChatToolRound> rounds,
            IReadOnlyList<ChatHistoryMessage> conversationHistory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var token in StreamAsync(prompt, cancellationToken))
                yield return new ChatProviderUpdate(Text: token);
        }
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

    private sealed class OcrFallbackStubHandler(IReadOnlyList<string> responses) : HttpMessageHandler
    {
        private int responseIndex;
        public List<bool> OcrFlags { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            OcrFlags.Add(
                body.Contains("name=\"do_ocr\"\r\n\r\ntrue", StringComparison.Ordinal) ||
                body.Contains("name=do_ocr\r\n\r\ntrue", StringComparison.Ordinal));
            var response = responses[responseIndex++];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(JsonDocument.Parse(response).RootElement)
            };
        }
    }

    private sealed class TypeSafeStubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/v1/systemone", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(JsonDocument.Parse(response).RootElement)
            });
        }
    }
}
