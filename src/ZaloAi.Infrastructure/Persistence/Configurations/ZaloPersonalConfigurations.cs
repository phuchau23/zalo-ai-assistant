using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

internal sealed class ZaloPersonalAccountConfiguration : IEntityTypeConfiguration<ZaloPersonalAccount>
{
    public void Configure(EntityTypeBuilder<ZaloPersonalAccount> builder)
    {
        builder.ToTable("zalo_personal_accounts");
        builder.HasKey(a => a.ConnectionId);
        builder.Property(a => a.ConnectionId).ValueGeneratedNever();
        builder.Property(a => a.ListenerState).HasMaxLength(30).HasConversion<LowercaseEnumConverter<ListenerState>>();
        // Mặc định ở DB để nick đã có khi thêm cột nhận giá trị đúng (safe / normal / phanh bật).
        builder.Property(a => a.PacingMode).HasMaxLength(20).HasConversion<LowercaseEnumConverter<PacingMode>>().HasDefaultValue(PacingMode.Safe);
        builder.Property(a => a.OutreachLevel).HasMaxLength(20).HasConversion<LowercaseEnumConverter<OutreachLevel>>().HasDefaultValue(OutreachLevel.Normal);
        builder.Property(a => a.AutoBrake).HasDefaultValue(true).HasSentinel(true); // sentinel true: ghi false vẫn được gửi xuống DB
        builder.Property(a => a.OutreachLevelReason).HasMaxLength(50);

        // Một proxy chỉ gán một nick. Không xóa proxy đang gán (chỉ "ngừng dùng").
        builder.HasOne<Proxy>().WithMany().HasForeignKey(a => a.ProxyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.ProxyId).IsUnique().HasFilter("proxy_id IS NOT NULL");

        // Ngắt kết nối (xóa channel_connections) → xóa luôn phiên đăng nhập.
        builder.HasOne<ChannelConnection>().WithOne().HasForeignKey<ZaloPersonalAccount>(a => a.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.TenantId);
    }
}

internal sealed class ZaloPeerConfiguration : IEntityTypeConfiguration<ZaloPeer>
{
    public void Configure(EntityTypeBuilder<ZaloPeer> builder)
    {
        builder.ToTable("zalo_peers");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.ExternalUserId).HasMaxLength(100);
        builder.Property(p => p.DisplayName).HasMaxLength(200);
        builder.Property(p => p.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<PeerKind>>();

        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(p => p.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(p => p.ContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.DecidedByUserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(p => new { p.ConnectionId, p.ExternalUserId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.ConnectionId, p.Kind });
    }
}

internal sealed class ProxyConfiguration : IEntityTypeConfiguration<Proxy>
{
    public void Configure(EntityTypeBuilder<Proxy> builder)
    {
        // Bảng hệ thống (không tenant): chỉ super admin đọc/ghi qua ProxyPoolService.
        builder.ToTable("proxies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Label).HasMaxLength(100);
        builder.Property(p => p.Fingerprint).HasMaxLength(64);
        builder.Property(p => p.Endpoint).HasMaxLength(255);
        builder.Property(p => p.Country).HasMaxLength(50);
        builder.Property(p => p.ExitIp).HasMaxLength(64);
        builder.Property(p => p.LastError).HasMaxLength(200);
        builder.Property(p => p.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ProxyStatus>>();
        builder.HasIndex(p => p.Fingerprint).IsUnique();
        builder.HasIndex(p => p.Status);
    }
}

internal sealed class OutreachBatchConfiguration : IEntityTypeConfiguration<OutreachBatch>
{
    public void Configure(EntityTypeBuilder<OutreachBatch> builder)
    {
        builder.ToTable("outreach_batches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Origin).HasMaxLength(20);
        builder.Property(b => b.ReferrerName).HasMaxLength(200);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(b => b.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Contact>().WithMany().HasForeignKey(b => b.ReferrerContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.AttestedByUserId).OnDelete(DeleteBehavior.SetNull); // bằng chứng còn ở audit_logs
        builder.HasIndex(b => new { b.TenantId, b.CreatedAt });
    }
}

internal sealed class OutreachTargetConfiguration : IEntityTypeConfiguration<OutreachTarget>
{
    public void Configure(EntityTypeBuilder<OutreachTarget> builder)
    {
        builder.ToTable("outreach_targets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.State).HasMaxLength(20).HasConversion<LowercaseEnumConverter<OutreachState>>();
        builder.Property(t => t.ZaloUid).HasMaxLength(100);
        builder.Property(t => t.LastError).HasMaxLength(100);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OutreachBatch>().WithMany().HasForeignKey(t => t.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(t => t.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(t => t.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.AssignedUserId).OnDelete(DeleteBehavior.SetNull);

        // Một người chỉ một dòng đang chạy (một nick chăm tại một lúc).
        builder.HasIndex(t => t.ContactId).IsUnique().HasFilter("state IN ('queued','invited','friend','messaged')");
        builder.HasIndex(t => new { t.TenantId, t.State, t.NextActionAt });
        builder.HasIndex(t => new { t.ConnectionId, t.ZaloUid });
        builder.HasIndex(t => t.BatchId);
    }
}

internal sealed class QuickReplyConfiguration : IEntityTypeConfiguration<QuickReply>
{
    public void Configure(EntityTypeBuilder<QuickReply> builder)
    {
        builder.ToTable("quick_replies");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.Shortcut).HasMaxLength(30);
        builder.Property(q => q.Title).HasMaxLength(100);
        builder.Property(q => q.Content).HasMaxLength(2000);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(q => q.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(q => q.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(q => new { q.TenantId, q.Shortcut }).IsUnique();
    }
}
