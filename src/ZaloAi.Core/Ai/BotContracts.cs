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
public sealed record BotTurnRequest(
    Guid TenantId,
    BotProfile Profile,
    string CustomerMessage,
    IReadOnlyList<BotHistoryTurn> History,
    string? Summary,
    bool IsFirstBotReply,
    string? CustomerName = null);

public sealed record BotUsage(UsageKind Kind, string Provider, string Model, int InputTokens, int OutputTokens, decimal CostUsd);

/// <summary>
/// Kết quả một lượt trả lời. <see cref="Reply"/> đã qua mọi kiểm tra (câu cấm, khẩn cấp...) và sẵn sàng gửi khách.
/// <see cref="TraceJson"/> không chứa nội dung tin hay dữ liệu cá nhân (an toàn để lưu dạng thường).
/// </summary>
/// <param name="Confidence">high | medium | low</param>
/// <param name="Sentiment">positive | neutral | negative, null nếu không rõ</param>
/// <param name="LeadFields">Thông tin khách tự cung cấp (tên, SĐT...) — dữ liệu cá nhân, phải mã hóa khi lưu.</param>
public sealed record BotTurnResult(
    string Reply,
    bool NeedsHuman,
    string? HandoffReason,
    Urgency Urgency,
    string Confidence,
    string? Sentiment,
    IReadOnlyDictionary<string, string> LeadFields,
    string TraceJson,
    IReadOnlyList<BotUsage> Usage);

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
