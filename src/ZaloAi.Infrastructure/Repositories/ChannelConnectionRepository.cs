using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Kênh đã kết nối của một tenant. Token mã hóa ở tầng gọi (IFieldEncryptor).</summary>
public sealed class ChannelConnectionRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<IReadOnlyList<ChannelConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.ChannelConnections
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<ChannelConnection?> GetAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ChannelConnections
            .Where(c => c.TenantId == tenantId && c.Id == connectionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Được track — dùng khi kết nối lại cùng OA (cập nhật token thay vì tạo dòng mới).</summary>
    public Task<ChannelConnection?> GetByExternalIdAsync(Guid tenantId, ChannelKind channel, string externalId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ChannelConnections
            .Where(c => c.TenantId == tenantId && c.Channel == channel && c.ExternalId == externalId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public ChannelConnection Add(Guid tenantId, ChannelConnection connection)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(connection);
        connection.TenantId = tenantId;
        if (connection.Id == Guid.Empty)
        {
            connection.Id = Guid.CreateVersion7();
        }

        Db.ChannelConnections.Add(connection);
        return connection;
    }

    public void Remove(Guid tenantId, ChannelConnection connection)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.TenantId != tenantId)
        {
            throw new TenantIsolationException("Kết nối không thuộc tenant hiện tại.");
        }

        Db.ChannelConnections.Remove(connection);
    }
}

/// <param name="TenantActive">Tenant còn hoạt động (bị khóa/hết hạn → bỏ qua tin, không trả lời).</param>
public sealed record ConnectionRoute(Guid ConnectionId, Guid TenantId, ConnectionStatus Status, bool TenantActive);

/// <summary>
/// Truy vấn HỆ THỐNG chạy trước khi biết tenant: webhook Zalo chỉ có OA ID → cần tìm OA thuộc tenant nào; job quét token sắp hết hạn
/// chạy cho mọi tenant. IgnoreQueryFilters() CÓ CHỦ ĐÍCH. Chỉ trả id + trạng thái (không token, không dữ liệu nghiệp vụ);
/// việc đọc/ghi tiếp theo phải set tenant context rồi dùng <see cref="ChannelConnectionRepository"/>.
/// </summary>
public sealed class ChannelConnectionLookup(AppDbContext db)
{
    public Task<ConnectionRoute?> FindAsync(ChannelKind channel, string externalId, CancellationToken cancellationToken) =>
        (from c in db.ChannelConnections.IgnoreQueryFilters()
         join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
         where c.Channel == channel && c.ExternalId == externalId
         select new ConnectionRoute(c.Id, c.TenantId, c.Status, t.Status == TenantStatus.Active))
        .AsNoTracking()
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>OA nào thuộc tenant khác chưa (để báo lỗi khi DN B cố kết nối OA đang thuộc DN A, không lộ tên DN A).</summary>
    public Task<bool> BelongsToOtherTenantAsync(ChannelKind channel, string externalId, Guid tenantId, CancellationToken cancellationToken) =>
        db.ChannelConnections.IgnoreQueryFilters()
            .AnyAsync(c => c.Channel == channel && c.ExternalId == externalId && c.TenantId != tenantId, cancellationToken);

    /// <summary>Kết nối đang hoạt động có access token hết hạn trước <paramref name="before"/> (cần làm mới).</summary>
    public async Task<IReadOnlyList<(Guid ConnectionId, Guid TenantId)>> ListExpiringAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        var rows = await db.ChannelConnections.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Status == ConnectionStatus.Active && c.AccessTokenExpiresAt < before)
            .Select(c => new { c.Id, c.TenantId })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.TenantId)).ToList();
    }
}
