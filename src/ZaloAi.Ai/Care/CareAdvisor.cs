using System.Globalization;
using System.Text;
using System.Text.Json;
using ZaloAi.Ai.Privacy;
using ZaloAi.Ai.Safety;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Ai.Care;

/// <summary>
/// Phân tích chăm sóc chủ động (docs/FEATURE-SPECS.md mục 2): AI chọn tự nhắn / cần nhân viên / thôi, kèm tin nháp.
/// Che dữ liệu cá nhân trước khi gửi AI, ghép lại sau. Tin nháp có câu cấm của ngành — hoặc có giá tiền khi bot tự gửi (bot không
/// được tự nêu giá ngoài câu trả lời có dẫn nguồn) — bị chặn (<see cref="CareAnalysisResult.DraftBlocked"/>), job sẽ chuyển nhân viên.
/// </summary>
public sealed class CareAdvisor(IChatProvider chat, TimeProvider time) : ICareAdvisor
{
    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["send", "ask_human", "none"], "description": "send = nhắn chăm sóc được; ask_human = cần nhân viên liên hệ; none = không cần làm gì" },
            "human_reason": { "type": ["string", "null"], "description": "Khi ask_human: complaint | sensitive | needs_staff_info | other; còn lại null" },
            "promotional": { "type": "boolean", "description": "Tin nháp có mời ưu đãi/khuyến mãi/kéo khách cũ quay lại mua (quảng cáo) không" },
            "temperature": { "type": "string", "enum": ["hot", "warm", "cold"] },
            "trigger": { "type": "string", "enum": ["price_no_close", "thinking", "asked_schedule", "complaint_followup", "win_back", "unused_package", "follow_up", "other"] },
            "reason": { "type": "string", "description": "Lý do ngắn gọn cho nhân viên (1-2 câu)" },
            "suggested_action": { "type": "string", "description": "Nên làm gì (1 câu)" },
            "draft_message": { "type": "string", "description": "Tin gửi khách, tiếng Việt, ngắn" }
          },
          "required": ["action", "human_reason", "promotional", "temperature", "trigger", "reason", "suggested_action", "draft_message"]
        }
        """;

    private static readonly string[] Triggers =
        ["price_no_close", "thinking", "asked_schedule", "complaint_followup", "win_back", "unused_package", "follow_up", "other"];

    private static readonly string[] HumanReasons = ["complaint", "sensitive", "needs_staff_info", "other"];

    public async Task<CareAnalysisResult?> AnalyzeAsync(CareAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = IndustryTemplateRegistry.Get(request.Profile.IndustrySlug);
        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, request.CustomerName);

        var system = BuildSystem(template, request, time.GetUtcNow());
        var input = BuildInput(request, vault);
        var usage = new List<BotUsage>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await chat.ChatAsync(
                new ChatRequest(system, [new ChatTurn(ChatRole.User, input)], Schema, attempt == 0 ? 0.3 : 0.0, 2048),
                cancellationToken);
            usage.Add(new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd));
            var parsed = Parse(result.Text);
            if (parsed is null)
            {
                continue;
            }

            var draft = parsed.Draft is null || parsed.Action == CareAction.None ? null : PiiMasker.Restore(parsed.Draft.Trim(), vault);
            var sources = request.Messages.Select(m => m.Text).Concat(request.Notes.Select(n => n.Text)).ToList();
            var blocked = draft is not null
                && (ForbiddenFilter.FindViolations(template, draft, sources).Count > 0 || (request.BotWillSend && PriceDetector.MentionsPrice(draft)));

            return parsed with
            {
                Reason = PiiMasker.Restore(parsed.Reason.Trim(), vault),
                SuggestedAction = parsed.SuggestedAction is null ? null : PiiMasker.Restore(parsed.SuggestedAction.Trim(), vault),
                Draft = blocked ? null : draft,
                DraftBlocked = blocked,
                Usage = usage,
            };
        }

        return null;
    }

    internal static string BuildSystem(IndustryTemplate template, CareAnalysisRequest request, DateTimeOffset now)
    {
        var p = request.Profile;
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"Bạn phụ trách chăm sóc khách hàng chủ động cho \"{p.BusinessName}\" (ngành: {template.Name}). Đọc lại cuộc trò chuyện và ghi chú của nhân viên, quyết định có nên chủ động nhắn khách không.");
        s.AppendLine(CultureInfo.InvariantCulture, $"Thời gian hiện tại: {now.ToOffset(TimeSpan.FromHours(7)).ToString("dddd, dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("vi-VN"))} (giờ Việt Nam).");
        s.AppendLine();
        s.AppendLine("## Chọn action");
        s.AppendLine("- send: khách còn nhu cầu chưa xong và một tin hỏi thăm/nhắc nhẹ là phù hợp — hỏi giá/dịch vụ mà chưa chốt, nói \"để suy nghĩ\", hỏi lịch mà chưa đặt, đã dùng dịch vụ cần hỏi thăm, tới giờ hẹn chăm sóc lại.");
        s.AppendLine("- ask_human: cần NGƯỜI liên hệ — khách phàn nàn/không hài lòng (human_reason = complaint); chuyện sức khỏe, triệu chứng, phản ứng sau dịch vụ, chuyện nhạy cảm (sensitive); khách cần thông tin chỉ nhân viên có như lịch trống cụ thể, giá đặc biệt, xác nhận đặt chỗ (needs_staff_info).");
        s.AppendLine("- none: khách đã chốt/đặt xong và không còn gì cần hỏi, chỉ chào hỏi xã giao, đã từ chối rõ ràng, hoặc nhắn thêm sẽ làm phiền.");
        s.AppendLine("- promotional = true nếu tin nháp mời ưu đãi, khuyến mãi, giảm giá, hoặc chỉ nhằm kéo khách cũ quay lại mua. Hỏi thăm sau dịch vụ, nhắc điều khách đang hỏi dở thì promotional = false.");
        s.AppendLine("- temperature: hot = sắp mua/đặt (hỏi giá cụ thể, hỏi lịch, để lại số điện thoại); warm = có quan tâm nhưng chưa rõ; cold = ít khả năng.");
        s.AppendLine("- reason và suggested_action viết cho nhân viên, ngắn gọn, nêu đúng điều khách đã nói. Không suy đoán điều không có trong dữ liệu.");
        s.AppendLine();
        s.AppendLine("## Tin gửi khách (draft_message)");
        if (request.BotWillSend)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"- Tin này do TRỢ LÝ AI {p.BotName} của \"{p.BusinessName}\" tự gửi cho khách (khách đã biết đang nói chuyện với trợ lý AI). Xưng \"{p.BotPronoun}\", gọi khách là \"{template.CustomerAddress}\" (hoặc theo cách khách tự xưng trong hội thoại). KHÔNG giả làm nhân viên, không ký tên người.");
            s.AppendLine("- Ngắn (1-3 câu), tự nhiên, nhắc đúng điều khách đã quan tâm, kết bằng một câu hỏi mở để khách dễ trả lời. Không dồn ép mua hàng.");
            s.AppendLine("- KHÔNG nêu bất kỳ con số giá tiền nào, KHÔNG nhắc ưu đãi/khuyến mãi/lịch trống/chính sách. Nếu cần các thông tin đó thì chọn ask_human.");
        }
        else
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"- Nhân viên sẽ đọc, SỬA rồi mới gửi. Viết như nhân viên chăm sóc khách hàng của \"{p.BusinessName}\": lịch sự, ngắn (2-4 câu), gọi khách là \"{template.CustomerAddress}\" (hoặc theo cách khách tự xưng trong hội thoại), xưng \"em\".");
            s.AppendLine("- KHÔNG bịa giá, khuyến mãi, ưu đãi, lịch trống, chính sách. Chỉ nhắc lại thông tin đã có trong hội thoại. Cần ưu đãi/lịch cụ thể thì để chỗ trống dạng \"(nhân viên điền ưu đãi nếu có)\".");
        }

        s.AppendLine("- Không chẩn đoán, không hứa kết quả.");
        s.AppendLine("- Giữ nguyên các mã như [NAME_1], [PHONE_1] nếu cần dùng (hệ thống tự thay bằng giá trị thật).");
        s.AppendLine("- action = none thì draft_message để chuỗi rỗng. action = ask_human vẫn viết tin nháp để nhân viên dùng.");
        if (request.FollowUpNote is not null)
        {
            s.AppendLine();
            s.AppendLine("## Lý do phân tích lần này");
            s.AppendLine("Bây giờ là lúc nhân viên đã hẹn chăm sóc lại khách (xem ghi chú hẹn). Trừ khi khách đã từ chối rõ ràng, chọn send hoặc ask_human với trigger = follow_up; tin nhắn bám theo nội dung ghi chú hẹn.");
        }

        return s.ToString();
    }

    private static string BuildInput(CareAnalysisRequest request, PiiVault vault)
    {
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"Trạng thái khách hiện tại: {request.LeadStatus.ToString().ToLowerInvariant()}");
        if (request.LastCustomerMessageAt is { } last)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Tin cuối của khách: {last.ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}");
        }

        if (request.FollowUpNote is { } note)
        {
            s.AppendLine().AppendLine("Ghi chú hẹn chăm sóc lại:").AppendLine(NoteLine(note, vault));
        }

        if (request.Notes.Count > 0)
        {
            s.AppendLine().AppendLine("Ghi chú của nhân viên (cũ → mới):");
            foreach (var n in request.Notes)
            {
                s.AppendLine(NoteLine(n, vault));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Summary))
        {
            s.AppendLine().AppendLine("Tóm tắt hội thoại cũ:").AppendLine(PiiMasker.Mask(request.Summary, vault));
        }

        s.AppendLine().AppendLine("Các tin gần nhất (cũ → mới):");
        foreach (var m in request.Messages)
        {
            var who = m.Sender switch
            {
                MessageSender.Customer => "Khách",
                MessageSender.Staff => "Nhân viên",
                MessageSender.System => "Hệ thống",
                _ => "Trợ lý AI",
            };
            s.AppendLine(CultureInfo.InvariantCulture, $"{who}: {PiiMasker.Mask(m.Text, vault)}");
        }

        return s.ToString();
    }

    private static string NoteLine(CareNoteInput n, PiiVault vault)
    {
        var kind = n.Kind switch
        {
            "service" => "Đã dùng dịch vụ",
            "appointment" => "Lịch hẹn",
            _ => "Ghi chú",
        };
        var follow = n.FollowUpAt is { } f ? $" (hẹn chăm sóc lại lúc {f.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm dd/MM/yyyy", CultureInfo.InvariantCulture)})" : "";
        return $"- {n.HappenedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} · {kind}{follow}: {PiiMasker.Mask(n.Text, vault)}";
    }

    internal static CareAnalysisResult? Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            CareAction? action = Str(root, "action") switch
            {
                "send" => CareAction.Send,
                "ask_human" => CareAction.AskHuman,
                "none" => CareAction.None,
                _ => null,
            };
            if (action is null || Str(root, "reason") is not { } reason)
            {
                return null;
            }

            var temperature = Str(root, "temperature") switch
            {
                "hot" => CareTemperature.Hot,
                "warm" => CareTemperature.Warm,
                _ => CareTemperature.Cold,
            };
            var trigger = Str(root, "trigger") is { } t && Triggers.Contains(t) ? t : "other";
            var humanReason = Str(root, "human_reason") is { } h && HumanReasons.Contains(h) ? h : null;
            return new CareAnalysisResult(
                action.Value,
                temperature,
                trigger,
                Clip(reason, 500),
                Str(root, "suggested_action") is { Length: > 0 } suggested ? Clip(suggested, 500) : null,
                Str(root, "draft_message") is { Length: > 0 } draft ? Clip(draft, 1500) : null,
                [],
                action == CareAction.AskHuman ? humanReason ?? "other" : null,
                root.TryGetProperty("promotional", out var promo) && promo.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
