using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Privacy;
using ZaloAi.Ai.Safety;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Ai.Outreach;

/// <summary>
/// AI viết tin chủ động cho người DN dán SĐT (chủ dự án chốt 2026-11-15: lời chào mẫu cố định nghe "công nghiệp"). Mỗi người một câu
/// khác nhau theo người giới thiệu, ghi chú, nhóm dịch vụ của DN — vừa tự nhiên, vừa tránh việc Zalo gắn cờ một câu y hệt gửi nhiều người.
/// Hệ thống kiểm luật TRƯỚC khi trả (Generator + Guardrail + Fallback): tin mở đầu phải có "trợ lý AI" (CLAUDE.md mục 7), không giá /
/// con số / khuyến mãi / câu cấm của ngành, không gọi "anh/chị", đủ ngắn. Sai → thử lại 1 lần → vẫn sai thì trả rỗng, job dùng mẫu soạn sẵn.
/// Tên khách, tên người giới thiệu, ghi chú được che trước khi gửi AI.
/// </summary>
public sealed partial class OutreachWriter(IChatProvider chat, ILogger<OutreachWriter> logger) : IOutreachWriter
{
    /// <summary>Lời mời kết bạn của Zalo giới hạn độ dài (chưa kiểm chứng con số) → giữ ≤ 150 ký tự như mẫu.</summary>
    public const int InviteMaxLength = 150;

    /// <summary>Mỗi đoạn tin mở đầu không dài quá chừng này (người thật không chào bằng cả đoạn văn).</summary>
    public const int PartMaxLength = 320;

    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "outreach_messages": { "type": "array", "items": { "type": "string" }, "description": "Các đoạn tin gửi lần lượt, tiếng Việt" }
          },
          "required": ["outreach_messages"]
        }
        """;

    public async Task<OutreachMessageResult> WriteAsync(OutreachMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = IndustryTemplateRegistry.Get(request.Profile.IndustrySlug);
        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, request.CustomerName);
        vault.AddKnown(PiiMasker.Name, request.ReferrerName);
        var system = BuildSystem(template, request, vault);
        var input = BuildInput(request, vault);
        var usage = new List<BotUsage>();

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await chat.ChatAsync(
                    new ChatRequest(system, [new ChatTurn(ChatRole.User, input)], Schema, attempt == 0 ? 0.7 : 0.3, 1024),
                    cancellationToken);
                usage.Add(new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd));
                var messages = Parse(result.Text);
                var problem = messages is null ? "invalid_json" : Check(template, request.Kind, messages, vault);
                if (problem is null)
                {
                    return new OutreachMessageResult(messages!.Select(m => PiiMasker.Restore(m, vault).Trim()).ToList(), usage);
                }

                LogRejected(logger, request.Kind, problem);
            }
        }
        catch (AiProviderException ex)
        {
            LogAiFailed(logger, ex.GetType().Name);
        }

        return new OutreachMessageResult([], usage);
    }

    /// <summary>Luật bắt buộc. Kiểm trên bản ĐÃ CHE (placeholder không bị tính là con số) rồi trên bản ghép lại.</summary>
    /// <returns>null = đạt; còn lại = mã lý do (ghi log, không chứa nội dung).</returns>
    public static string? Check(IndustryTemplate template, OutreachMessageKind kind, IReadOnlyList<string> masked, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(masked);
        ArgumentNullException.ThrowIfNull(vault);
        var parts = masked.Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        var max = kind == OutreachMessageKind.Invite ? 1 : 2;
        if (parts.Count == 0 || parts.Count > max)
        {
            return "part_count";
        }

        var joined = string.Join(" ", parts);
        var restored = parts.Select(p => PiiMasker.Restore(p, vault)).ToList();
        if (kind == OutreachMessageKind.Invite ? restored[0].Length > InviteMaxLength : restored.Any(p => p.Length > PartMaxLength))
        {
            return "too_long";
        }

        // Placeholder AI tự bịa ([NAME_3]...): Restore sẽ xóa đi, để lại câu hụt chữ ("chị  giới thiệu") → không gửi.
        if (PlaceholderToken().Matches(joined).Any(m => !vault.TryGetOriginal(m.Value, out _)))
        {
            return "placeholder";
        }

        if (AnyDigit().IsMatch(PlaceholderToken().Replace(joined, "")) || PriceDetector.MentionsPrice(joined))
        {
            return "number_or_price";
        }

        // Kiểm trên bản đã che tên: tên như "Lan Anh" đứng trước "chị" không bị bắt nhầm thành "anh chị".
        var folded = VietText.Fold(joined);
        // Đã báo "trợ lý AI" ở tin trước (khách đã nói chuyện / đã nhận tin chào) thì không bắt nhắc lại.
        if (kind is not (OutreachMessageKind.AcceptedThanks or OutreachMessageKind.AcceptedIntro) && !folded.Contains(" tro ly ai ", StringComparison.Ordinal))
        {
            return "no_ai_disclosure";
        }

        // Tin giới thiệu sau khi kết bạn là chủ động tìm khách: phải mở câu hỏi, không kết kiểu chờ khách tự nhắn (chủ dự án 2026-11-25).
        if (kind == OutreachMessageKind.AcceptedIntro && PassiveClose.Any(p => folded.Contains(p, StringComparison.Ordinal)))
        {
            return "passive_close";
        }

        if (Promo.Any(p => folded.Contains(p, StringComparison.Ordinal))
            || folded.Contains(" anh chi ", StringComparison.Ordinal) || folded.Contains(" quy khach ", StringComparison.Ordinal))
        {
            return "promo_or_salutation";
        }

        return ForbiddenFilter.FindViolations(template, joined, []).Count > 0 ? "forbidden" : null;
    }

    /// <summary>Tin chủ động gửi người chưa đồng ý nhận quảng cáo → không mời ưu đãi (CLAUDE.md mục 7).</summary>
    private static readonly string[] Promo = [" khuyen mai ", " uu dai ", " giam gia ", " voucher ", " qua tang ", " mien phi "];

    /// <summary>Câu kết "chờ khách tự nhắn" — không hợp tin chủ động giới thiệu (đã bỏ dấu, có khoảng trắng hai đầu).</summary>
    private static readonly string[] PassiveClose = [" can gi cu ", " khi nao can ", " co gi cu nhan ", " cu nhan "];

    private static string BuildSystem(IndustryTemplate template, OutreachMessageRequest r, PiiVault vault)
    {
        var p = r.Profile;
        var pronoun = string.IsNullOrWhiteSpace(p.BotPronoun) ? "em" : p.BotPronoun.Trim();
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"Bạn là {p.BotName}, trợ lý AI của \"{p.BusinessName}\" ({template.Name}). Bạn soạn MỘT lượt tin Zalo gửi một người, qua nick Zalo cá nhân của doanh nghiệp.");
        s.AppendLine();
        s.AppendLine("## LUẬT (bắt buộc)");
        s.AppendLine("1. Viết như nhân viên thật đang nhắn Zalo: tự nhiên, ấm áp, ngắn, có chút riêng cho người này. KHÔNG văn mẫu, không giọng tổng đài, không markdown, không liệt kê dài. Tối đa 1 biểu tượng cảm xúc cho cả lượt.");
        s.AppendLine(CultureInfo.InvariantCulture, $"2. Xưng \"{pronoun}\". Gọi người nhận đúng như mục NGƯỜI NHẬN; không đoán giới tính; không viết \"anh/chị\", \"bạn\", \"quý khách\".");
        s.AppendLine("3. KHÔNG nêu giá, con số, khuyến mãi, ưu đãi, quà tặng, miễn phí. Không cam kết hiệu quả, không chẩn đoán, không nói chữa khỏi.");
        s.AppendLine("4. Chỉ dùng thông tin ở dưới. Không bịa người giới thiệu, dịch vụ, lý do. Các chuỗi [NAME_1]... là tên đã che: giữ NGUYÊN chuỗi khi nhắc tới, không tự tạo chuỗi mới.");
        s.AppendLine("5. Trả JSON đúng schema; mỗi phần tử outreach_messages là một tin gửi riêng.");
        s.AppendLine();
        s.AppendLine("## VIỆC CẦN VIẾT");
        s.AppendLine(r.Kind switch
        {
            OutreachMessageKind.Invite =>
                $"Lời chào đi kèm LỜI MỜI KẾT BẠN: đúng 1 tin, tối đa 130 ký tự. Chào, nói rõ mình là trợ lý AI của {p.BusinessName} (dùng đúng cụm \"trợ lý AI\"), nhắc lý do liên hệ, xin kết bạn để tiện hỗ trợ.",
            OutreachMessageKind.Greeting =>
                $"TIN ĐẦU TIÊN gửi người chưa từng nói chuyện: 1–2 tin, mỗi tin tối đa 200 ký tự. Chào, nói rõ mình là trợ lý AI của {p.BusinessName} (dùng đúng cụm \"trợ lý AI\"), nêu đúng lý do liên hệ, có thể nói rất ngắn bên mình làm gì, rồi kết bằng MỘT câu hỏi xin phép gửi thông tin phù hợp. Chưa gửi thông tin dịch vụ chi tiết.",
            OutreachMessageKind.AcceptedGreeting =>
                $"Người này VỪA CHẤP NHẬN lời mời kết bạn, chưa nói chuyện: 1–2 tin, mỗi tin tối đa 200 ký tự. Cảm ơn đã kết bạn, nói rõ mình là trợ lý AI của {p.BusinessName} (dùng đúng cụm \"trợ lý AI\"), nói sơ bên mình hỗ trợ được gì (theo NHÓM DỊCH VỤ, kiểu \"bên {pronoun} có các liệu trình … tùy nhu cầu\"), rồi kết bằng MỘT câu hỏi xin phép gửi thông tin phù hợp.",
            OutreachMessageKind.AcceptedIntro =>
                $"Người này ĐÃ NHẬN tin chào của bên mình (xem TIN GẦN NHẤT), CHƯA trả lời, giờ VỪA CHẤP NHẬN lời mời kết bạn. Bên mình chủ động giới thiệu để người này biết và quan tâm: 1–2 tin, mỗi tin tối đa 200 ký tự. Cảm ơn đã kết bạn, rồi giới thiệu {p.BusinessName}: bên {pronoun} chuyên về gì, các NHÓM DỊCH VỤ chính (chỉ dùng thông tin có ở dưới, không bịa thế mạnh). Kết bằng MỘT câu hỏi mở cụ thể để người này trả lời (vd hỏi người này đang quan tâm nhóm nào để {pronoun} gửi thông tin). KHÔNG chào lại từ đầu, không lặp câu đã nói ở tin chào, KHÔNG viết kiểu \"cần gì cứ nhắn {pronoun}\" / \"khi nào cần thì liên hệ\".",
            _ =>
                $"Người này ĐÃ NÓI CHUYỆN với bên mình (xem TIN GẦN NHẤT), giờ mới chấp nhận lời mời kết bạn: 1–2 tin ngắn. Cảm ơn đã kết bạn, rồi một câu ngắn bên mình hỗ trợ được gì (theo NHÓM DỊCH VỤ, nối với điều người này quan tâm nếu có). KHÔNG chào lại từ đầu, không lặp lại câu đã nói, không hỏi xin phép, không giới thiệu chi tiết công ty.",
        });
        if ((r.Kind is OutreachMessageKind.AcceptedThanks or OutreachMessageKind.AcceptedIntro) && r.Recent.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("## TIN GẦN NHẤT (cũ → mới)");
            foreach (var t in r.Recent.TakeLast(6))
            {
                var who = t.Sender == MessageSender.Customer ? "Người nhận" : "Bên mình";
                s.AppendLine(CultureInfo.InvariantCulture, $"- {who}: {Clip(PiiMasker.Mask(t.Text, vault), 300)}");
            }
        }

        return s.ToString();
    }

    private static string BuildInput(OutreachMessageRequest r, PiiVault vault)
    {
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"NGƯỜI NHẬN: gọi là \"{Call(r.Salutation, r.CustomerName, vault)}\".");
        var referrer = string.IsNullOrWhiteSpace(r.ReferrerName) ? null : PiiMasker.Mask(r.ReferrerName.Trim(), vault);
        s.AppendLine(CultureInfo.InvariantCulture, $"LÝ DO LIÊN HỆ: {r.Origin switch
        {
            "referral" when referrer is not null => $"{referrer} (khách của bên mình) giới thiệu người này với bên mình.",
            "referral" => "người quen của người này giới thiệu với bên mình (không biết tên người giới thiệu, đừng bịa).",
            "inquiry" => "người này từng hỏi / để lại thông tin liên hệ với bên mình.",
            _ => "người này từng dùng dịch vụ của bên mình.",
        }}");
        if (!string.IsNullOrWhiteSpace(r.Note))
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"GHI CHÚ CỦA DOANH NGHIỆP VỀ NGƯỜI NÀY: {Clip(PiiMasker.Mask(r.Note.Trim(), vault), 300)}");
        }

        if (r.Offerings.Count > 0)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"NHÓM DỊCH VỤ CỦA DOANH NGHIỆP: {string.Join("; ", r.Offerings.Take(5))}.");
        }

        return s.ToString();
    }

    /// <summary>Cách gọi: "chị [NAME_1]" khi biết cách gọi + tên; "chị"; "[NAME_1]"; không biết gì → "mình" (PromptBuilder.AppendAddressing).</summary>
    private static string Call(string? salutation, string? name, PiiVault vault)
    {
        var maskedName = string.IsNullOrWhiteSpace(name) ? null : PiiMasker.Mask(name.Trim(), vault);
        var addressing = Addressing.From(salutation, byStaff: false);
        return addressing.Known
            ? (maskedName is null ? addressing.Salutation : $"{addressing.Salutation} {maskedName}")
            : maskedName ?? CustomerSalutation.Unknown;
    }

    private static List<string>? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("outreach_messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"\d")]
    private static partial Regex AnyDigit();

    [GeneratedRegex(@"\[[A-Z]+_\d+\]")]
    private static partial Regex PlaceholderToken();

    [LoggerMessage(Level = LogLevel.Information, Message = "AI viết tin chủ động ({Kind}) chưa đạt luật: {Problem} — dùng mẫu nếu lần sau vẫn sai")]
    private static partial void LogRejected(ILogger logger, OutreachMessageKind kind, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gọi AI viết tin chủ động lỗi ({ErrorType}) — dùng mẫu soạn sẵn")]
    private static partial void LogAiFailed(ILogger logger, string errorType);
}
