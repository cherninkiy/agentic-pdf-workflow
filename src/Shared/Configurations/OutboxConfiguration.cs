using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Models;

namespace Shared.Configurations;

public class OutboxConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.DocumentId).IsRequired();
        builder.Property(e => e.MessagePayload).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.HasIndex(e => e.ProcessedAt).HasFilter("\"processed_at\" IS NULL");
    }
}