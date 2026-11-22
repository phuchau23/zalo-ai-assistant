using ZaloAi.Ai.Safety;

namespace ZaloAi.Ai.Bot;

/// <summary>
/// Tin xã giao kết thúc ("dạ em cảm ơn nha", "ok thanks shop", "bye em"): bot vẫn đáp lại bằng AI cho tự nhiên nhưng KHÔNG cần tìm dữ liệu
/// (bớt token đầu vào + một lần gọi tạo vector — chủ dự án duyệt 2026-11-15). So khớp CẢ tin: chỉ gồm lời cảm ơn/tạm biệt và từ đệm.
/// Cố ý KHÔNG coi "ok", "ừ", "có", "được", 👍 là xã giao: đó thường là đồng ý điều bot vừa đề nghị (quy tắc 11b) → cần dữ liệu.
/// </summary>
public static class SmallTalk
{
    private const int MaxChars = 60;

    /// <summary>Cụm nhiều chữ (đã bỏ dấu) được coi là lời cảm ơn/tạm biệt.</summary>
    private static readonly string[] ClosingPhrases = [" cam on ", " cam ta ", " tam biet ", " hen gap lai ", " thank you "];

    private static readonly HashSet<string> ClosingWords = new(StringComparer.Ordinal)
    {
        "camon", "thanks", "thank", "thankyou", "tks", "thks", "thx", "bye", "byebye",
    };

    /// <summary>Từ đệm, xưng hô, cảm thán đi kèm lời cảm ơn mà không đổi ý nghĩa.</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "da", "vang", "ok", "oke", "okie", "okela", "okay", "uh", "u", "um", "uhm",
        "em", "e", "chi", "c", "anh", "a", "minh", "shop", "ban", "b", "ad", "admin", "co", "chu", "bac",
        "nha", "nhe", "nhen", "nghen", "na", "ha", "ah", "hen", "nhaa", "nhee",
        "nhieu", "lam", "rat", "xin", "chan", "thanh", "so", "much", "very", "you", "luon", "nhiu", "roi",
        "hihi", "haha", "hehe", "hi", "hjhj", "kaka",
    };

    public static bool IsClosing(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars)
        {
            return false;
        }

        var folded = VietText.Fold(text);
        var found = false;
        foreach (var phrase in ClosingPhrases)
        {
            if (folded.Contains(phrase, StringComparison.Ordinal))
            {
                found = true;
                folded = folded.Replace(phrase, " ", StringComparison.Ordinal);
            }
        }

        foreach (var word in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ClosingWords.Contains(word))
            {
                found = true;
            }
            else if (!Fillers.Contains(word))
            {
                return false; // có nội dung khác (hỏi thêm, hẹn lịch, từ chối...) → luồng bình thường
            }
        }

        return found;
    }
}
