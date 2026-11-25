using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>
/// MẪU DỰ PHÒNG cho tin chủ động gửi người DN dán SĐT (docs/ZALO-PERSONAL-PLAN.md mục 6.7, 6.9). Từ 2026-11-15 AI viết riêng từng người
/// (<see cref="ZaloAi.Core.Ai.IOutreachWriter"/>); mẫu này chỉ dùng khi AI lỗi hoặc viết sai luật. Mọi mẫu bảo đảm: (1) tin mở đầu báo là
/// trợ lý AI, (2) nói rõ lý do liên hệ, (3) chỉ HỎI XIN PHÉP, (4) không giá / ưu đãi. Mỗi loại có vài biến thể chọn ngẫu nhiên để không
/// gửi một câu y hệt cho nhiều người (kiểu Zalo dễ gắn cờ spam).
/// </summary>
public static class OutreachGreeting
{
    /// <summary>Lời mời kết bạn của Zalo giới hạn độ dài (chưa kiểm chứng con số) → cắt an toàn.</summary>
    public const int InviteMaxLength = 150;

    public const int FirstMessageVariants = 3;

    public const int AcceptedVariants = 2;

    /// <param name="salutation">Cách gọi đã lưu (anh/chị/cô/chú/bác) hoặc null.</param>
    /// <param name="origin">referral | inquiry | customer</param>
    public static string FirstMessage(
        string botName, string botPronoun, string businessName, string? salutation, string? customerName, string origin, string? referrerName, int variant = 0)
    {
        var p = Pronoun(botPronoun);
        var (call, callCap) = Call(salutation, customerName);
        var reason = Reason(origin, referrerName, call, p, businessName);
        return variant switch
        {
            1 => $"Dạ {p} chào {call} ạ 😊 {Cap(p)} là {botName}, trợ lý AI bên {businessName}. {Cap(reason)}, nên {p} xin phép nhắn làm quen với {call} một chút. "
                 + $"{callCap} có cần {p} gửi thông tin gì không ạ?",
            2 => $"{Cap(p)} chào {call} nha, {p} là {botName} — trợ lý AI của {businessName} ạ. {Cap(reason)}. "
                 + $"Không biết {call} có muốn {p} gửi vài thông tin để tham khảo không ạ?",
            _ => $"Dạ {p} chào {call}! {Cap(p)} là {botName}, trợ lý AI của {businessName}. {Cap(reason)} ạ. "
                 + $"{callCap} có muốn {p} gửi thông tin dịch vụ phù hợp không ạ?",
        };
    }

    /// <summary>Lời chào ngắn kèm lời mời kết bạn (≤ <see cref="InviteMaxLength"/> ký tự).</summary>
    public static string Invite(string botPronoun, string businessName, string? salutation, string? customerName, string origin, string? referrerName)
    {
        var pronoun = Pronoun(botPronoun);
        var (call, _) = Call(salutation, customerName);
        var text = origin == "referral" && !string.IsNullOrWhiteSpace(referrerName)
            ? $"Dạ {pronoun} chào {call}, {pronoun} là trợ lý AI của {businessName}. {referrerName.Trim()} có giới thiệu {call} với bên {pronoun} ạ."
            : $"Dạ {pronoun} chào {call}, {pronoun} là trợ lý AI của {businessName}, xin phép kết bạn để hỗ trợ {call} ạ.";
        return Fit(text);
    }

    /// <summary>
    /// Lời mời kết bạn gửi SAU khi khách đã trả lời tin chào (≤ <see cref="InviteMaxLength"/> ký tự): khách đã biết bên mình, chỉ nhắc
    /// lại là ai (vẫn báo trợ lý AI — thẻ lời mời hiện tách khỏi khung chat) và vì sao kết bạn.
    /// </summary>
    public static string InviteAfterReply(string botName, string botPronoun, string businessName, string? salutation, string? customerName)
    {
        var pronoun = Pronoun(botPronoun);
        var (call, _) = Call(salutation, customerName);
        return Fit($"Dạ {pronoun} là {botName}, trợ lý AI của {businessName} đang nhắn với {call} ạ. Kết bạn để {pronoun} tiện gửi thông tin, hỗ trợ {call} nha.");
    }

    /// <summary>Khách vừa chấp nhận lời mời, chưa nói chuyện: cảm ơn, báo trợ lý AI, bên mình giúp được gì, hỏi xin phép.</summary>
    public static string AcceptedGreeting(
        string botName, string botPronoun, string businessName, string? salutation, string? customerName, IReadOnlyList<string> offerings, int variant = 0)
    {
        var p = Pronoun(botPronoun);
        var (call, callCap) = Call(salutation, customerName);
        var offer = Offer(offerings);
        return variant == 1
            ? $"Cảm ơn {call} đã đồng ý kết bạn nha 😊 {Cap(p)} là {botName}, trợ lý AI bên {businessName}. Bên {p} có {offer}. "
              + $"{callCap} đang quan tâm gì để {p} gửi thông tin cho đúng ạ?"
            : $"Dạ {p} cảm ơn {call} đã kết bạn ạ 😊 {Cap(p)} là {botName}, trợ lý AI của {businessName}. Bên {p} có {offer}, tùy nhu cầu mỗi người. "
              + $"{callCap} có muốn {p} gửi thông tin phù hợp không ạ?";
    }

    /// <summary>Đã nói chuyện rồi mới chấp nhận lời mời: cảm ơn + giới thiệu sơ bên mình giúp được gì, không chào lại, không hỏi xin phép.</summary>
    public static string AcceptedThanks(string botPronoun, string? salutation, string? customerName, IReadOnlyList<string> offerings, int variant = 0)
    {
        var p = Pronoun(botPronoun);
        var (call, _) = Call(salutation, customerName);
        var offer = Offer(offerings);
        return variant == 1
            ? $"Cảm ơn {call} đã kết bạn nha 😊 Có gì về {offer} {call} cứ hỏi {p}, {p} hỗ trợ liền ạ."
            : $"Dạ {p} cảm ơn {call} đã kết bạn với {p} ạ 😊 Bên {p} có {offer}, {call} cần gì cứ nhắn {p} nha.";
    }

    /// <summary>
    /// Đã nhận tin chào rồi chấp nhận lời mời, chưa trả lời: cảm ơn + giới thiệu doanh nghiệp + MỘT câu hỏi mở để khách trả lời (chủ động tìm
    /// khách — không "cần gì cứ nhắn", chủ dự án chốt 2026-11-25). Không chào lại, không báo trợ lý AI lần nữa (tin chào đã báo).
    /// </summary>
    public static string AcceptedIntro(string botPronoun, string businessName, string? salutation, string? customerName, IReadOnlyList<string> offerings, int variant = 0)
    {
        var p = Pronoun(botPronoun);
        var (call, callCap) = Call(salutation, customerName);
        var offer = Offer(offerings);
        return variant == 1
            ? $"Cảm ơn {call} đã đồng ý kết bạn nha 😊 {businessName} bên {p} chuyên {offer}, mỗi người một liệu trình riêng theo nhu cầu. "
              + $"{callCap} đang quan tâm phần nào để {p} gửi thông tin chi tiết cho mình ạ?"
            : $"Dạ {p} cảm ơn {call} đã kết bạn với {p} ạ 😊 Bên {p} là {businessName}, có {offer}. "
              + $"{callCap} muốn {p} gửi thông tin phần nào trước ạ?";
    }

    /// <summary>"các liệu trình trị liệu chuyên sâu, dưỡng sinh tạng và gói dưỡng sinh" — từ nhóm dịch vụ của DN, không giá.</summary>
    public static string Offer(IReadOnlyList<string> offerings)
    {
        ArgumentNullException.ThrowIfNull(offerings);
        var names = offerings.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => Lower(o.Trim())).Take(3).ToList();
        return names.Count switch
        {
            0 => "các dịch vụ chăm sóc tùy nhu cầu",
            1 => $"các liệu trình {names[0]}",
            _ => $"các liệu trình {string.Join(", ", names.Take(names.Count - 1))} và {names[^1]}",
        };
    }

    private static string Reason(string origin, string? referrerName, string call, string pronoun, string businessName) => origin switch
    {
        "referral" when !string.IsNullOrWhiteSpace(referrerName) => $"{referrerName.Trim()} có giới thiệu {call} với bên {pronoun}",
        "referral" => $"bên {pronoun} được người quen của {call} giới thiệu",
        "inquiry" => $"{call} có để lại thông tin liên hệ với bên {pronoun}",
        _ => $"cảm ơn {call} đã từng ghé {businessName}",
    };

    private static string Fit(string text) => text.Length <= InviteMaxLength ? text : text[..(InviteMaxLength - 1)] + "…";

    /// <summary>
    /// Cách gọi khách: biết giới tính → "chị Hoa" / "chị"; chưa biết → tên (nếu có) hoặc "mình" (CustomerSalutation: không viết "anh/chị").
    /// </summary>
    private static (string Call, string Capitalized) Call(string? salutation, string? customerName)
    {
        var name = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim();
        var call = CustomerSalutation.IsAllowed(salutation)
            ? (name is null ? salutation! : $"{salutation} {name}")
            : name ?? CustomerSalutation.Unknown;
        return (call, Cap(call));
    }

    private static string Pronoun(string? botPronoun) => string.IsNullOrWhiteSpace(botPronoun) ? "em" : botPronoun.Trim();

    private static string Cap(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Lower(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
