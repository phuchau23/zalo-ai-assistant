using FluentValidation;
using ZaloAi.Core.Entities;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Api.Tenants;

public sealed record TenantSettingsResponse(
    Guid Id,
    string Name,
    string IndustrySlug,
    string BotName,
    string BotPronoun,
    string? PrivacyUrl,
    string Plan,
    string Status)
{
    internal static TenantSettingsResponse From(Tenant t) =>
        new(t.Id, t.Name, t.IndustrySlug, t.BotName, t.BotPronoun, t.PrivacyUrl, t.Plan, t.Status.ToString().ToLowerInvariant());
}

public sealed record UpdateTenantSettingsRequest(
    string Name,
    string IndustrySlug,
    string BotName,
    string BotPronoun,
    string? PrivacyUrl);

internal sealed class UpdateTenantSettingsRequestValidator : AbstractValidator<UpdateTenantSettingsRequest>
{
    public UpdateTenantSettingsRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên doanh nghiệp");
        RuleFor(x => x.IndustrySlug)
            .Must(IndustryCatalog.IsKnown)
            .WithMessage("Ngành không có trong danh sách hỗ trợ.");
        RuleFor(x => x.BotName).NotEmpty().MaximumLength(100).WithName("Tên bot");
        RuleFor(x => x.BotPronoun).NotEmpty().MaximumLength(20).WithName("Cách bot xưng hô");
        RuleFor(x => x.PrivacyUrl)
            .MaximumLength(500)
            .Must(BeHttpsUrl)
            .WithMessage("Link chính sách bảo mật phải là địa chỉ https:// đầy đủ.")
            .When(x => !string.IsNullOrWhiteSpace(x.PrivacyUrl));
    }

    private static bool BeHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
