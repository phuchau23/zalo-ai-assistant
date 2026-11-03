using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

// CRM C2 (docs/CRM-PLAN.md): tài nguyên, lịch hẹn, nhật ký lịch, lịch định kỳ, hàng chờ.

internal sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("resources");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(100);
        builder.Property(r => r.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ResourceKind>>();
        builder.HasIndex(r => new { r.TenantId, r.BranchId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(r => r.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<AppointmentStatus>>();
        builder.Property(a => a.Source).HasMaxLength(20).HasConversion<LowercaseEnumConverter<AppointmentSource>>();
        builder.Property(a => a.CancelReason).HasMaxLength(500);
        builder.Property(a => a.CancelledBy).HasMaxLength(20);
        builder.Ignore(a => a.IsActive);
        builder.HasIndex(a => new { a.TenantId, a.BranchId, a.StartAt });
        builder.HasIndex(a => new { a.TenantId, a.StaffMemberId, a.StartAt });
        builder.HasIndex(a => new { a.TenantId, a.ContactId, a.StartAt });
        builder.HasIndex(a => new { a.TenantId, a.Status, a.StartAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => a.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(a => a.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(a => a.StaffMemberId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Resource>().WithMany().HasForeignKey(a => a.ResourceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppointmentSeries>().WithMany().HasForeignKey(a => a.SeriesId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.BookedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AppointmentItemConfiguration : IEntityTypeConfiguration<AppointmentItem>
{
    public void Configure(EntityTypeBuilder<AppointmentItem> builder)
    {
        builder.ToTable("appointment_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Name).HasMaxLength(200);
        builder.HasIndex(i => new { i.TenantId, i.AppointmentId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Appointment>().WithMany().HasForeignKey(i => i.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(i => i.CatalogItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppointmentEventConfiguration : IEntityTypeConfiguration<AppointmentEvent>
{
    public void Configure(EntityTypeBuilder<AppointmentEvent> builder)
    {
        builder.ToTable("appointment_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Kind).HasMaxLength(30);
        builder.Property(e => e.Actor).HasMaxLength(20);
        builder.Property(e => e.DataJson).HasColumnName("data").HasColumnType("jsonb");
        builder.HasIndex(e => new { e.TenantId, e.AppointmentId, e.At });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Appointment>().WithMany().HasForeignKey(e => e.AppointmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AppointmentSeriesConfiguration : IEntityTypeConfiguration<AppointmentSeries>
{
    public void Configure(EntityTypeBuilder<AppointmentSeries> builder)
    {
        builder.ToTable("appointment_series");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => new { s.TenantId, s.ContactId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(s => s.ContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<WaitlistStatus>>();
        builder.HasIndex(w => new { w.TenantId, w.BranchId, w.Status, w.PreferredFrom });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(w => w.BranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(w => w.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(w => w.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(w => w.StaffMemberId).OnDelete(DeleteBehavior.SetNull);
    }
}
