using System.Globalization;
using System.Text;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Customers;

public enum CareDecisionKind
{
    /// <summary>Không làm gì (không cần, khách đã từ chối nhận tin, bot vừa nhắn và khách chưa trả lời...).</summary>
    Nothing = 0,

    /// <summary>Bot tự nhắn vào lúc <see cref="CareDecisionResult.SendAt"/>.</summary>
    AutoSend = 1,

    /// <summary>Cần nhân viên: lưu gợi ý + báo hộp thư/Telegram.</summary>
    Escalate = 2,

    /// <summary>DN chọn "chỉ gợi ý": lưu gợi ý, nhân viên tự quyết.</summary>
    Suggest = 3,
}

/// <param name="Reason">Mã ngắn khi Escalate (complaint, sensitive, needs_staff_info, draft_blocked, promotional_no_consent,
/// staff_handling, outside_window, other) hoặc lý do Nothing (opted_out, awaiting_reply).</param>
public sealed record CareDecisionResult(CareDecisionKind Kind, string? Reason = null, DateTimeOffset? SendAt = null);

/// <summary>
/// Giới hạn CỨNG của hệ thống cho tin bot tự nhắn (CLAUDE.md mục 7) — AI chỉ đề xuất, các luật ở đây quyết định:
/// khách đã từ chối → không nhắn; bot đã nhắn mà khách chưa trả lời → không nhắn tin thứ hai (trừ lịch hẹn nhân viên đặt);
/// cách tin chủ động trước ≥ 24 giờ; chỉ trong khung giờ (DN chọn, luôn nằm trong 07:00–21:00 giờ Việt Nam); quá hạn nhắn Zalo
/// (7 ngày) → nhân viên gọi điện; tin quảng cáo (chưa có dữ liệu đồng ý nhận tin) → nhân viên; nhân viên đang xử lý → nhân viên.
/// </summary>
public static class CareDecision
{
    public static readonly TimeSpan MinProactiveGap = TimeSpan.FromHours(24);

    public static readonly TimeOnly HardStart = new(7, 0);

    public static readonly TimeOnly HardEnd = new(21, 0);

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static CareDecisionResult Decide(
        CareAnalysisResult result,
        Contact contact,
        Conversation conversation,
        HandoffSettings settings,
        bool isFollowUp,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(settings);

        if (result.Action == CareAction.None)
        {
            return new(CareDecisionKind.Nothing);
        }

        if (result.Action == CareAction.AskHuman)
        {
            return new(CareDecisionKind.Escalate, result.HumanReason ?? "other");
        }

        // Action = Send
        if (contact.ProactiveOptOutAt is not null)
        {
            return new(CareDecisionKind.Nothing, "opted_out");
        }

        if (!settings.CareAutoSend)
        {
            return new(CareDecisionKind.Suggest);
        }

        if (result.DraftBlocked || string.IsNullOrWhiteSpace(result.Draft))
        {
            return new(CareDecisionKind.Escalate, "draft_blocked");
        }

        if (result.Promotional)
        {
            return new(CareDecisionKind.Escalate, "promotional_no_consent");
        }

        if (conversation.Mode == ConversationMode.Human)
        {
            return new(CareDecisionKind.Escalate, "staff_handling");
        }

        if (contact.ProactiveAwaitingReply && !isFollowUp)
        {
            return new(CareDecisionKind.Nothing, "awaiting_reply");
        }

        var earliest = contact.LastProactiveAt is { } last && last + MinProactiveGap > now ? last + MinProactiveGap : now;
        var sendAt = NextSendTime(settings, earliest);
        var deadline = MessagingDeadline(conversation);
        if (deadline is not null && sendAt >= deadline)
        {
            return new(CareDecisionKind.Escalate, "outside_window");
        }

        return new(CareDecisionKind.AutoSend, SendAt: sendAt);
    }

    /// <summary>Hạn còn nhắn được qua kênh: Zalo 7 ngày sau tin cuối của khách; chat thử không giới hạn.</summary>
    public static DateTimeOffset? MessagingDeadline(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Channel == ChannelKind.Zalo && conversation.LastCustomerMessageAt is { } last ? last + TimeSpan.FromDays(7) : null;
    }

    /// <summary>Khung giờ được nhắn (giờ Việt Nam), luôn bị kẹp trong 07:00–21:00 dù DN cài gì.</summary>
    public static (TimeOnly Start, TimeOnly End) SendWindow(HandoffSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var start = TimeOnly.TryParseExact(settings.CareSendStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) ? s : new TimeOnly(8, 0);
        var end = TimeOnly.TryParseExact(settings.CareSendEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : new TimeOnly(20, 0);
        start = start < HardStart ? HardStart : start;
        end = end > HardEnd ? HardEnd : end;
        return start < end ? (start, end) : (HardStart, HardEnd);
    }

    public static bool InSendWindow(HandoffSettings settings, DateTimeOffset at)
    {
        var (start, end) = SendWindow(settings);
        var local = TimeOnly.FromTimeSpan(at.ToOffset(Vietnam).TimeOfDay);
        return local >= start && local < end;
    }

    /// <summary>Thời điểm sớm nhất ≥ <paramref name="from"/> nằm trong khung giờ được nhắn.</summary>
    public static DateTimeOffset NextSendTime(HandoffSettings settings, DateTimeOffset from)
    {
        if (InSendWindow(settings, from))
        {
            return from;
        }

        var (start, _) = SendWindow(settings);
        var local = from.ToOffset(Vietnam);
        var day = TimeOnly.FromTimeSpan(local.TimeOfDay) < start ? local.Date : local.Date.AddDays(1);
        return new DateTimeOffset(day + start.ToTimeSpan(), Vietnam).ToUniversalTime();
    }

    /// <summary>
    /// Khách nhắn đúng một lệnh từ chối nhận tin ("hủy", "dừng", "stop", "đừng nhắn nữa"...). So khớp CẢ tin (bỏ dấu, dấu câu) để
    /// "hủy lịch hẹn thứ 7" không bị hiểu là từ chối nhận tin.
    /// </summary>
    public static bool IsOptOutCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }

        return OptOutPhrases.Contains(Normalize(text));
    }

    private static readonly HashSet<string> OptOutPhrases = new(StringComparer.Ordinal)
    {
        // Không có "dung" đứng một mình: bỏ dấu thì "đúng" (đồng ý) cũng thành "dung".
        "huy", "stop", "unsubscribe", "dung lai", "huy nhan tin", "dung nhan tin", "dung nhan", "dung gui", "dung gui tin",
        "dung nhan nua", "dung nhan tin nua", "khong nhan tin nua", "khong muon nhan tin", "dung lam phien", "huy dang ky",
    };

    private static string Normalize(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }
}
