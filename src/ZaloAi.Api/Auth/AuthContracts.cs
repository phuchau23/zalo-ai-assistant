using FluentValidation;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Security;

namespace ZaloAi.Api.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record SwitchTenantRequest(Guid TenantId);

/// <param name="ExpiresAt">Hạn thuê; null = không thời hạn. Web nhắc chủ DN từ 7 ngày trước, quá hạn 3 ngày thì tự khóa.</param>
public sealed record TenantSummary(Guid Id, string Name, string Role, bool IsActive, DateTimeOffset? ExpiresAt);

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string Name,
    bool IsSuperAdmin,
    TenantSummary? CurrentTenant,
    IReadOnlyList<TenantSummary> Tenants)
{
    internal static MeResponse From(UserAccess user, IReadOnlyList<TenantAccess> tenants, Guid? currentTenantId)
    {
        var summaries = tenants
            .Select(t => new TenantSummary(t.TenantId, t.TenantName, t.Role.ToString().ToLowerInvariant(), t.Status == TenantStatus.Active, t.ExpiresAt))
            .ToList();
        return new MeResponse(
            user.UserId,
            user.Email,
            user.Name,
            user.IsSuperAdmin,
            summaries.FirstOrDefault(t => t.Id == currentTenantId),
            summaries);
    }
}

internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress().WithName("Email");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200).WithName("Mật khẩu");
    }
}

internal sealed class SwitchTenantRequestValidator : AbstractValidator<SwitchTenantRequest>
{
    public SwitchTenantRequestValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
    }
}

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(200).WithName("Mật khẩu hiện tại");
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(Passwords.MinLength).MaximumLength(200).WithName("Mật khẩu mới");
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("Mật khẩu mới phải khác mật khẩu hiện tại.");
    }
}
