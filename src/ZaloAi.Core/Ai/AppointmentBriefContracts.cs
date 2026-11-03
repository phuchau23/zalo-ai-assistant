namespace ZaloAi.Core.Ai;

/// <param name="Services">Dịch vụ sắp làm.</param>
/// <param name="StaffNotes">Nhật ký chăm sóc của nhân viên (đã giải mã, sẽ được che PII trước khi gửi AI).</param>
/// <param name="ConversationSummary">Tóm tắt hội thoại + vài tin gần nhất.</param>
/// <param name="PreviousVisits">Các buổi đã làm trước (ngày + dịch vụ).</param>
public sealed record AppointmentBriefRequest(
    string BusinessName,
    string IndustrySlug,
    string CustomerName,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> StaffNotes,
    string? ConversationSummary,
    IReadOnlyList<BotHistoryTurn> RecentMessages,
    IReadOnlyList<string> PreviousVisits);

public sealed record AppointmentBriefResult(string Brief, IReadOnlyList<BotUsage> Usage);

/// <summary>Tóm tắt khách cho kỹ thuật viên trước buổi làm (CRM C2): khách gặp gì, muốn gì, lưu ý, buổi trước. Che PII trước khi gọi AI.</summary>
public interface IAppointmentBriefWriter
{
    Task<AppointmentBriefResult> WriteAsync(Guid tenantId, AppointmentBriefRequest request, CancellationToken cancellationToken);
}
