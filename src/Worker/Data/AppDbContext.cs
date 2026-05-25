using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Worker.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DocumentDto> Documents => Set<DocumentDto>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<WorkflowCheckpoint> WorkflowCheckpoints => Set<WorkflowCheckpoint>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

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

        // ── Workflow Checkpoints ──
        // Stores agent execution state for durable workflows.
        // If a worker crashes, the agent resumes from the last checkpoint.
        modelBuilder.Entity<WorkflowCheckpoint>(entity =>
        {
            entity.ToTable("workflow_checkpoints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.CurrentActivity).HasMaxLength(128).IsRequired();
            entity.Property(e => e.StateData).HasColumnType("text");
            entity.Property(e => e.ErrorMessage).HasMaxLength(4096);
            entity.HasIndex(e => new { e.AgentName, e.DocumentId });
            entity.HasIndex(e => e.IsCompleted);
        });

        // ── Agent Definitions ──
        // Registry of available agents for dynamic discovery.
        modelBuilder.Entity<AgentDefinition>(entity =>
        {
            entity.ToTable("agent_definitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(128).IsRequired();
            entity.Property(e => e.HandlerType).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Activities).HasColumnType("jsonb");
            entity.HasIndex(e => e.Name).IsUnique();
        });
    }
}
