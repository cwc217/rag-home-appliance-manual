using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using System.ComponentModel.DataAnnotations.Schema;

namespace HomeApplianceManualRag.Web.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public const int EmbeddingDimensions = 1536;

    public DbSet<ManualDocument> ManualDocuments => Set<ManualDocument>();
    public DbSet<ManualChunk> ManualChunks => Set<ManualChunk>();
    public DbSet<IndexingJob> IndexingJobs => Set<IndexingJob>();
    public DbSet<StorageCleanup> StorageCleanups => Set<StorageCleanup>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        if (Database.IsNpgsql())
        {
            builder.HasPostgresExtension("vector");
            builder.Entity<ManualChunk>().Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
        }
        builder.Entity<ManualDocument>().HasIndex(x => new { x.Status, x.OwnerUserId });
        builder.Entity<ManualChunk>().HasIndex(x => x.DocumentId);
        builder.Entity<IndexingJob>().HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        builder.Entity<Conversation>().HasIndex(x => x.UserId);
        builder.Entity<Conversation>().HasIndex(x => x.AnonymousAccessKeyHash);
        builder.Entity<ChatMessage>().HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
        builder.Entity<Conversation>()
            .HasMany(x => x.Messages)
            .WithOne(x => x.Conversation)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<IndexingJob>()
            .HasOne(x => x.Document)
            .WithMany()
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        if (!Database.IsNpgsql())
            builder.Entity<ManualChunk>().Ignore(x => x.Embedding);
    }

}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public string? UserId { get; set; }
    public string? AnonymousAccessKeyHash { get; set; }
    public string Title { get; set; } = "New chat";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<ChatMessage> Messages { get; set; } = [];
}

public enum ChatMessageRole { User, Assistant }

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public ChatMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? CitationsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Conversation? Conversation { get; set; }
}

public enum DocumentStatus { Pending, Processing, Ready, Failed }

public enum IndexingJobStatus { Pending, Processing, Completed, Failed }

public sealed class IndexingJob
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DocumentVersion { get; set; }
    public IndexingJobStatus Status { get; set; } = IndexingJobStatus.Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public ManualDocument? Document { get; set; }
}

public enum StorageCleanupStatus { Pending, Completed }

public sealed class StorageCleanup
{
    public Guid Id { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public StorageCleanupStatus Status { get; set; } = StorageCleanupStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
}

public sealed class ManualDocument
{
    public Guid Id { get; set; }
    public string? OwnerUserId { get; set; }
    [NotMapped]
    public string? OwnerEmail { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long SizeBytes { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAtUtc { get; set; }
    public Guid DocumentVersion { get; set; } = Guid.NewGuid();
    public ICollection<ManualChunk> Chunks { get; set; } = [];
}

public sealed class ManualChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkOrdinal { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public Vector? Embedding { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ManualDocument? Document { get; set; }
}
