using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

// CRM C5: hoa hồng, chấm công.

internal sealed class CommissionRuleConfiguration : IEntityTypeConfiguration<CommissionRule>
{
    public void Configure(EntityTypeBuilder<CommissionRule> builder)
    {
        builder.ToTable("commission_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Scope).HasMaxLength(20).HasConversion<LowercaseEnumConverter<CommissionScope>>();
        builder.Property(r => r.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<CommissionKind>>();
        builder.Property(r => r.Note).HasMaxLength(300);
        builder.HasIndex(r => new { r.TenantId, r.StaffMemberId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(r => r.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(r => r.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CommissionEntryConfiguration : IEntityTypeConfiguration<CommissionEntry>
{
    public void Configure(EntityTypeBuilder<CommissionEntry> builder)
    {
        builder.ToTable("commission_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Period).HasMaxLength(7);
        builder.HasIndex(e => new { e.TenantId, e.StaffMemberId, e.Period });
        builder.HasIndex(e => new { e.TenantId, e.InvoiceId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(e => e.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
{
    public void Configure(EntityTypeBuilder<Attendance> builder)
    {
        builder.ToTable("attendance");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Source).HasMaxLength(20);
        builder.Property(a => a.Note).HasMaxLength(300);
        builder.HasIndex(a => new { a.TenantId, a.StaffMemberId, a.ClockInAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(a => a.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}
