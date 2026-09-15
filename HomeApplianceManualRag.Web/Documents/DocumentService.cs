using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components.Forms;

namespace HomeApplianceManualRag.Web.Documents;

public sealed class DocumentQuotaExceededException() : InvalidOperationException("You can have at most 3 active documents.");
public sealed class DocumentForbiddenException() : UnauthorizedAccessException("You are not allowed to manage this document.");

public sealed class DocumentService(
    ApplicationDbContext db,
    ICurrentUser currentUser,
    PdfValidator validator,
    IFileStorage storage)
{
    private static readonly DocumentStatus[] ActiveStatuses = [DocumentStatus.Pending, DocumentStatus.Processing, DocumentStatus.Ready];

    public async Task<ManualDocument> UploadAsync(IFormFile file, CancellationToken cancellationToken = default)
        => await UploadAsync(new PdfUpload(file.FileName, file.ContentType, file.Length, () => file.OpenReadStream()), cancellationToken);

    public async Task<ManualDocument> UploadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
        => await UploadAsync(new PdfUpload(file.Name, file.ContentType, file.Size, () => file.OpenReadStream(file.Size)), cancellationToken);

    private async Task<ManualDocument> UploadAsync(PdfUpload file, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(file, cancellationToken);
        if (!validation.IsValid) throw new InvalidDataException(validation.Error);
        var isAdmin = currentUser.IsInRole(ApplicationRoles.Admin);
        var owner = isAdmin ? null : currentUser.UserId ?? throw new DocumentForbiddenException();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        if (!isAdmin && await db.ManualDocuments.CountAsync(x => x.OwnerUserId == owner && ActiveStatuses.Contains(x.Status), cancellationToken) >= 3)
            throw new DocumentQuotaExceededException();
        var key = $"{Guid.NewGuid():N}.pdf";
        var document = new ManualDocument { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = Path.GetFileName(file.FileName), StorageKey = key, ContentType = "application/pdf", SizeBytes = validation.SizeBytes, Status = DocumentStatus.Pending, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        await using var stream = file.OpenRead();
        await storage.StoreAsync(key, stream, cancellationToken);
        db.ManualDocuments.Add(document);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return document;
    }

    public Task<List<ManualDocument>> ListAsync(CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) throw new DocumentForbiddenException();
        var isAdmin = currentUser.IsInRole(ApplicationRoles.Admin);
        var owner = currentUser.UserId;
        return (isAdmin ? db.ManualDocuments : db.ManualDocuments.Where(x => x.OwnerUserId == owner))
        .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetAuthorizedAsync(id, ct);
        db.ManualDocuments.Remove(document);
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(document.StorageKey, ct);
    }

    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetAuthorizedAsync(id, ct);
        if (document.Status != DocumentStatus.Failed) throw new InvalidOperationException("Only failed documents can be retried.");
        document.Status = DocumentStatus.Pending; document.LastError = null; document.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReindexAsync(Guid id, CancellationToken ct = default)
    {
        if (!currentUser.IsInRole(ApplicationRoles.Admin)) throw new DocumentForbiddenException();
        var document = await GetAuthorizedAsync(id, ct);
        document.Status = DocumentStatus.Pending;
        document.LastError = null;
        document.IndexedAtUtc = null;
        document.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ManualDocument> GetAuthorizedAsync(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated) throw new DocumentForbiddenException();
        var document = await db.ManualDocuments.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Document was not found.");
        if (!currentUser.IsInRole(ApplicationRoles.Admin) &&
            !(currentUser.IsAuthenticated && document.OwnerUserId == currentUser.UserId))
            throw new DocumentForbiddenException();
        return document;
    }
}
