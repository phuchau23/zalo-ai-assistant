using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace ZaloAi.Core.Options;

/// <summary>Một gói thuê. Giá chốt ở tuần 7–8; giờ chỉ có hạn mức hội thoại.</summary>
public sealed class PlanDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Số hội thoại (khách khác nhau có nhắn) mỗi tháng giờ Việt Nam. null = không giới hạn.</summary>
    public int? ConversationsPerMonth { get; set; }
}

/// <summary>
/// Gói thuê và hạn mức (CLAUDE.md M6, chủ dự án chốt 2026-10-14: gói dùng thử tạm 100 hội thoại/tháng). Thêm gói trong
/// appsettings (Billing:Plans:&lt;mã&gt;); super admin còn đặt được hạn mức riêng từng DN (tenants.conversation_quota).
/// </summary>
public sealed partial class BillingOptions : IValidatableObject
{
    public const string SectionName = "Billing";

    public const string TrialPlan = "trial";

    /// <summary>Quá hạn thuê bao nhiêu ngày thì tự khóa.</summary>
    [Range(0, 60)]
    public int SuspendAfterExpiryDays { get; set; } = 3;

    /// <summary>Báo trước bao nhiêu ngày trên web.</summary>
    [Range(0, 60)]
    public int ExpiryWarningDays { get; set; } = 7;

    public Dictionary<string, PlanDefinition> Plans { get; set; } = new(StringComparer.Ordinal)
    {
        [TrialPlan] = new PlanDefinition { Name = "Dùng thử", ConversationsPerMonth = 100 },
    };

    /// <summary>Hạn mức thực tế: riêng của DN nếu super admin đặt, không thì theo gói (gói lạ → như gói dùng thử).</summary>
    public int? QuotaFor(string plan, int? tenantQuota) =>
        tenantQuota ?? (Plans.TryGetValue(plan, out var p) ? p : Plans[TrialPlan]).ConversationsPerMonth;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Plans.ContainsKey(TrialPlan))
        {
            yield return new ValidationResult("Billing:Plans phải có gói 'trial'.", [nameof(Plans)]);
        }

        foreach (var (code, plan) in Plans)
        {
            if (!PlanCode().IsMatch(code) || string.IsNullOrWhiteSpace(plan.Name) || plan.ConversationsPerMonth is < 0)
            {
                yield return new ValidationResult($"Gói '{code}' không hợp lệ (mã a-z0-9-, có tên, hạn mức ≥ 0).", [nameof(Plans)]);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9-]{1,30}$")]
    private static partial Regex PlanCode();
}
