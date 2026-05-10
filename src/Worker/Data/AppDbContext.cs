using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Worker.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DocumentDto> Documents => Set<DocumentDto>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Status is stored as INTEGER (matching document_statuses lookup table).
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

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.ToTable("processed_messages");
            entity.HasKey(e => e.MessageId);
            entity.HasIndex(e => e.MessageId);
        });
    }
}