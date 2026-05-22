using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Models;

namespace Shared.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<DocumentDto>
{
    public void Configure(EntityTypeBuilder<DocumentDto> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Filename).HasMaxLength(512).IsRequired();
        builder.Property(e => e.Status).HasConversion<int>().IsRequired();
        builder.Property(e => e.FilePath).HasMaxLength(1024).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
    }
}