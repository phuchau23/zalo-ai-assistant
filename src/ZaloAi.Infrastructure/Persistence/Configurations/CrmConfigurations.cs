using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

// CRM C1 (docs/CRM-PLAN.md): chi nhánh, danh mục bán, nhân viên + ca làm, cài đặt CRM. Mọi bảng có tenant_id + FK cascade về tenants.

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Code).HasMaxLength(50);
        builder.Property(b => b.Name).HasMaxLength(200);
        builder.Property(b => b.Address).HasMaxLength(500);
        builder.Property(b => b.Phone).HasMaxLength(30);
        builder.Property(b => b.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<BranchStatus>>();
        builder.HasIndex(b => new { b.TenantId, b.Code }).IsUnique();
        builder.HasIndex(b => b.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BranchHourConfiguration : IEntityTypeConfiguration<BranchHour>
{
    public void Configure(EntityTypeBuilder<BranchHour> builder)
    {
        builder.ToTable("branch_hours");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.HasIndex(h => new { h.TenantId, h.BranchId, h.DayOfWeek });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(h => h.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BranchClosureConfiguration : IEntityTypeConfiguration<BranchClosure>
{
    public void Configure(EntityTypeBuilder<BranchClosure> builder)
    {
        builder.ToTable("branch_closures");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Reason).HasMaxLength(200);
        builder.HasIndex(c => new { c.TenantId, c.BranchId, c.Date }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(c => c.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItem>
{
    public void Configure(EntityTypeBuilder<CatalogItem> builder)
    {
        builder.ToTable("catalog_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<CatalogKind>>();
        builder.Property(i => i.Code).HasMaxLength(50);
        builder.Property(i => i.Name).HasMaxLength(200);
        builder.Property(i => i.GroupName).HasMaxLength(100);
        builder.Property(i => i.PriceNote).HasMaxLength(500);
        builder.Property(i => i.Sku).HasMaxLength(100);
        builder.Property(i => i.Unit).HasMaxLength(50);
        builder.HasIndex(i => new { i.TenantId, i.Code }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.Kind, i.IsActive });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeItem>().WithMany().HasForeignKey(i => i.KnowledgeItemId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CatalogItemBranchConfiguration : IEntityTypeConfiguration<CatalogItemBranch>
{
    public void Configure(EntityTypeBuilder<CatalogItemBranch> builder)
    {
        builder.ToTable("catalog_item_branches");
        builder.HasKey(x => new { x.CatalogItemId, x.BranchId });
        builder.HasIndex(x => x.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PackageContentConfiguration : IEntityTypeConfiguration<PackageContent>
{
    public void Configure(EntityTypeBuilder<PackageContent> builder)
    {
        builder.ToTable("package_contents");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasIndex(p => new { p.TenantId, p.PackageId, p.ServiceId }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(p => p.PackageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(p => p.ServiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("staff_members");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.DisplayName).HasMaxLength(200);
        builder.Property(s => s.Title).HasMaxLength(100);
        builder.Property(s => s.Color).HasMaxLength(9);
        builder.Property(s => s.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<StaffStatus>>();
        builder.HasIndex(s => new { s.TenantId, s.BranchId });
        builder.HasIndex(s => new { s.TenantId, s.UserId }).IsUnique().HasFilter("user_id IS NOT NULL");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class StaffSkillConfiguration : IEntityTypeConfiguration<StaffSkill>
{
    public void Configure(EntityTypeBuilder<StaffSkill> builder)
    {
        builder.ToTable("staff_services");
        builder.HasKey(x => new { x.StaffMemberId, x.CatalogItemId });
        builder.HasIndex(x => x.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(x => x.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StaffShiftTemplateConfiguration : IEntityTypeConfiguration<StaffShiftTemplate>
{
    public void Configure(EntityTypeBuilder<StaffShiftTemplate> builder)
    {
        builder.ToTable("staff_shift_templates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasIndex(t => new { t.TenantId, t.StaffMemberId, t.DayOfWeek });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(t => t.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StaffShiftConfiguration : IEntityTypeConfiguration<StaffShift>
{
    public void Configure(EntityTypeBuilder<StaffShift> builder)
    {
        builder.ToTable("staff_shifts");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Source).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ShiftSource>>();
        builder.HasIndex(s => new { s.TenantId, s.StaffMemberId, s.Date });
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.Date });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(s => s.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StaffTimeOffConfiguration : IEntityTypeConfiguration<StaffTimeOff>
{
    public void Configure(EntityTypeBuilder<StaffTimeOff> builder)
    {
        builder.ToTable("staff_time_off");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Reason).HasMaxLength(200);
        builder.HasIndex(t => new { t.TenantId, t.StaffMemberId, t.FromAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(t => t.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CrmSettingsConfiguration : IEntityTypeConfiguration<CrmSettings>
{
    public void Configure(EntityTypeBuilder<CrmSettings> builder)
    {
        builder.ToTable("crm_settings");
        builder.HasKey(s => s.TenantId);
        builder.Property(s => s.TimeZone).HasMaxLength(64);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
