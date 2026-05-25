using Microsoft.EntityFrameworkCore;
using Shared.Configurations;
using Shared.Models;

namespace ApiGateway.Data;

/// <summary>
/// ApiGateway-specific DbContext with Outbox support.
/// Applies shared entity configurations and defines DbSets for this service.
/// </summary>
public class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options) { }

    public DbSet<DocumentDto> Documents => Set<DocumentDto>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxConfiguration());
    }
}