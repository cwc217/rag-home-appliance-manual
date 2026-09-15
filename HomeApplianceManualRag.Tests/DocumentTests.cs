using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HomeApplianceManualRag.Tests;

public sealed class DocumentValidationTests
{
    private static IFormFile File(string name, string type, byte[] bytes)
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = type };

    [Fact]
    public async Task Accepts_pdf_at_exactly_ten_megabytes_when_signature_is_present()
    {
        var bytes = new byte[10 * 1024 * 1024];
        "%PDF-"u8.CopyTo(bytes);
        var result = await new PdfValidator(Options.Create(new UploadOptions())).ValidateAsync(File("manual.PDF", "application/pdf", bytes));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("manual.txt", "application/pdf")]
    [InlineData("manual.pdf", "application/octet-stream")]
    public async Task Rejects_wrong_extension_or_content_type(string name, string type)
    {
        var result = await new PdfValidator(Options.Create(new UploadOptions())).ValidateAsync(File(name, type, "%PDF-1.7"u8.ToArray()));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Rejects_missing_pdf_signature()
    {
        var result = await new PdfValidator(Options.Create(new UploadOptions())).ValidateAsync(File("manual.pdf", "application/pdf", "not a pdf"u8.ToArray()));
        Assert.False(result.IsValid);
        Assert.Contains("%PDF-", result.Error);
    }

    [Fact]
    public async Task Local_storage_rejects_path_traversal()
    {
        var storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { RootPath = "App_Data/test-uploads" }));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StoreAsync("../outside.pdf", new MemoryStream()));
    }
}
