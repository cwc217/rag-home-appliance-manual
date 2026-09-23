using HomeApplianceManualRag.Web.Configuration;
using HomeApplianceManualRag.Web.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using HomeApplianceManualRag.Web.Data;
using Microsoft.EntityFrameworkCore;

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

    public sealed class DocumentAuthorizationTests
    {
        [Fact]
        public async Task Normal_users_only_list_documents_they_own()
        {
            await using var db = CreateDb();
            db.ManualDocuments.AddRange(
                Document("mine", "user-1"),
                Document("other", "user-2"),
                Document("public", null));
            await db.SaveChangesAsync();

            var service = Service(db, new TestCurrentUser("user-1", false));
            var result = await service.ListAsync();

            var document = Assert.Single(result);
            Assert.Equal("mine", document.DisplayName);
        }

        [Fact]
        public async Task Listing_refresh_reads_status_updated_by_worker_context()
        {
            await using var db = CreateDb();
            var document = Document("manual", "user-1");
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            Assert.Equal(DocumentStatus.Pending, (await service.ListAsync())[0].Status);

            document.Status = DocumentStatus.Failed;
            document.LastError = "Docling executable 'docling' is unavailable.";
            await db.SaveChangesAsync();

            var refreshed = Assert.Single(await service.ListAsync());
            Assert.Equal(DocumentStatus.Failed, refreshed.Status);
            Assert.Contains("unavailable", refreshed.LastError);
        }

        [Fact]
        public async Task Progress_listing_returns_requested_terminal_transition()
        {
            await using var db = CreateDb();
            var document = Document("manual", "user-1");
            db.ManualDocuments.AddRange(document, Document("other", "user-1"));
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));
            document.Status = DocumentStatus.Ready;
            await db.SaveChangesAsync();

            var result = await service.ListProgressAsync([document.Id]);

            var refreshed = Assert.Single(result);
            Assert.Equal(DocumentStatus.Ready, refreshed.Status);
        }

        [Fact]
        public async Task Progress_listing_enforces_normal_user_ownership()
        {
            await using var db = CreateDb();
            var mine = Document("mine", "user-1");
            var other = Document("other", "user-2");
            db.ManualDocuments.AddRange(mine, other);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            var result = await service.ListProgressAsync([mine.Id, other.Id]);

            var returned = Assert.Single(result);
            Assert.Equal(mine.Id, returned.Id);
        }

        [Fact]
        public async Task Admins_list_every_document_and_can_retry_failed_documents()
        {
            await using var db = CreateDb();
            var failed = Document("failed", "user-1");
            failed.Status = DocumentStatus.Failed;
            db.ManualDocuments.AddRange(failed, Document("public", null));
            await db.SaveChangesAsync();

            var service = Service(db, new TestCurrentUser("admin", true));
            Assert.Equal(2, (await service.ListAsync()).Count);
            await service.RetryAsync(failed.Id);

            Assert.Equal(DocumentStatus.Pending, (await db.ManualDocuments.FindAsync(failed.Id))!.Status);
        }

        [Fact]
        public async Task Deleted_documents_are_hidden_from_users_but_visible_to_admins()
        {
            await using var db = CreateDb();
            var active = Document("active", "user-1");
            var deleted = Document("deleted", "user-1");
            deleted.DeletedAtUtc = DateTime.UtcNow;
            db.ManualDocuments.AddRange(active, deleted);
            await db.SaveChangesAsync();

            var userDocuments = await Service(db, new TestCurrentUser("user-1", false)).ListAsync();
            var adminDocuments = await Service(db, new TestCurrentUser("admin", true)).ListAsync();

            Assert.Single(userDocuments);
            Assert.Equal(active.Id, userDocuments[0].Id);
            Assert.Equal(2, adminDocuments.Count);
            Assert.Contains(adminDocuments, document => document.Id == deleted.Id);
        }

        [Fact]
        public async Task Normal_users_cannot_retry_another_owners_document()
        {
            await using var db = CreateDb();
            var failed = Document("other", "user-2");
            failed.Status = DocumentStatus.Failed;
            db.ManualDocuments.Add(failed);
            await db.SaveChangesAsync();

            var service = Service(db, new TestCurrentUser("user-1", false));
            await Assert.ThrowsAsync<DocumentForbiddenException>(() => service.RetryAsync(failed.Id));
        }

        [Fact]
        public async Task Delete_soft_deletes_document_without_removing_pdf()
        {
            await using var db = CreateDb();
            var document = Document("to-delete", "user-1");
            document.Status = DocumentStatus.Ready;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var storage = new RecordingStorage();
            var service = new DocumentService(db, new TestCurrentUser("user-1", false),
                new PdfValidator(Options.Create(new UploadOptions())), storage, new FakeActiveAccountGuard());

            await service.DeleteAsync(document.Id);

            var deleted = await db.ManualDocuments.FindAsync(document.Id);
            Assert.NotNull(deleted);
            Assert.NotNull(deleted.DeletedAtUtc);
            Assert.Equal("user-1", deleted.DeletedByUserId);
            Assert.Empty(db.StorageCleanups);
            Assert.Empty(storage.DeletedKeys);
        }

        [Fact]
        public async Task Delete_uses_the_latest_document_status_when_context_is_stale()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            await using var db = new ApplicationDbContext(options);
            var document = Document("just-indexed", "user-1");
            document.Status = DocumentStatus.Pending;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();

            await using (var workerDb = new ApplicationDbContext(options))
            {
                var indexed = await workerDb.ManualDocuments.SingleAsync(x => x.Id == document.Id);
                indexed.Status = DocumentStatus.Ready;
                await workerDb.SaveChangesAsync();
            }

            var service = Service(db, new TestCurrentUser("user-1", false));
            await service.DeleteAsync(document.Id);

            Assert.NotNull(document.DeletedAtUtc);
            Assert.Equal(DocumentStatus.Ready, document.Status);
        }

        [Fact]
        public async Task Admin_can_restore_and_hard_delete_soft_deleted_document()
        {
            await using var db = CreateDb();
            var document = Document("to-delete", "user-1");
            document.Status = DocumentStatus.Ready;
            document.DeletedAtUtc = DateTime.UtcNow;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var storage = new RecordingStorage();
            var service = new DocumentService(db, new TestCurrentUser("admin", true),
                new PdfValidator(Options.Create(new UploadOptions())), storage, new FakeActiveAccountGuard());

            await service.RestoreAsync(document.Id);
            Assert.Null((await db.ManualDocuments.FindAsync(document.Id))!.DeletedAtUtc);

            await service.DeleteAsync(document.Id);
            await service.HardDeleteAsync(document.Id);

            Assert.Null(await db.ManualDocuments.FindAsync(document.Id));
            Assert.Equal(document.StorageKey, Assert.Single(storage.DeletedKeys));
            Assert.Equal(StorageCleanupStatus.Completed, Assert.Single(db.StorageCleanups).Status);
        }

        [Fact]
        public async Task Non_admin_cannot_restore_or_hard_delete_document()
        {
            await using var db = CreateDb();
            var document = Document("deleted", "user-1");
            document.DeletedAtUtc = DateTime.UtcNow;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var service = new DocumentService(db, new TestCurrentUser("user-1", false),
                new PdfValidator(Options.Create(new UploadOptions())), new RecordingStorage(), new FakeActiveAccountGuard());

            await Assert.ThrowsAsync<DocumentForbiddenException>(() => service.RestoreAsync(document.Id));
            await Assert.ThrowsAsync<DocumentForbiddenException>(() => service.HardDeleteAsync(document.Id));
        }

        [Fact]
        public async Task Admin_hard_delete_requires_soft_deleted_document()
        {
            await using var db = CreateDb();
            var document = Document("active", "user-1");
            document.Status = DocumentStatus.Ready;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("admin", true));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.HardDeleteAsync(document.Id));
        }

        [Fact]
        public async Task Delete_rejects_a_document_while_it_is_processing()
        {
            await using var db = CreateDb();
            var document = Document("processing", "user-1");
            document.Status = DocumentStatus.Processing;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            await Assert.ThrowsAsync<DocumentProcessingInProgressException>(() =>
                service.DeleteAsync(document.Id));

            Assert.NotNull(await db.ManualDocuments.FindAsync(document.Id));
        }

        [Fact]
        public async Task Admin_reindex_rejects_a_pending_document()
        {
            await using var db = CreateDb();
            var document = Document("pending", "user-1");
            document.Status = DocumentStatus.Pending;
            db.ManualDocuments.Add(document);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("admin", true));

            await Assert.ThrowsAsync<DocumentProcessingInProgressException>(() =>
                service.ReindexAsync(document.Id));

            Assert.Equal(DocumentStatus.Pending, (await db.ManualDocuments.FindAsync(document.Id))!.Status);
        }

        [Fact]
        public async Task Upload_creates_a_pending_indexing_job()
        {
            await using var db = CreateDb();
            var service = Service(db, new TestCurrentUser("admin", true));
            var file = File("manual.pdf", "application/pdf", "%PDF-1.7\ncontent"u8.ToArray());

            var document = await service.UploadAsync(file);

            var job = Assert.Single(db.IndexingJobs);
            Assert.Equal(document.Id, job.DocumentId);
            Assert.Equal(document.DocumentVersion, job.DocumentVersion);
            Assert.Equal(IndexingJobStatus.Pending, job.Status);
            Assert.Equal(DocumentProcessingStage.Queued, document.ProcessingStage);
        }

        [Fact]
        public async Task Upload_accepts_browser_file_from_interactive_component()
        {
            await using var db = CreateDb();
            var service = Service(db, new TestCurrentUser("admin", true));
            var file = new BrowserFile("manual.pdf", "application/pdf", "%PDF-1.7\ncontent"u8.ToArray());

            var document = await service.UploadAsync(file);

            Assert.Equal("manual.pdf", document.DisplayName);
            Assert.Equal(DocumentStatus.Pending, document.Status);
            Assert.Equal(DocumentProcessingStage.Queued, document.ProcessingStage);
            Assert.Single(db.IndexingJobs);
        }

        [Fact]
        public async Task Browser_upload_reports_bytes_read_while_saving_the_file()
        {
            await using var db = CreateDb();
            var storage = new CopyingStorage();
            var service = new DocumentService(
                db,
                new TestCurrentUser("admin", true),
                new PdfValidator(Options.Create(new UploadOptions())),
                storage,
                new FakeActiveAccountGuard());
            var file = new BrowserFile("manual.pdf", "application/pdf", "%PDF-1.7\ncontent"u8.ToArray());
            var progress = new RecordingProgress();

            await service.UploadAsync(file, byteProgress: progress);

            Assert.NotEmpty(progress.Values);
            Assert.Equal(file.Size, progress.Values[^1]);
            Assert.Equal(file.Size, storage.BytesCopied);
        }

        [Fact]
        public async Task Normal_user_cannot_upload_while_another_document_is_progressing()
        {
            await using var db = CreateDb();
            db.ManualDocuments.Add(Document("in-progress", "user-1"));
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            await Assert.ThrowsAsync<DocumentProcessingInProgressException>(() =>
                service.UploadAsync(File("second.pdf", "application/pdf", "%PDF-1.7\ncontent"u8.ToArray())));
        }

        [Fact]
        public async Task Normal_user_can_retry_when_no_other_document_is_progressing()
        {
            await using var db = CreateDb();
            var failed = Document("failed", "user-1");
            failed.Status = DocumentStatus.Failed;
            failed.ProcessingStage = DocumentProcessingStage.Failed;
            failed.ProgressCompletedUnits = 2;
            failed.ProgressTotalUnits = 5;
            db.ManualDocuments.Add(failed);
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            await service.RetryAsync(failed.Id);

            Assert.Equal(DocumentStatus.Pending, failed.Status);
            Assert.Equal(DocumentProcessingStage.Queued, failed.ProcessingStage);
            Assert.Null(failed.ProgressCompletedUnits);
            Assert.Null(failed.ProgressTotalUnits);
        }

        [Fact]
        public async Task Normal_user_cannot_retry_while_another_document_is_progressing()
        {
            await using var db = CreateDb();
            var failed = Document("failed", "user-1");
            failed.Status = DocumentStatus.Failed;
            db.ManualDocuments.AddRange(failed, Document("in-progress", "user-1"));
            await db.SaveChangesAsync();
            var service = Service(db, new TestCurrentUser("user-1", false));

            await Assert.ThrowsAsync<DocumentProcessingInProgressException>(() => service.RetryAsync(failed.Id));
        }

        private static ApplicationDbContext CreateDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static ManualDocument Document(string name, string? owner) => new()
        {
            Id = Guid.NewGuid(), DisplayName = name, OwnerUserId = owner,
            StorageKey = $"{name}.pdf", CreatedAtUtc = DateTime.UtcNow
        };

        private static DocumentService Service(ApplicationDbContext db, ICurrentUser user) =>
            new(db, user, new PdfValidator(Options.Create(new UploadOptions())), new NoopStorage(),
                new FakeActiveAccountGuard());

        private sealed class TestCurrentUser(string? id, bool admin) : ICurrentUser
        {
            public string? UserId => id;
            public string? Email => null;
            public bool IsAuthenticated => id is not null;
            public bool IsInRole(string role) => admin && role == ApplicationRoles.Admin;
        }

        private class NoopStorage : IFileStorage
        {
            public virtual Task StoreAsync(string key, Stream content, CancellationToken ct = default) => Task.CompletedTask;
            public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
            public virtual Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
            public Task<IReadOnlyCollection<StoredFile>> ListAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyCollection<StoredFile>>([]);
        }

        private sealed class RecordingStorage : NoopStorage
        {
            public List<string> DeletedKeys { get; } = [];
            public override Task DeleteAsync(string key, CancellationToken ct = default)
            {
                DeletedKeys.Add(key);
                return Task.CompletedTask;
            }
        }

        private sealed class CopyingStorage : NoopStorage
        {
            public long BytesCopied { get; private set; }

            public override async Task StoreAsync(string key, Stream content, CancellationToken ct = default)
            {
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, ct);
                BytesCopied = copy.Length;
            }
        }

        private sealed class RecordingProgress : IProgress<long>
        {
            public List<long> Values { get; } = [];
            public void Report(long value) => Values.Add(value);
        }

        private sealed class BrowserFile(string name, string contentType, byte[] content) : IBrowserFile
        {
            public string Name => name;
            public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
            public long Size => content.LongLength;
            public string ContentType => contentType;
            public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
                new MemoryStream(content, writable: false);
        }
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
