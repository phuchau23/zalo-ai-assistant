using ZaloAi.Core.Entities;

namespace ZaloAi.Core.Channels;

/// <param name="ExternalUserId">Id khách trên kênh (Zalo user id; webchat: id phiên chat thử).</param>
public sealed record OutgoingMessage(Guid TenantId, Guid ConversationId, Guid MessageId, string ExternalUserId, string Text);

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
