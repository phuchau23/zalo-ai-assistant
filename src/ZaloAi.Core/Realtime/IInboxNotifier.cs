namespace ZaloAi.Core.Realtime;

/// <summary>
/// Sự kiện hộp thư gửi tới trình duyệt của nhân viên (SSE). Chỉ mang id + loại, KHÔNG mang nội dung tin —
/// trình duyệt nhận sự kiện rồi tự gọi API (có kiểm quyền) để tải lại.
/// </summary>
/// <param name="Type">message (tin mới) | attention (cần người) | reminder (chờ lâu) | updated (đổi trạng thái/người phụ trách)</param>
public sealed record InboxEvent(string Type, Guid ConversationId);

/// <summary>Phát sự kiện hộp thư theo tenant (Redis pub/sub: Worker phát, mọi bản Api nhận).</summary>
public interface IInboxNotifier
{
    Task PublishAsync(Guid tenantId, InboxEvent inboxEvent, CancellationToken cancellationToken);
}
