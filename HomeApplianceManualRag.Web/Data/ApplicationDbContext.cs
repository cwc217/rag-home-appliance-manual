using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace HomeApplianceManualRag.Web.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public const int EmbeddingDimensions = 1536;

    public DbSet<ManualDocument> ManualDocuments => Set<ManualDocument>();
    public DbSet<ManualChunk> ManualChunks => Set<ManualChunk>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("vector");
        builder.Entity<ManualDocument>().HasIndex(x => new { x.Status, x.OwnerUserId });
        builder.Entity<ManualChunk>().HasIndex(x => x.DocumentId);
        builder.Entity<ManualChunk>().Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
    }
}

public enum DocumentStatus { Pending, Processing, Ready, Failed }

public sealed class ManualDocument
{
    public Guid Id { get; set; }
    public string? OwnerUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/pdf";
    public long SizeBytes { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? IndexedAtUtc { get; set; }
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
