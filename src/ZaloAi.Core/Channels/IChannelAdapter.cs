using ZaloAi.Core.Entities;

namespace ZaloAi.Core.Channels;

/// <param name="ExternalUserId">Id khách trên kênh (Zalo user id; webchat: id phiên chat thử).</param>
/// <param name="ConnectionId">Kênh đã kết nối dùng để gửi (Zalo OA, nick Zalo cá nhân). null với webchat.</param>
/// <param name="Sender">Ai gửi — kênh Zalo cá nhân chỉ giả "đang soạn tin" cho tin của bot, tin nhân viên gửi ngay.</param>
/// <param name="Proactive">Tin bot chủ động nhắn (chăm sóc), không phải trả lời khách — đã được kiểm tra lượt trước khi xếp.</param>
/// <param name="AnswersExternalMessageId">
/// Tin bot này trả lời tin khách có id kênh này. Kênh có "đang soạn" (Zalo cá nhân) bỏ tin nếu trong lúc soạn khách đã nhắn tin mới hơn
/// (<see cref="MessageSupersededException"/>), để đoạn trả lời cũ không chen sau tin mới của khách. null = luôn gửi.
/// </param>
public sealed record OutgoingMessage(
    Guid TenantId,
    Guid ConversationId,
    Guid MessageId,
    string ExternalUserId,
    string Text,
    Guid? ConnectionId = null,
    MessageSender Sender = MessageSender.Bot,
    bool Proactive = false,
    string? AnswersExternalMessageId = null);

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
public sealed class ChannelPermanentException(string code, string message, bool channelBlocked = false) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>
    /// Kênh chặn MỌI tin (vd OA chưa mua gói Zalo cho tính năng gửi tin) chứ không riêng khách này → bot không trả lời được ai,
    /// cần chuyển nhân viên trả lời tay và báo chủ DN.
    /// </summary>
    public bool ChannelBlocked { get; } = channelBlocked;
}

/// <summary>Kênh tạm thời không gửi được (mạng, máy chủ kết nối chưa sẵn sàng...). Job thử lại sau, tin vẫn pending.</summary>
public sealed class ChannelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Khách đã nhắn tin mới hơn tin bot đang trả lời → kênh không gửi tin này (đánh dấu Skipped, không thử lại).</summary>
public sealed class MessageSupersededException() : Exception("Khách đã nhắn tin mới hơn, bỏ đoạn trả lời cũ.");
