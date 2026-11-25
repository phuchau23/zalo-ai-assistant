using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>
/// Vòng đời một số điện thoại được dán vào chăm sóc qua nick (chủ dự án chốt 2026-11-25) — máy trạng thái (State Machine): mỗi số luôn ở
/// đúng một trạng thái, chỉ đi theo các mũi tên dưới đây; job dọn dẹp mỗi giờ chuyển trạng thái theo thời gian.
/// <code>
/// Chờ lượt → Đã liên hệ (chào + mời + giới thiệu, hết) ─┬─ khách trả lời ─→ Đang tư vấn ─┬─ có lịch hẹn / hóa đơn → Đã thành khách
///                                                       │                                ├─ im 14 ngày → Đã nguội ─┐
///                                                       └─ im 2 ngày ─→ Cần gọi điện ←───┴─────────────────────────┘ (danh sách gọi)
/// Cần gọi điện / Đã nguội / Không có Zalo: nhân viên gọi → Quan tâm (về Đang tư vấn) | Không quan tâm (ngừng hẳn) | Không nghe máy;
/// 30 ngày không ai xử lý → Lưu trữ. Khách tự nhắn lại → về Đang tư vấn.
/// </code>
/// Dán lại số đã liên hệ: chặn 90 ngày (danh sách tạm khóa), tối đa 2 lần mỗi số. Nhắn người mới chỉ dùng 70% lượt tin/ngày của nick, phần
/// còn lại dành cho khách đang chat (Backpressure: đầu sau bận thì đầu trước chậm lại).
/// </summary>
public static class OutreachLifecycle
{
    /// <summary>Im chừng này sau tin cuối bên mình gửi → "Cần gọi điện" (trước 2026-11-25 là 7 ngày).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(2);

    /// <summary>Đang tư vấn mà khách im chừng này → "Đã nguội".</summary>
    public static readonly TimeSpan ColdAfter = TimeSpan.FromDays(14);

    /// <summary>Nằm trong danh sách gọi điện chừng này không ai xử lý → "Lưu trữ".</summary>
    public static readonly TimeSpan ArchiveAfter = TimeSpan.FromDays(30);

    /// <summary>Số đã liên hệ (nhắn / mời) mà chưa từng trả lời: dán lại phải chờ chừng này.</summary>
    public static readonly TimeSpan RecontactAfter = TimeSpan.FromDays(90);

    /// <summary>Số lần tối đa một số chưa từng trả lời được bot liên hệ chủ động (lần đầu + một lần cuối sau 90 ngày).</summary>
    public const int MaxTouches = 2;

    /// <summary>Job còn việc hẹn trong khoảng này (giới thiệu sau khi khách chấp nhận, thử lại lời mời) → chưa chuyển trạng thái.</summary>
    public static readonly TimeSpan Imminent = TimeSpan.FromHours(1);

    /// <summary>Trạng thái vào danh sách "Cần gọi điện" (đã nguội đứng đầu — từng quan tâm thật, gọi dễ chốt hơn).</summary>
    public static readonly OutreachState[] CallStates = [OutreachState.Cold, OutreachState.NoReply, OutreachState.NoZalo];

    /// <summary>Khách tự nhắn lại khi đang ở các trạng thái này → về "Đang tư vấn".</summary>
    public static readonly OutreachState[] RevivableStates = [OutreachState.NoReply, OutreachState.Cold, OutreachState.Archived, OutreachState.Expired];

    /// <summary>Có lịch hẹn / hóa đơn mới → "Đã thành khách" (người đã từ chối, đã dừng, lỗi thì giữ nguyên).</summary>
    public static readonly OutreachState[] ConvertibleStates =
    [
        OutreachState.Queued, OutreachState.Invited, OutreachState.Friend, OutreachState.Messaged, OutreachState.Replied,
        OutreachState.NoReply, OutreachState.NoZalo, OutreachState.Cold, OutreachState.Archived, OutreachState.Expired,
    ];

    /// <summary>Lý do bỏ qua khi dán lại số đã liên hệ.</summary>
    public const string ContactedRecently = "contacted_recently";

    public const string ContactedTwice = "contacted_twice";

    /// <summary>Kết quả cuộc gọi nhân viên bấm.</summary>
    public const string Interested = "interested";

    public const string NotInterested = "not_interested";

    public const string NoAnswer = "no_answer";

    public static readonly string[] CallResults = [Interested, NotInterested, NoAnswer];

    /// <summary>
    /// Số tin/ngày nick được dùng cho người MỚI (tin chào, lời mời người lạ): 70% hạn mức, 30% còn lại luôn dành cho khách đang chat —
    /// khách đang hỏi không bao giờ bị chuyển nhân viên chỉ vì hôm đó bot nhắn người mới nhiều. Vd 200 → 140.
    /// </summary>
    public static int NewOutreachMessageCap(int messagesPerDay) => messagesPerDay <= 0 ? 0 : messagesPerDay * 7 / 10;

    /// <summary>
    /// Dán lại một số chưa từng trả lời: đã liên hệ trong 90 ngày → chặn tới hết 90 ngày; đã liên hệ đủ 2 lần → chặn hẳn. null = được dán.
    /// </summary>
    /// <param name="touches">Số lần bot đã chạm (nhắn / mời được) người này.</param>
    /// <param name="lastTouch">Lần chạm gần nhất.</param>
    /// <param name="retryAfter">Lúc dán lại được (khi bị chặn tạm).</param>
    public static string? PasteBlock(int touches, DateTimeOffset? lastTouch, DateTimeOffset now, out DateTimeOffset? retryAfter)
    {
        retryAfter = null;
        if (touches <= 0 || lastTouch is null)
        {
            return null;
        }

        if (touches >= MaxTouches)
        {
            return ContactedTwice;
        }

        var until = lastTouch.Value + RecontactAfter;
        if (until > now)
        {
            retryAfter = until;
            return ContactedRecently;
        }

        return null;
    }
}
