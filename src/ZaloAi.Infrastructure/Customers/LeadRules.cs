using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Customers;

/// <summary>
/// Hệ thống tự nâng tầng khách tiềm năng (Mới → Quan tâm → Nóng), không bao giờ hạ, không đụng khách nhân viên đã tự đặt
/// hoặc đã ở Đã chốt / Không tiềm năng (docs/DECISIONS.md, M6).
/// </summary>
public static class LeadRules
{
    /// <summary>Khóa thông tin cho thấy khách có nhu cầu cụ thể (theo mẫu ngành).</summary>
    private static readonly string[] InterestKeys = ["service_interest", "need", "concern_area", "preferred_time", "branch"];

    /// <returns>true nếu đã đổi.</returns>
    public static bool Promote(Contact contact, LeadStatus target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.LeadStatusManual || contact.LeadStatus >= LeadStatus.Won || target >= LeadStatus.Won || target <= contact.LeadStatus)
        {
            return false;
        }

        contact.LeadStatus = target;
        contact.LeadStatusChangedAt = now;
        return true;
    }

    /// <summary>
    /// Sau một lượt bot trả lời: để lại SĐT hoặc muốn đặt lịch → Nóng; nói rõ nhu cầu (dịch vụ, thời gian...) hoặc đã nhắn từ
    /// 3 tin → Quan tâm.
    /// </summary>
    public static LeadStatus FromBotTurn(IReadOnlyDictionary<string, string> allLeadFields, string? handoffReason, int customerMessages)
    {
        ArgumentNullException.ThrowIfNull(allLeadFields);
        if (allLeadFields.ContainsKey("phone") || handoffReason == "booking")
        {
            return LeadStatus.Hot;
        }

        return InterestKeys.Any(allLeadFields.ContainsKey) || customerMessages >= 3 ? LeadStatus.Interested : LeadStatus.New;
    }

    public static LeadStatus FromCare(CareTemperature temperature) => temperature switch
    {
        CareTemperature.Hot => LeadStatus.Hot,
        CareTemperature.Warm => LeadStatus.Interested,
        _ => LeadStatus.New,
    };
}
