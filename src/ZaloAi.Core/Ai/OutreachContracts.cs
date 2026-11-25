namespace ZaloAi.Core.Ai;

/// <summary>Loại tin chủ động gửi người DN dán SĐT (docs/ZALO-PERSONAL-PLAN.md mục 6.7, 6.9; chủ dự án chốt 2026-11-15).</summary>
public enum OutreachMessageKind
{
    /// <summary>Lời chào kèm lời mời kết bạn (Zalo giới hạn ~150 ký tự, 1 đoạn).</summary>
    Invite,

    /// <summary>Tin đầu nhắn thẳng cho người chưa từng nói chuyện: chào, báo trợ lý AI, lý do, hỏi xin phép.</summary>
    Greeting,

    /// <summary>Khách vừa chấp nhận lời mời, chưa nói chuyện: cảm ơn, báo trợ lý AI, bên mình giúp được gì, hỏi xin phép.</summary>
    AcceptedGreeting,

    /// <summary>Khách đã nói chuyện rồi mới chấp nhận lời mời: cảm ơn + giới thiệu sơ bên mình giúp được gì, không chào lại.</summary>
    AcceptedThanks,

    /// <summary>
    /// Đã nhận tin chào (nhắn trước) rồi chấp nhận lời mời, CHƯA trả lời: cảm ơn + giới thiệu doanh nghiệp (nhóm dịch vụ) + một câu hỏi mở
    /// để khách phản hồi — chủ động tìm khách, không "cần gì cứ nhắn" (chủ dự án chốt 2026-11-25).
    /// </summary>
    AcceptedIntro,
}

/// <summary>Thông tin để AI viết tin chủ động. Tên/ghi chú là bản gốc — bộ viết tự che trước khi gửi AI.</summary>
/// <param name="Origin">referral | inquiry | customer — lý do liên hệ DN đã khai khi dán số.</param>
/// <param name="Note">Ghi chú cho AI của đợt dán (vd "quan tâm giảm mỡ bụng"); null nếu không có.</param>
/// <param name="Offerings">Nhóm dịch vụ / gói tiêu biểu của DN (để nói "bên em hỗ trợ được gì"), không có giá.</param>
/// <param name="Recent">Các tin gần nhất (cũ → mới) — chỉ dùng cho <see cref="OutreachMessageKind.AcceptedThanks"/>.</param>
public sealed record OutreachMessageRequest(
    Guid TenantId,
    BotProfile Profile,
    OutreachMessageKind Kind,
    string? CustomerName,
    string? Salutation,
    string Origin,
    string? ReferrerName,
    string? Note,
    IReadOnlyList<string> Offerings,
    IReadOnlyList<BotHistoryTurn> Recent);

/// <param name="Messages">Các đoạn đã qua kiểm tra. Rỗng = AI lỗi hoặc viết sai luật → nơi gọi dùng mẫu soạn sẵn.</param>
public sealed record OutreachMessageResult(IReadOnlyList<string> Messages, IReadOnlyList<BotUsage> Usage);

/// <summary>
/// AI viết tin chủ động cho từng người (tự nhiên, không văn mẫu) — hệ thống kiểm luật trước khi trả: có câu báo trợ lý AI (với tin mở đầu),
/// không giá / con số / câu cấm, đủ ngắn. Sai luật → trả rỗng để nơi gọi dùng mẫu (Generator + Guardrail + Fallback).
/// </summary>
public interface IOutreachWriter
{
    Task<OutreachMessageResult> WriteAsync(OutreachMessageRequest request, CancellationToken cancellationToken);
}

/// <summary>Gộp các đoạn trả lời cho vừa số tin tối đa (giữ thứ tự, đoạn thừa nối vào đoạn cuối).</summary>
public static class MessageParts
{
    public static IReadOnlyList<string> Fit(IReadOnlyList<string> messages, int max)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (max < 1 || messages.Count <= max)
        {
            return messages;
        }

        var kept = messages.Take(max - 1).ToList();
        kept.Add(string.Join("\n", messages.Skip(max - 1)));
        return kept;
    }
}
