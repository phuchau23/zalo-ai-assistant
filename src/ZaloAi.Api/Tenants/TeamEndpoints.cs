using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Team;

namespace ZaloAi.Api.Tenants;

/// <param name="Role">owner (chủ doanh nghiệp) | staff (nhân viên)</param>
/// <param name="IsYou">Chính người đang đăng nhập (không tự xóa / tự cấp lại mật khẩu).</param>
/// <param name="Permissions">Công tắc quyền của nhân viên (xem CrmSettingsResponse.AllPermissions); chủ luôn đầy đủ.</param>
/// <param name="BranchId">Chi nhánh làm việc; null = mọi chi nhánh.</param>
public sealed record TeamMemberResponse(
    Guid UserId,
    string Email,
    string Name,
    string Role,
    bool IsYou,
    DateTimeOffset JoinedAt,
    IReadOnlyList<string> Permissions,
    Guid? BranchId);

/// <param name="Permissions">Danh sách quyền bật cho nhân viên (mã quyền).</param>
/// <param name="BranchId">Chi nhánh làm việc; null = mọi chi nhánh.</param>
public sealed record SetTeamPermissionsRequest(IReadOnlyList<string> Permissions, Guid? BranchId);

internal sealed class SetTeamPermissionsRequestValidator : AbstractValidator<SetTeamPermissionsRequest>
{
    public SetTeamPermissionsRequestValidator()
    {
        RuleFor(x => x.Permissions).NotNull().Must(p => p.Count <= 50);
        RuleForEach(x => x.Permissions).Must(TenantPermissions.IsKnown).WithMessage("Quyền không hợp lệ.");
    }
}

/// <param name="Role">owner | staff</param>
public sealed record AddTeamMemberRequest(string Email, string Name, string Role);

/// <param name="TemporaryPassword">Mật khẩu tạm — chỉ hiện MỘT lần; null khi email đã có tài khoản (dùng mật khẩu hiện có).</param>
public sealed record AddTeamMemberResponse(TeamMemberResponse Member, string? TemporaryPassword);

/// <param name="Role">owner | staff</param>
public sealed record ChangeTeamRoleRequest(string Role);

/// <param name="TemporaryPassword">Mật khẩu tạm mới — chỉ hiện MỘT lần.</param>
public sealed record ResetPasswordResponse(string TemporaryPassword);

internal sealed class AddTeamMemberRequestValidator : AbstractValidator<AddTeamMemberRequest>
{
    public AddTeamMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress().WithName("Email");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên");
        RuleFor(x => x.Role).Must(TeamEndpoints.IsRole).WithMessage("Vai trò phải là owner hoặc staff.");
    }
}

internal sealed class ChangeTeamRoleRequestValidator : AbstractValidator<ChangeTeamRoleRequest>
{
    public ChangeTeamRoleRequestValidator() => RuleFor(x => x.Role).Must(TeamEndpoints.IsRole).WithMessage("Vai trò phải là owner hoặc staff.");
}

/// <summary>
/// Trang Cài đặt → Nhân viên (chỉ chủ DN): thêm tài khoản nhân viên bằng email (cấp mật khẩu tạm), đổi vai trò, xóa, cấp lại
/// mật khẩu. tenantId luôn từ cookie. Logic và chốt an toàn ở <see cref="TeamService"/>.
/// </summary>
internal static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tenant/team").WithTags("Tenant").RequireTenantRole(TenantRole.Owner);
        group.MapGet("", ListAsync);
        group.MapPost("", AddAsync).Validate<AddTeamMemberRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPatch("/{userId:guid}", ChangeRoleAsync)
            .Validate<ChangeTeamRoleRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{userId:guid}", RemoveAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{userId:guid}/reset-password", ResetPasswordAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{userId:guid}/permissions", SetPermissionsAsync)
            .Validate<SetTeamPermissionsRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<NoContent> SetPermissionsAsync(
        Guid userId, SetTeamPermissionsRequest request, ITenantContext tenant, TeamService team, CancellationToken cancellationToken)
    {
        await team.SetPermissionsAsync(tenant.RequireTenantId(), userId, request.Permissions, request.BranchId, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static bool IsRole(string? role) => role is "owner" or "staff";

    private static TenantRole ParseRole(string role) => role == "owner" ? TenantRole.Owner : TenantRole.Staff;

    private static async Task<Ok<List<TeamMemberResponse>>> ListAsync(ITenantContext tenant, TeamService team, CancellationToken cancellationToken)
    {
        var members = await team.ListAsync(tenant.RequireTenantId(), cancellationToken);
        return TypedResults.Ok(members.Select(m => ToResponse(m, tenant)).ToList());
    }

    private static async Task<Ok<AddTeamMemberResponse>> AddAsync(
        AddTeamMemberRequest request, ITenantContext tenant, TeamService team, CancellationToken cancellationToken)
    {
        var added = await team.AddAsync(tenant.RequireTenantId(), request.Email, request.Name, ParseRole(request.Role), cancellationToken);
        return TypedResults.Ok(new AddTeamMemberResponse(ToResponse(added.Member, tenant), added.TemporaryPassword));
    }

    private static async Task<NoContent> ChangeRoleAsync(
        Guid userId, ChangeTeamRoleRequest request, ITenantContext tenant, TeamService team, CancellationToken cancellationToken)
    {
        await team.ChangeRoleAsync(tenant.RequireTenantId(), userId, ParseRole(request.Role), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveAsync(Guid userId, ITenantContext tenant, TeamService team, CancellationToken cancellationToken)
    {
        await team.RemoveAsync(tenant.RequireTenantId(), userId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ResetPasswordResponse>> ResetPasswordAsync(
        Guid userId, ITenantContext tenant, TeamService team, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ResetPasswordResponse(await team.ResetPasswordAsync(tenant.RequireTenantId(), userId, cancellationToken)));

    private static TeamMemberResponse ToResponse(TeamMember m, ITenantContext tenant) =>
        new(m.UserId, m.Email, m.Name, m.Role.ToString().ToLowerInvariant(), m.UserId == tenant.UserId, m.JoinedAt, m.Permissions, m.BranchId);
}
