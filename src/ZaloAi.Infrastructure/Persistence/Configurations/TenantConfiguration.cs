using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name).HasMaxLength(200);
        builder.Property(t => t.IndustrySlug).HasMaxLength(50);
        builder.Property(t => t.BotName).HasMaxLength(100);
        builder.Property(t => t.BotPronoun).HasMaxLength(20);
        builder.Property(t => t.PrivacyUrl).HasMaxLength(500);
        builder.Property(t => t.Plan).HasMaxLength(30);
        builder.Property(t => t.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<TenantStatus>>();
        builder.Property(t => t.BotTone).HasMaxLength(20).HasConversion<LowercaseEnumConverter<BotTone>>().HasDefaultValue(BotTone.Friendly).HasSentinel((BotTone)(-1));
        builder.Property(t => t.BotInstructions).HasMaxLength(1000);
    }
}
