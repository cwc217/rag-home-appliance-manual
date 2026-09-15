using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Documents;
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
}
