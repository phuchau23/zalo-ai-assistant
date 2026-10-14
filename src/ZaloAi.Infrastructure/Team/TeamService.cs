using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Security;

namespace ZaloAi.Infrastructure.Team;

public sealed record TeamMember(Guid UserId, string Email, string Name, TenantRole Role, DateTimeOffset JoinedAt);

/// <param name="TemporaryPassword">Mật khẩu tạm (chỉ trả một lần); null khi email đã có tài khoản (giữ mật khẩu cũ).</param>
public sealed record AddedMember(TeamMember Member, string? TemporaryPassword);

/// <summary>
/// Nhân viên của MỘT doanh nghiệp (chủ DN quản lý): thêm bằng email, đổi vai trò, xóa, cấp lại mật khẩu. Mọi thao tác lọc theo
/// tenant_id của người gọi và ghi audit log. Chốt an toàn: luôn còn ít nhất một chủ; không tự xóa mình; chỉ cấp lại mật khẩu cho
/// tài khoản CHỈ thuộc DN này (tài khoản dùng chung với DN khác thì người đó tự đổi — tránh chủ DN A chiếm tài khoản ở DN B).
/// </summary>
public sealed class TeamService(AppDbContext db, ITenantContext tenantContext, IPasswordHasher<User> hasher) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<TeamMember>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (
            from m in Db.Memberships
            join u in Db.Users on m.UserId equals u.Id
            where m.TenantId == tenantId
            orderby m.Role descending, m.CreatedAt
            select new TeamMember(u.Id, u.Email, u.Name, m.Role, m.CreatedAt)).ToListAsync(cancellationToken);
    }

    public async Task<AddedMember> AddAsync(Guid tenantId, string email, string name, TenantRole role, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var normalized = User.NormalizeEmail(email);
        var user = await Db.Users.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
        string? temporaryPassword = null;
        if (user is null)
        {
            temporaryPassword = Passwords.NewTemporary();
            user = new User { Id = Guid.CreateVersion7(), Email = normalized, Name = name.Trim(), PasswordHash = "" };
            user.PasswordHash = hasher.HashPassword(user, temporaryPassword);
            Db.Users.Add(user);
        }
        else if (await Db.Memberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == user.Id, cancellationToken))
        {
            throw new ConflictException("Người này đã là thành viên của doanh nghiệp.", "already_member");
        }

        var membership = new Membership { TenantId = tenantId, UserId = user.Id, Role = role };
        Db.Memberships.Add(membership);
        Audit(tenantId, "team.member_added", $"{user.Id}:{Lower(role)}");
        await Db.SaveChangesAsync(cancellationToken);
        return new AddedMember(new TeamMember(user.Id, user.Email, user.Name, role, membership.CreatedAt), temporaryPassword);
    }

    public async Task ChangeRoleAsync(Guid tenantId, Guid userId, TenantRole role, CancellationToken cancellationToken)
    {
        var membership = await RequireAsync(tenantId, userId, cancellationToken);
        if (membership.Role == role)
        {
            return;
        }

        if (membership.Role == TenantRole.Owner && await OwnerCountAsync(tenantId, cancellationToken) <= 1)
        {
            throw new ConflictException("Doanh nghiệp cần ít nhất một chủ. Thêm chủ khác trước khi đổi vai trò người này.", "last_owner");
        }

        membership.Role = role;
        Audit(tenantId, "team.role_changed", $"{userId}:{Lower(role)}");
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Xóa khỏi DN (mất quyền ngay ở request kế tiếp). Tài khoản không còn thuộc DN nào và không phải super admin thì xóa luôn.</summary>
    public async Task RemoveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        if (userId == _caller.UserId)
        {
            throw new ConflictException("Bạn không thể tự xóa mình khỏi doanh nghiệp.", "cannot_remove_self");
        }

        var membership = await RequireAsync(tenantId, userId, cancellationToken);
        if (membership.Role == TenantRole.Owner && await OwnerCountAsync(tenantId, cancellationToken) <= 1)
        {
            throw new ConflictException("Doanh nghiệp cần ít nhất một chủ.", "last_owner");
        }

        Db.Memberships.Remove(membership);
        Audit(tenantId, "team.member_removed", userId.ToString());
        await Db.SaveChangesAsync(cancellationToken);

        // Đếm membership XUYÊN tenant có chủ đích: chỉ để biết tài khoản còn thuộc DN khác không, không đọc dữ liệu nghiệp vụ.
        await Db.Users
            .Where(u => u.Id == userId && !u.IsSuperAdmin && !Db.Memberships.IgnoreQueryFilters().Any(m => m.UserId == u.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Cấp mật khẩu tạm mới (chỉ trả một lần). Mọi phiên đăng nhập cũ của người đó bị đăng xuất.</summary>
    public async Task<string> ResetPasswordAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        if (userId == _caller.UserId)
        {
            throw new ConflictException("Tự đổi mật khẩu của bạn ở trang Tài khoản.", "use_change_password");
        }

        await RequireAsync(tenantId, userId, cancellationToken);
        var user = await Db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        if (user.IsSuperAdmin || await Db.Memberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.TenantId != tenantId, cancellationToken))
        {
            throw new ConflictException("Tài khoản này còn dùng ở nơi khác, người đó cần tự đổi mật khẩu.", "shared_account");
        }

        var password = Passwords.NewTemporary();
        user.PasswordHash = hasher.HashPassword(user, password);
        Audit(tenantId, "team.password_reset", userId.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return password;
    }

    private async Task<Membership> RequireAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Memberships.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, cancellationToken)
            ?? throw new NotFoundException();
    }

    private Task<int> OwnerCountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Db.Memberships.CountAsync(m => m.TenantId == tenantId && m.Role == TenantRole.Owner, cancellationToken);

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });

    private static string Lower(TenantRole role) => role.ToString().ToLowerInvariant();
}
