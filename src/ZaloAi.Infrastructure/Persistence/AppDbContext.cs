using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;

namespace ZaloAi.Infrastructure.Persistence;

/// <summary>
/// Hai lớp bảo vệ cô lập tenant nằm ở đây:
/// 1. Global query filter: mọi entity <see cref="ITenantOwned"/> (và bảng tenants) chỉ trả dòng của tenant hiện tại.
///    Chưa có tenant → không trả dòng nào (fail closed).
/// 2. SaveChanges từ chối ghi/sửa/xóa dòng không thuộc tenant hiện tại.
/// Cấm IgnoreQueryFilters() trừ code super admin / job hệ thống có comment lý do (CLAUDE.md mục 8).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Khóa mã hóa cookie đăng nhập của ASP.NET Core. Bảng hệ thống, không thuộc tenant.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<KnowledgeItem> KnowledgeItems => Set<KnowledgeItem>();

    public DbSet<KnowledgeImport> KnowledgeImports => Set<KnowledgeImport>();

    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChannelConnection> ChannelConnections => Set<ChannelConnection>();

    public DbSet<HandoffSettings> HandoffSettings => Set<HandoffSettings>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    public DbSet<ContactNote> ContactNotes => Set<ContactNote>();

    public DbSet<CareSuggestion> CareSuggestions => Set<CareSuggestion>();

    /// <summary>EF đọc lại giá trị này mỗi lần truy vấn (tham số hóa theo instance DbContext).</summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<Tenant>().HasQueryFilter(t => t.Id == CurrentTenantId);

        var applyFilter = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                applyFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder)
        where T : class, ITenantOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

    private void PrepareForSave()
    {
        var now = DateTimeOffset.UtcNow;
        var current = tenantContext.TenantId;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case ITenantOwned owned:
                    if (entry.State == EntityState.Added && owned.TenantId == Guid.Empty && current is not null)
                    {
                        owned.TenantId = current.Value;
                    }

                    EnsureCurrentTenant(owned.TenantId, current, entry.Metadata.ClrType.Name);
                    if (entry.State != EntityState.Added)
                    {
                        // Chặn cả việc "chuyển" một dòng sang tenant khác.
                        var original = (Guid)entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue!;
                        EnsureCurrentTenant(original, current, entry.Metadata.ClrType.Name);
                    }

                    break;

                case Tenant tenant:
                    EnsureCurrentTenant(tenant.Id, current, nameof(Tenant));
                    break;
            }

            if (entry.State == EntityState.Added)
            {
                SetIfDefault(entry, "Id", Guid.CreateVersion7());
                SetIfDefault(entry, "CreatedAt", now);
            }

            if (entry.State is (EntityState.Added or EntityState.Modified) && entry.Metadata.FindProperty("UpdatedAt") is not null)
            {
                entry.Property("UpdatedAt").CurrentValue = now;
            }
        }
    }

    private static void EnsureCurrentTenant(Guid owner, Guid? current, string entityName)
    {
        if (current is null || owner != current)
        {
            // Không đưa id vào message để log/Sentry không lộ cấu trúc dữ liệu tenant khác.
            throw new TenantIsolationException($"Từ chối ghi {entityName}: không thuộc tenant hiện tại.");
        }
    }

    private static void SetIfDefault<T>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string property, T value)
        where T : struct
    {
        if (entry.Metadata.FindProperty(property) is { } meta && meta.ClrType == typeof(T))
        {
            var prop = entry.Property(property);
            if (prop.CurrentValue is T existing && existing.Equals(default(T)))
            {
                prop.CurrentValue = value;
            }
        }
    }
}
