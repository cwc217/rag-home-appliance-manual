using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using HomeApplianceManualRag.Web.Configuration;

namespace HomeApplianceManualRag.Web.Documents;

public sealed record PdfValidationResult(bool IsValid, string? Error, long SizeBytes);
public sealed record PdfUpload(string FileName, string ContentType, long Length, Func<Stream> OpenRead);

public sealed class PdfValidator(IOptions<UploadOptions> options)
{
    private static readonly byte[] Signature = "%PDF-"u8.ToArray();

    public Task<PdfValidationResult> ValidateAsync(IFormFile file, CancellationToken cancellationToken = default) =>
        ValidateAsync(new PdfUpload(file.FileName, file.ContentType, file.Length, () => file.OpenReadStream()), cancellationToken);

    public async Task<PdfValidationResult> ValidateAsync(PdfUpload file, CancellationToken cancellationToken = default)
    {
        if (file is null) return new(false, "A file is required.", 0);
        var max = options.Value.MaxFileSizeBytes;
        if (file.Length <= 0 || file.Length > max) return new(false, $"PDF files must be between 1 byte and {max / 1024 / 1024} MB.", file.Length);
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return new(false, "Only .pdf files are accepted.", file.Length);
        if (!string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return new(false, "The declared content type must be application/pdf.", file.Length);
        await using var stream = file.OpenRead();
        var header = new byte[Signature.Length];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read != Signature.Length || !header.AsSpan().SequenceEqual(Signature))
            return new(false, "The file is not a valid PDF (missing %PDF- signature).", file.Length);
        return new(true, null, file.Length);
    }
}
