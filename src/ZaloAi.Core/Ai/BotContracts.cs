using ZaloAi.Core.Entities;

namespace ZaloAi.Core.Ai;

/// <summary>Một đoạn kiến thức tìm được cho câu hỏi. <see cref="Score"/> 0..1 (1 = giống hệt).</summary>
/// <param name="Source">item (mục có cấu trúc) | document (tài liệu tự do)</param>
/// <param name="MedicallyReviewed">Nội dung đã được người có chuyên môn duyệt (INDUSTRIES.md mục 3).</param>
public sealed record KnowledgeHit(
    Guid ChunkId,
    string Source,
    Guid SourceId,
    string? Code,
    string? Title,
    string Content,
    double Score,
    bool MedicallyReviewed);

/// <summary>Tìm kiến thức liên quan, CHỈ trong kho của tenant (lọc tenant_id trước khi xếp theo độ giống).</summary>
public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken);
}

/// <summary>Cài đặt của doanh nghiệp mà bot cần biết.</summary>
public sealed record BotProfile(
    string BusinessName,
    string IndustrySlug,
    string BotName,
    string BotPronoun,
    BotTone Tone,
    string? Instructions,
    string? PrivacyUrl);

/// <summary>Một tin trong lịch sử hội thoại (nội dung gốc, chưa che — bộ não tự che trước khi gửi AI).</summary>
public sealed record BotHistoryTurn(MessageSender Sender, string Text);

/// <param name="History">Các tin trước tin hiện tại (sau mốc tóm tắt), cũ → mới.</param>
/// <param name="Summary">Tóm tắt các tin cũ hơn (nội dung gốc, bộ não tự che).</param>
/// <param name="IsFirstBotReply">Chưa từng trả lời trong hội thoại này → kèm câu báo trợ lý AI + link chính sách.</param>
/// <param name="CustomerName">Tên khách đã biết (tên hiển thị Zalo, khách tự khai) — được che trước khi gửi AI.</param>
/// <param name="Salutation">Cách gọi khách đã lưu (<see cref="CustomerSalutation"/>); null = chưa biết.</param>
/// <param name="SalutationByStaff">Nhân viên đã đặt cách gọi → AI không đổi theo cách khách tự xưng.</param>
/// <param name="Booking">Đặt lịch qua chat đang bật (CRM C2) và thông tin lịch sắp tới của khách để AI hiểu "dời/hủy".</param>
public sealed record BotTurnRequest(
    Guid TenantId,
    BotProfile Profile,
    string CustomerMessage,
    IReadOnlyList<BotHistoryTurn> History,
    string? Summary,
    bool IsFirstBotReply,
    string? CustomerName = null,
    string? Salutation = null,
    bool SalutationByStaff = false,
    BookingContext? Booking = null);

/// <summary>Lịch sắp tới của khách (đưa vào prompt, không PII ngoài tên dịch vụ/giờ).</summary>
/// <param name="StartLocal">Giờ địa phương "dddd dd/MM HH:mm".</param>
public sealed record UpcomingAppointment(Guid AppointmentId, string StartLocal, string ServiceNames, string? StaffName, string BranchName);

/// <param name="Enabled">Bot được tự chốt lịch.</param>
/// <param name="BranchNames">Tên các chi nhánh đang nhận lịch (để AI hỏi khách chọn khi có nhiều).</param>
/// <param name="Upcoming">Lịch sắp tới (tối đa 3).</param>
public sealed record BookingContext(bool Enabled, IReadOnlyList<string> BranchNames, IReadOnlyList<UpcomingAppointment> Upcoming);

/// <summary>
/// Ý định đặt lịch AI rút ra từ tin khách (CRM C2). AI KHÔNG quyết định còn chỗ hay không — hệ thống kiểm tra rồi mới chốt
/// và tự soạn câu xác nhận / gợi ý giờ khác.
/// </summary>
/// <param name="Action">book | reschedule | cancel | check_slots</param>
/// <param name="ServiceCodes">Mã dịch vụ/gói trong dữ liệu tham khảo (DV-..., GOI-...), khách muốn làm.</param>
/// <param name="Date">yyyy-MM-dd theo giờ Việt Nam (AI quy đổi "mai", "thứ 7" theo thời gian hiện tại trong prompt).</param>
/// <param name="Time">HH:mm; null = khách chưa nói giờ.</param>
/// <param name="StaffName">Khách muốn nhân viên cụ thể (tên khách nhắc), null = ai cũng được.</param>
/// <param name="BranchName">Khách chọn chi nhánh (khi DN có nhiều).</param>
/// <param name="PartySize">Số người (mặc định 1).</param>
/// <param name="AppointmentId">Lịch đang có khách muốn dời/hủy (từ danh sách lịch sắp tới).</param>
public sealed record BookingIntent(
    string Action,
    IReadOnlyList<string> ServiceCodes,
    string? Date,
    string? Time,
    string? StaffName,
    string? BranchName,
    int PartySize,
    Guid? AppointmentId);

public sealed record BotUsage(UsageKind Kind, string Provider, string Model, int InputTokens, int OutputTokens, decimal CostUsd);

/// <summary>
/// Kết quả một lượt trả lời. <see cref="Reply"/> đã qua mọi kiểm tra (câu cấm, khẩn cấp...) và sẵn sàng gửi khách.
/// <see cref="TraceJson"/> không chứa nội dung tin hay dữ liệu cá nhân (an toàn để lưu dạng thường).
/// </summary>
/// <param name="Confidence">high | medium | low</param>
/// <param name="Sentiment">positive | neutral | negative, null nếu không rõ</param>
/// <param name="LeadFields">Thông tin khách tự cung cấp (tên, SĐT...) — dữ liệu cá nhân, phải mã hóa khi lưu.</param>
/// <param name="Messages">Các tin gửi lần lượt (1–4 tin ngắn như người thật nhắn; câu báo trợ lý AI nằm ở đầu tin đầu tiên khi cần).</param>
/// <param name="DetectedSalutation">Khách tự xưng rõ (anh/chị/cô/chú/bác) trong tin vừa rồi; null nếu không.</param>
/// <param name="Booking">Ý định đặt/dời/hủy lịch (chỉ khi không chuyển người); job gọi hệ thống lịch để chốt và nối thêm câu xác nhận.</param>
public sealed record BotTurnResult(
    IReadOnlyList<string> Messages,
    bool NeedsHuman,
    string? HandoffReason,
    Urgency Urgency,
    string Confidence,
    string? Sentiment,
    IReadOnlyDictionary<string, string> LeadFields,
    string TraceJson,
    IReadOnlyList<BotUsage> Usage,
    string? DetectedSalutation = null,
    BookingIntent? Booking = null)
{
    /// <summary>Toàn bộ câu trả lời gộp một đoạn (eval, log chẩn đoán).</summary>
    public string Reply => string.Join("\n", Messages);
}

public sealed record BotSummaryResult(string Summary, IReadOnlyList<BotUsage> Usage);

/// <summary>
/// "Bộ não" trả lời khách: che dữ liệu cá nhân → kiểm tra dấu hiệu nguy hiểm → tìm kiến thức → gọi AI → kiểm tra câu cấm
/// → câu chào báo trợ lý AI. Không đọc/ghi database (job lo phần đó).
/// </summary>
public interface IBotEngine
{
    Task<BotTurnResult> ReplyAsync(BotTurnRequest request, CancellationToken cancellationToken);

    /// <summary>Gộp tóm tắt cũ + các tin cũ thành tóm tắt mới (đã che dữ liệu cá nhân).</summary>
    Task<BotSummaryResult> SummarizeAsync(Guid tenantId, string? previousSummary, IReadOnlyList<BotHistoryTurn> turns, CancellationToken cancellationToken);
}
