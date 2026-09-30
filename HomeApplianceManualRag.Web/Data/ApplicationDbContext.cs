using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using NpgsqlTypes;
using System.ComponentModel.DataAnnotations.Schema;
using HomeApplianceManualRag.Web.Activity;

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
    public DbSet<UserActivity> UserActivities => Set<UserActivity>();
    public DbSet<OperationalErrorEvent> OperationalErrorEvents => Set<OperationalErrorEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        if (Database.IsNpgsql())
        {
            builder.HasPostgresExtension("vector");
            builder.Entity<ManualChunk>().Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            builder.Entity<ManualChunk>()
                .Property(x => x.SearchVector)
                .HasColumnType("tsvector")
                .HasComputedColumnSql("""to_tsvector('simple'::regconfig, "SearchText")""", stored: true);
            builder.Entity<ManualChunk>().Property(x => x.ParentHeaders).HasColumnType("text[]");
            builder.Entity<ManualChunk>().Property(x => x.PageNumbers).HasColumnType("integer[]");
            builder.Entity<ManualChunk>().Property(x => x.MetadataJson).HasColumnType("jsonb");
        }
        builder.Entity<ManualDocument>().HasIndex(x => new { x.Status, x.OwnerUserId });
        builder.Entity<ManualDocument>().HasIndex(x => x.DeletedAtUtc);
        builder.Entity<ManualChunk>().HasIndex(x => new { x.DocumentId, x.ChunkOrdinal }).IsUnique();
        builder.Entity<ManualChunk>().HasIndex(x => x.ParentChunkId);
        if (Database.IsNpgsql())
        {
            builder.Entity<ManualChunk>()
                .HasIndex(x => x.SearchVector)
                .HasMethod("gin");
            builder.Entity<ManualChunk>()
                .HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasFilter("\"Embedding\" IS NOT NULL");
        }
        builder.Entity<ManualChunk>().Property(x => x.ChunkType).HasConversion<string>().HasMaxLength(32);
        builder.Entity<ManualChunk>()
            .HasOne(x => x.ParentChunk)
            .WithMany(x => x.Children)
            .HasForeignKey(x => x.ParentChunkId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Entity<IndexingJob>().HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        builder.Entity<Conversation>().HasIndex(x => x.UserId);
        builder.Entity<Conversation>().HasIndex(x => x.AnonymousAccessKeyHash);
        builder.Entity<ChatMessage>().HasIndex(x => new { x.ConversationId, x.CreatedAtUtc });
        builder.Entity<UserActivity>().HasIndex(x => new { x.CreatedAtUtc, x.ActivityType });
        builder.Entity<UserActivity>().HasIndex(x => x.UserId);
        builder.Entity<UserActivity>().HasIndex(x => x.IpAddress);
        builder.Entity<UserActivity>().HasIndex(x => new { x.Path, x.CreatedAtUtc });
        builder.Entity<OperationalErrorEvent>().HasIndex(x => x.LastOccurredAtUtc);
        builder.Entity<OperationalErrorEvent>().HasIndex(x => new { x.Service, x.LastOccurredAtUtc });
        builder.Entity<OperationalErrorEvent>().HasIndex(x => new { x.Fingerprint, x.LastOccurredAtUtc });
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
        {
            builder.Entity<ManualChunk>().Ignore(x => x.Embedding);
            builder.Entity<ManualChunk>().Ignore(x => x.SearchVector);
        }
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
    public DateTime? DeletedAtUtc { get; set; }
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
    public int Sequence { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Conversation? Conversation { get; set; }
}

public enum DocumentStatus { Pending, Processing, Ready, Failed, Rejected, Cancelled }

public enum DocumentProcessingStage
{
    Queued,
    ExtractingText,
    VerifyingManual,
    AnalyzingImages,
    ExtractingMetadata,
    PreparingChunks,
    CreatingEmbeddings,
    SavingIndex,
    Completed,
    Failed,
    Cancelled
}

public enum MetadataExtractionStatus { Pending, Completed, NeedsReview, Failed }
public enum ManualChunkType { Text, Table, TableSummary, ImageDescription, SectionParent }

public enum IndexingJobStatus { Pending, Processing, Completed, Failed, Cancelled }

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
    public DocumentProcessingStage ProcessingStage { get; set; } = DocumentProcessingStage.Queued;
    public int? ProgressCompletedUnits { get; set; }
    public int? ProgressTotalUnits { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedByUserId { get; set; }
    public string? DeletionReason { get; set; }
    public Guid DocumentVersion { get; set; } = Guid.NewGuid();
    public string? ProductName { get; set; }
    public string? ApplianceCategory { get; set; }
    public string? Brand { get; set; }
    public string? ModelNumber { get; set; }
    public string? Language { get; set; }
    public double? MetadataConfidence { get; set; }
    public DateTime? MetadataExtractedAtUtc { get; set; }
    public MetadataExtractionStatus MetadataStatus { get; set; } = MetadataExtractionStatus.Pending;
    public ICollection<ManualChunk> Chunks { get; set; } = [];
}

public sealed class ManualChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ChunkOrdinal { get; set; }
    public int? LegacyPageNumber { get; set; }
    public string? LegacyText { get; set; }
    public int[] PageNumbers { get; set; } = [];
    public string Content { get; set; } = string.Empty;
    public string EmbeddingText { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public NpgsqlTsVector? SearchVector { get; set; }
    public Vector? Embedding { get; set; }
    public string[] ParentHeaders { get; set; } = [];
    public Guid? ParentChunkId { get; set; }
    public ManualChunkType ChunkType { get; set; } = ManualChunkType.Text;
    public string LanguageCode { get; set; } = "und";
    public int TokenCount { get; set; }
    public string ParserVersion { get; set; } = string.Empty;
    public string ChunkerVersion { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ManualDocument? Document { get; set; }
    public ManualChunk? ParentChunk { get; set; }
    public ICollection<ManualChunk> Children { get; set; } = [];

    [NotMapped]
    public int PageNumber
    {
        get => PageNumbers.FirstOrDefault();
        set => PageNumbers = value > 0 ? [value] : [];
    }

    [NotMapped]
    public string Text
    {
        get => Content;
        set => Content = value;
    }
}

public sealed class OperationalErrorEvent
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastOccurredAtUtc { get; set; } = DateTime.UtcNow;
    public int OccurrenceCount { get; set; } = 1;
    public string Fingerprint { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
    public string ExceptionType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? IndexingJobId { get; set; }
    public string? CorrelationId { get; set; }
}
