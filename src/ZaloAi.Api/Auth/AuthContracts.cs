using FluentValidation;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record SwitchTenantRequest(Guid TenantId);

public sealed record TenantSummary(Guid Id, string Name, string Role, bool IsActive);

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
            .Select(t => new TenantSummary(t.TenantId, t.TenantName, t.Role.ToString().ToLowerInvariant(), t.Status == TenantStatus.Active))
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
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

internal sealed class SwitchTenantRequestValidator : AbstractValidator<SwitchTenantRequest>
{
    public SwitchTenantRequestValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
    }
}
