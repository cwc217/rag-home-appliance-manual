using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Components.Forms;

namespace HomeApplianceManualRag.Web.Documents;

public sealed class DocumentQuotaExceededException() : InvalidOperationException("You can have at most 3 active documents.");
public sealed class DocumentProcessingInProgressException() : InvalidOperationException("You can only process one document at a time. Wait for the current document to finish before trying again.");
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
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;
        if (!isAdmin && await HasInProgressDocumentAsync(owner!, cancellationToken))
            throw new DocumentProcessingInProgressException();
        if (!isAdmin && await db.ManualDocuments.CountAsync(x => x.OwnerUserId == owner && ActiveStatuses.Contains(x.Status), cancellationToken) >= 3)
            throw new DocumentQuotaExceededException();
        var key = $"{Guid.NewGuid():N}.pdf";
        var document = new ManualDocument { Id = Guid.NewGuid(), OwnerUserId = owner, DisplayName = Path.GetFileName(file.FileName), StorageKey = key, ContentType = "application/pdf", SizeBytes = validation.SizeBytes, Status = DocumentStatus.Pending, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        await using var stream = file.OpenRead();
        var storageWriteStarted = false;
        try
        {
            storageWriteStarted = true;
            await storage.StoreAsync(key, stream, cancellationToken);
            db.ManualDocuments.Add(document);
            db.IndexingJobs.Add(CreateIndexingJob(document));
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return document;
        }
        catch (Exception databaseException) when (storageWriteStarted)
        {
            try
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Document metadata save failed and uploaded file cleanup also failed.",
                    databaseException,
                    cleanupException);
            }

            throw;
        }
    }

    public Task<List<ManualDocument>> ListAsync(CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated) throw new DocumentForbiddenException();
        var isAdmin = currentUser.IsInRole(ApplicationRoles.Admin);
        var owner = currentUser.UserId;
        return ListCoreAsync(isAdmin, owner, ct);
    }

    private async Task<List<ManualDocument>> ListCoreAsync(bool isAdmin, string? owner, CancellationToken ct)
    {
        var documents = await (isAdmin ? db.ManualDocuments : db.ManualDocuments.Where(x => x.OwnerUserId == owner))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        if (isAdmin)
        {
            var ownerIds = documents.Where(x => x.OwnerUserId is not null).Select(x => x.OwnerUserId!).Distinct().ToList();
            var emails = await db.Users
                .Where(x => ownerIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Email, ct);
            foreach (var document in documents)
                document.OwnerEmail = document.OwnerUserId is not null && emails.TryGetValue(document.OwnerUserId, out var email)
                    ? email
                    : null;
        }
        return documents;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetAuthorizedAsync(id, ct);
        var cleanup = new StorageCleanup
        {
            Id = Guid.NewGuid(),
            StorageKey = document.StorageKey,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow
        };
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        db.ManualDocuments.Remove(document);
        db.StorageCleanups.Add(cleanup);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        await storage.DeleteAsync(document.StorageKey, ct);
        cleanup.Status = StorageCleanupStatus.Completed;
        cleanup.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(ManualDocument Document, Stream Content)> OpenReadAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetAuthorizedAsync(id, ct);
        return (document, await storage.OpenReadAsync(document.StorageKey, ct));
    }

    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        var document = await GetAuthorizedAsync(id, ct);
        if (document.Status != DocumentStatus.Failed) throw new InvalidOperationException("Only failed documents can be retried.");
        if (!currentUser.IsInRole(ApplicationRoles.Admin) &&
            await HasInProgressDocumentAsync(currentUser.UserId!, ct))
            throw new DocumentProcessingInProgressException();
        document.Status = DocumentStatus.Pending;
        document.LastError = null;
        document.DocumentVersion = Guid.NewGuid();
        document.UpdatedAtUtc = DateTime.UtcNow;
        await CancelOutstandingJobsAsync(document.Id, ct);
        db.IndexingJobs.Add(CreateIndexingJob(document));
        await db.SaveChangesAsync(ct);
    }

    public async Task ReindexAsync(Guid id, CancellationToken ct = default)
    {
        if (!currentUser.IsInRole(ApplicationRoles.Admin)) throw new DocumentForbiddenException();
        var document = await GetAuthorizedAsync(id, ct);
        document.Status = DocumentStatus.Pending;
        document.LastError = null;
        document.IndexedAtUtc = null;
        document.DocumentVersion = Guid.NewGuid();
        document.UpdatedAtUtc = DateTime.UtcNow;
        await CancelOutstandingJobsAsync(document.Id, ct);
        db.IndexingJobs.Add(CreateIndexingJob(document));
        await db.SaveChangesAsync(ct);
    }

    private static IndexingJob CreateIndexingJob(ManualDocument document) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = document.Id,
        DocumentVersion = document.DocumentVersion,
        Status = IndexingJobStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
        NextAttemptAtUtc = DateTime.UtcNow
    };

    private async Task CancelOutstandingJobsAsync(Guid documentId, CancellationToken ct)
    {
        var jobs = await db.IndexingJobs
            .Where(x => x.DocumentId == documentId &&
                        (x.Status == IndexingJobStatus.Pending || x.Status == IndexingJobStatus.Processing))
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = IndexingJobStatus.Failed;
            job.LastError = "Superseded by a newer indexing request.";
            job.NextAttemptAtUtc = null;
        }
    }

    private Task<bool> HasInProgressDocumentAsync(string owner, CancellationToken ct) =>
        db.ManualDocuments.AnyAsync(
            x => x.OwnerUserId == owner &&
                 (x.Status == DocumentStatus.Pending || x.Status == DocumentStatus.Processing),
            ct);

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
