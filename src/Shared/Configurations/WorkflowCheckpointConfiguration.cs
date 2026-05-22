using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Models;

namespace Shared.Configurations;

public class WorkflowCheckpointConfiguration : IEntityTypeConfiguration<WorkflowCheckpoint>
{
    public void Configure(EntityTypeBuilder<WorkflowCheckpoint> builder)
    {
        builder.ToTable("workflow_checkpoints");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AgentName).HasMaxLength(128).IsRequired();
        builder.Property(e => e.CurrentActivity).HasMaxLength(128).IsRequired();
        builder.Property(e => e.StateData).HasColumnType("text");
        builder.Property(e => e.ErrorMessage).HasMaxLength(4096);
        builder.HasIndex(e => new { e.AgentName, e.DocumentId });
        builder.HasIndex(e => e.IsCompleted);
    }
}