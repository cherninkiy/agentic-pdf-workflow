using Microsoft.EntityFrameworkCore;
using Shared.Configurations;
using Shared.Models;

namespace Worker.Data;

/// <summary>
/// Worker-specific DbContext with WorkflowCheckpoint and AgentDefinition support.
/// Applies shared entity configurations and defines DbSets for this service.
/// </summary>
public class WorkerDbContext : DbContext
{
    public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options) { }

    public DbSet<DocumentDto> Documents => Set<DocumentDto>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<WorkflowCheckpoint> WorkflowCheckpoints => Set<WorkflowCheckpoint>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());
        modelBuilder.ApplyConfiguration(new WorkflowCheckpointConfiguration());
        modelBuilder.ApplyConfiguration(new AgentDefinitionConfiguration());
    }
}