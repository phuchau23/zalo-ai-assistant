using ZaloAi.Core.Entities;

namespace ZaloAi.Core.Channels;

/// <param name="ExternalUserId">Id khách trên kênh (Zalo user id; webchat: id phiên chat thử).</param>
/// <param name="ConnectionId">Kênh đã kết nối dùng để gửi (Zalo OA). null với webchat.</param>
public sealed record OutgoingMessage(Guid TenantId, Guid ConversationId, Guid MessageId, string ExternalUserId, string Text, Guid? ConnectionId = null);

/// <param name="ExternalMessageId">Id tin do kênh cấp (null nếu kênh không có, như webchat).</param>
public sealed record SendResult(string? ExternalMessageId);

/// <summary>
/// Gửi tin ra một kênh (webchat, Zalo, sau này Messenger...). Worker chỉ biết interface này, thêm kênh không sửa worker.
/// Lỗi gửi → ném exception để job thử lại; adapter tự bảo đảm không gửi trùng khi thử lại (theo MessageId).
/// </summary>
public interface IChannelAdapter
{
    ChannelKind Channel { get; }

    Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Không thể gửi và gửi lại cũng vô ích (khách không còn nhận được tin, kênh mất quyền...). Job đánh dấu tin Failed, không thử lại.
/// <see cref="Code"/> ngắn, không chứa nội dung (vd "zalo:-230").
/// </summary>
public sealed class ChannelPermanentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
