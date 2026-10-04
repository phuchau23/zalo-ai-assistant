using ZaloAi.Core.Entities;

namespace ZaloAi.Core.Ai;

/// <summary>Ghi chú nhân viên về khách (nội dung gốc, bộ phân tích tự che dữ liệu cá nhân trước khi gửi AI).</summary>
/// <param name="Kind">note | service | appointment</param>
public sealed record CareNoteInput(string Kind, DateOnly HappenedOn, DateTimeOffset? FollowUpAt, string Text);

/// <param name="Messages">Các tin gần nhất (cũ → mới), nội dung gốc.</param>
/// <param name="FollowUpNote">Có giá trị khi phân tích vì tới giờ hẹn chăm sóc lại trong ghi chú này.</param>
/// <param name="BotWillSend">true = tin nháp do BOT tự gửi (viết bằng giọng trợ lý AI); false = nhân viên sửa rồi gửi.</param>
public sealed record CareAnalysisRequest(
    Guid TenantId,
    BotProfile Profile,
    string? CustomerName,
    string? Summary,
    IReadOnlyList<BotHistoryTurn> Messages,
    IReadOnlyList<CareNoteInput> Notes,
    LeadStatus LeadStatus,
    DateTimeOffset? LastCustomerMessageAt,
    CareNoteInput? FollowUpNote,
    bool BotWillSend = false);

/// <summary>AI đề xuất làm gì với khách.</summary>
public enum CareAction
{
    /// <summary>Không cần làm gì (đã chốt, đã từ chối, chỉ chào hỏi...).</summary>
    None = 0,

    /// <summary>Nhắn chăm sóc được — bot tự nhắn (nếu DN bật) hoặc nhân viên nhắn theo tin nháp.</summary>
    Send = 1,

    /// <summary>Cần nhân viên liên hệ (phàn nàn, sức khỏe, cần thông tin bot không có...).</summary>
    AskHuman = 2,
}

/// <param name="Trigger">price_no_close | thinking | asked_schedule | complaint_followup | win_back | unused_package | follow_up | other</param>
/// <param name="HumanReason">Khi <see cref="Action"/> = AskHuman: complaint | sensitive | needs_staff_info | other.</param>
/// <param name="Promotional">Tin mang tính quảng cáo (ưu đãi, kéo khách cũ) — chỉ được tự gửi khi khách đã đồng ý nhận tin.</param>
/// <param name="Draft">Tin nháp (đã ghép lại dữ liệu cá nhân); null nếu không có hoặc bị bộ lọc an toàn chặn.</param>
/// <param name="DraftBlocked">Tin nháp bị chặn bởi bộ lọc (câu cấm, có giá tiền khi bot tự gửi).</param>
public sealed record CareAnalysisResult(
    CareAction Action,
    CareTemperature Temperature,
    string Trigger,
    string Reason,
    string? SuggestedAction,
    string? Draft,
    IReadOnlyList<BotUsage> Usage,
    string? HumanReason = null,
    bool Promotional = false,
    bool DraftBlocked = false)
{
    public bool ShouldFollowUp => Action != CareAction.None;
}

/// <summary>
/// AI xem khách có cần chăm sóc chủ động không (docs/FEATURE-SPECS.md mục 2): tự nhắn, gọi nhân viên, hay thôi.
/// Không đọc/ghi database, không tự gửi — job quyết định gửi sau khi qua các giới hạn cứng của hệ thống.
/// </summary>
public interface ICareAdvisor
{
    /// <returns>null nếu AI trả sai định dạng 2 lần (bỏ qua lần này).</returns>
    Task<CareAnalysisResult?> AnalyzeAsync(CareAnalysisRequest request, CancellationToken cancellationToken);
}
