using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace ApiGateway.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DocumentDto> Documents => Set<DocumentDto>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Status is stored as INTEGER (matching document_statuses lookup table).
        // C# enum DocumentStatus maps directly to int (0 = Uploaded, 1 = Processing, 2 = Completed, 3 = Failed).
        // No JOIN needed in services — status int is sufficient for all business logic.
        modelBuilder.Entity<DocumentDto>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Filename).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Status)
                .HasConversion<int>()
                .IsRequired();
            entity.Property(e => e.FilePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MessagePayload).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.ProcessedAt).HasFilter("\"processed_at\" IS NULL");
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.ToTable("processed_messages");
            entity.HasKey(e => e.MessageId);
            entity.HasIndex(e => e.MessageId);
        });
    }
}
