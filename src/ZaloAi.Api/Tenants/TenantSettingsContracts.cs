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
    string Status,
    string BotTone,
    string? BotInstructions)
{
    internal static TenantSettingsResponse From(Tenant t) =>
        new(t.Id, t.Name, t.IndustrySlug, t.BotName, t.BotPronoun, t.PrivacyUrl, t.Plan, t.Status.ToString().ToLowerInvariant(),
            t.BotTone.ToString().ToLowerInvariant(), t.BotInstructions);
}

/// <param name="BotTone">friendly | professional | concise</param>
/// <param name="BotInstructions">Hướng dẫn thêm cho bot (không ghi đè được quy tắc an toàn), tối đa 1000 ký tự.</param>
public sealed record UpdateBotStyleRequest(string BotTone, string? BotInstructions);

internal sealed class UpdateBotStyleRequestValidator : AbstractValidator<UpdateBotStyleRequest>
{
    public UpdateBotStyleRequestValidator()
    {
        RuleFor(x => x.BotTone)
            .Must(t => Enum.TryParse<BotTone>(t, ignoreCase: true, out _) && !int.TryParse(t, out _))
            .WithMessage("Giọng văn phải là friendly, professional hoặc concise.");
        RuleFor(x => x.BotInstructions).MaximumLength(1000).WithName("Hướng dẫn thêm");
    }
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
