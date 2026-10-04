using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Ai.Privacy;
using ZaloAi.Ai.Safety;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Ai.Bot;

/// <summary>
/// Bộ não trả lời khách (CLAUDE.md M3, INDUSTRIES.md mục 2–3). Không đọc/ghi database.
/// Thứ tự: che dữ liệu cá nhân → dấu hiệu nguy hiểm (trước khi gọi AI) → tìm kiến thức trong kho tenant → gọi AI (JSON)
/// → lỗi định dạng thử lại 1 lần → câu cấm thử viết lại 1 lần → chặn giá không có nguồn → nhắc thăm khám (ngành y)
/// → ghép lại dữ liệu đã che → câu báo trợ lý AI ở tin đầu tiên. Lỗi AI → câu dự phòng + chuyển người (không im lặng).
/// </summary>
public sealed partial class BotEngine(
    IChatProvider chat,
    IKnowledgeSearch search,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<BotEngine> logger) : IBotEngine
{
    private const int MaxOutputTokens = 4096;
    private readonly AiOptions _options = options.Value;

    public async Task<BotTurnResult> ReplyAsync(BotTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = IndustryTemplateRegistry.Get(request.Profile.IndustrySlug);
        var texts = new BotTexts(template, request.Profile);
        var trace = new TurnTrace(template);
        var usage = new List<BotUsage>();

        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, request.CustomerName);
        var maskedSummary = request.Summary is null ? null : PiiMasker.Mask(request.Summary, vault);
        var maskedHistory = request.History
            .TakeLast(_options.BotHistoryTurns)
            .Select(t => new BotHistoryTurn(t.Sender, PiiMasker.Mask(t.Text, vault)))
            .ToList();
        var maskedMessage = PiiMasker.Mask(request.CustomerMessage, vault);

        // 1. Dấu hiệu nguy hiểm: không cần hỏi AI, chuyển người khẩn cấp ngay.
        var danger = DangerDetector.Detect(template, request.CustomerMessage);
        if (danger is not null)
        {
            trace.DangerSignal = danger;
            trace.Guards.Add("danger_keyword");
            return Finish(request, texts, trace, vault, usage, new Decision(texts.Urgent, true, "urgent", Urgency.Urgent, "high", null, []));
        }

        // Tin không phải chữ (ảnh, video, file...): bot chưa xem được → trả lời cố định, không gọi AI (không bịa nội dung ảnh).
        if (InboundText.IsMedia(request.CustomerMessage))
        {
            trace.Guards.Add("media_handoff");
            return Finish(request, texts, trace, vault, usage, new Decision(texts.Media, true, "media", Urgency.None, "high", null, []));
        }

        if (request.CustomerMessage == InboundText.Sticker)
        {
            trace.Guards.Add("sticker_reply");
            return Finish(request, texts, trace, vault, usage, new Decision(texts.Sticker, false, null, Urgency.None, "high", null, []));
        }

        // 2. Tìm kiến thức (câu đã che — không gửi dữ liệu cá nhân cho dịch vụ tạo vector).
        var chunks = await SearchAsync(request.TenantId, maskedMessage, maskedHistory, trace, cancellationToken);

        // 3. Gọi AI.
        var system = PromptBuilder.BuildSystem(template, request.Profile, chunks, maskedSummary, time.GetUtcNow());
        var turns = BuildTurns(maskedHistory, maskedMessage);
        BotAnswer? answer;
        try
        {
            answer = await AskAsync(system, turns, usage, trace, cancellationToken);
            if (answer is null)
            {
                trace.Guards.Add("invalid_output");
                return Finish(request, texts, trace, vault, usage, Handoff(texts, "ai_error"));
            }

            var violations = Violations(template, answer, chunks);
            if (violations.Count > 0)
            {
                trace.Guards.Add("forbidden_retry");
                trace.Forbidden = violations;
                var retryTurns = turns
                    .Append(new ChatTurn(ChatRole.Assistant, answer.Reply))
                    .Append(new ChatTurn(ChatRole.User,
                        $"[Hệ thống, không phải khách] Câu trả lời vừa rồi có cụm từ không được phép: {string.Join(", ", violations.Select(v => $"\"{v}\""))}. " +
                        "Viết lại câu trả lời cho tin nhắn trước của khách, giữ nguyên thông tin đúng, không dùng các cụm đó, không hứa hẹn kết quả."))
                    .ToList();
                answer = await AskAsync(system, retryTurns, usage, trace, cancellationToken);
                if (answer is null || Violations(template, answer, chunks).Count > 0)
                {
                    trace.Guards.Add("forbidden_blocked");
                    return Finish(request, texts, trace, vault, usage, Handoff(texts, "forbidden_phrase"));
                }
            }
        }
        catch (AiProviderException ex)
        {
            LogAiFailed(logger, ex.GetType().Name);
            trace.Guards.Add("ai_unavailable");
            return Finish(request, texts, trace, vault, usage, Handoff(texts, "ai_error"));
        }

        return Finish(request, texts, trace, vault, usage, Decide(template, texts, answer, chunks, trace));
    }

    public async Task<BotSummaryResult> SummarizeAsync(
        Guid tenantId,
        string? previousSummary,
        IReadOnlyList<BotHistoryTurn> turns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var vault = new PiiVault();
        var text = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            text.AppendLine("Tóm tắt trước đó:").AppendLine(PiiMasker.Mask(previousSummary, vault)).AppendLine();
        }

        text.AppendLine("Các tin nhắn tiếp theo:");
        foreach (var turn in turns)
        {
            var who = turn.Sender switch
            {
                MessageSender.Customer => "Khách",
                MessageSender.Staff => "Nhân viên",
                _ => "Trợ lý",
            };
            text.AppendLine(CultureInfo.InvariantCulture, $"{who}: {PiiMasker.Mask(turn.Text, vault)}");
        }

        var result = await chat.ChatAsync(
            new ChatRequest(PromptBuilder.SummarySystem, [new ChatTurn(ChatRole.User, text.ToString())], null, 0.2, 1024),
            cancellationToken);
        var usage = new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd);
        return new BotSummaryResult(PiiMasker.Restore(result.Text.Trim(), vault), [usage]);
    }

    private async Task<IReadOnlyList<PromptChunk>> SearchAsync(
        Guid tenantId,
        string maskedMessage,
        IReadOnlyList<BotHistoryTurn> maskedHistory,
        TurnTrace trace,
        CancellationToken cancellationToken)
    {
        // Tin quá ngắn ("còn gói 90 phút thì sao?") → ghép tin khách trước để tìm đúng ngữ cảnh.
        var query = maskedMessage;
        if (query.Length < 30)
        {
            var previous = maskedHistory.LastOrDefault(t => t.Sender == MessageSender.Customer);
            if (previous is not null)
            {
                query = $"{previous.Text}\n{query}";
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            var hits = await search.SearchAsync(tenantId, query, _options.BotSearchLimit, cancellationToken);
            trace.BelowThreshold = hits.Count(h => h.Score < _options.BotMinScore);
            var chunks = hits
                .Where(h => h.Score >= _options.BotMinScore)
                .Select((h, i) => new PromptChunk($"C{i + 1}", h))
                .ToList();
            trace.Chunks = chunks;
            return chunks;
        }
        catch (AiProviderException ex)
        {
            // Tìm kiếm lỗi: vẫn hỏi AI (không có dữ liệu → AI tự chuyển người), không bỏ rơi khách.
            LogSearchFailed(logger, ex.GetType().Name);
            trace.Guards.Add("search_unavailable");
            return [];
        }
    }

    /// <summary>Gọi AI, JSON sai định dạng → thử lại 1 lần (nhiệt độ 0). Vẫn sai → null.</summary>
    private async Task<BotAnswer?> AskAsync(
        string system,
        IReadOnlyList<ChatTurn> turns,
        List<BotUsage> usage,
        TurnTrace trace,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await chat.ChatAsync(
                new ChatRequest(system, turns, PromptBuilder.ReplySchema, attempt == 0 ? 0.3 : 0.0, MaxOutputTokens),
                cancellationToken);
            usage.Add(new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd));
            trace.Calls++;
            trace.Model = result.Model;
            trace.LatencyMs += result.LatencyMs;

            var answer = BotAnswer.TryParse(result.Text);
            if (answer is not null)
            {
                return answer;
            }

            trace.Guards.Add("parse_retry");
        }

        return null;
    }

    private static List<string> Violations(IndustryTemplate template, BotAnswer answer, IReadOnlyList<PromptChunk> chunks)
    {
        var used = chunks.Where(c => answer.UsedChunkIds.Contains(c.Id)).Select(c => c.Hit.Content);
        return ForbiddenFilter.FindViolations(template, answer.Reply, used).ToList();
    }

    private static Decision Decide(IndustryTemplate template, BotTexts texts, BotAnswer answer, IReadOnlyList<PromptChunk> chunks, TurnTrace trace)
    {
        var validIds = chunks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var used = answer.UsedChunkIds.Where(validIds.Contains).Distinct(StringComparer.Ordinal).ToList();
        trace.UsedChunkIds = used;
        trace.HealthTopic = answer.HealthTopic;

        var reply = answer.Reply.Trim();
        var needsHuman = answer.NeedsHuman;
        var reason = answer.HandoffReason;
        var urgency = answer.Urgency switch
        {
            "urgent" => Urgency.Urgent,
            "normal" => Urgency.Normal,
            _ => Urgency.None,
        };

        if (urgency == Urgency.Urgent)
        {
            trace.Guards.Add("ai_urgent");
            return new Decision(texts.Urgent, true, "urgent", Urgency.Urgent, answer.Confidence, answer.Sentiment, answer.LeadFields);
        }

        // "0 bịa giá": có con số tiền nhưng không dẫn đoạn dữ liệu nào → không gửi, chuyển người.
        if (used.Count == 0 && PriceDetector.MentionsPrice(reply))
        {
            trace.Guards.Add("price_without_source");
            return Handoff(texts, "no_knowledge") with { Sentiment = answer.Sentiment, LeadFields = answer.LeadFields };
        }

        if (string.IsNullOrWhiteSpace(reply))
        {
            trace.Guards.Add("empty_reply");
            return Handoff(texts, "ai_error");
        }

        if (answer.Confidence == "low" && !needsHuman)
        {
            trace.Guards.Add("low_confidence_handoff");
            needsHuman = true;
            reason ??= "low_confidence";
        }

        if (template.MedicalSafety && answer.HealthTopic && !VietText.Fold(reply).Contains(" tham kham truc tiep ", StringComparison.Ordinal))
        {
            trace.Guards.Add("medical_disclaimer_added");
            reply = $"{reply}\n{texts.MedicalDisclaimer}";
        }

        if (needsHuman && string.IsNullOrWhiteSpace(reason))
        {
            reason = "other";
        }

        return new Decision(reply, needsHuman, needsHuman ? reason : null, urgency, answer.Confidence, answer.Sentiment, answer.LeadFields);
    }

    private static Decision Handoff(BotTexts texts, string reason) =>
        new(texts.Handoff, true, reason, Urgency.None, "low", null, []);

    private static BotTurnResult Finish(
        BotTurnRequest request,
        BotTexts texts,
        TurnTrace trace,
        PiiVault vault,
        List<BotUsage> usage,
        Decision decision)
    {
        var reply = PiiMasker.Restore(decision.Reply, vault).Trim();
        if (request.IsFirstBotReply)
        {
            reply = $"{texts.Disclosure}\n\n{reply}";
        }

        var allowedKeys = IndustryTemplateRegistry.Get(request.Profile.IndustrySlug).LeadFields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var leads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in decision.LeadFields)
        {
            var restored = PiiMasker.Restore(value, vault).Trim();
            if (allowedKeys.Contains(key) && restored.Length is > 0 and <= 200)
            {
                leads[key] = restored;
            }
        }

        trace.PiiMasked = vault.Count;
        trace.Confidence = decision.Confidence;
        trace.NeedsHuman = decision.NeedsHuman;
        trace.HandoffReason = decision.HandoffReason;
        trace.Urgency = decision.Urgency;
        trace.Sentiment = decision.Sentiment;
        trace.LeadKeys = leads.Keys.ToList();
        trace.FirstReply = request.IsFirstBotReply;

        return new BotTurnResult(
            reply,
            decision.NeedsHuman,
            decision.HandoffReason,
            decision.Urgency,
            decision.Confidence,
            decision.Sentiment,
            leads,
            trace.ToJson(),
            usage);
    }

    /// <summary>Lịch sử (đã che) + tin mới → lượt chat cho AI: bắt đầu bằng khách, gộp các tin liên tiếp cùng phía.</summary>
    private static List<ChatTurn> BuildTurns(IReadOnlyList<BotHistoryTurn> history, string message)
    {
        var turns = new List<ChatTurn>();
        foreach (var (role, text) in history
            .Select(t => t.Sender == MessageSender.Customer
                ? (ChatRole.User, t.Text)
                : (ChatRole.Assistant, t.Sender == MessageSender.Staff ? $"(Nhân viên trả lời) {t.Text}" : t.Text))
            .Append((ChatRole.User, message)))
        {
            if (turns.Count == 0 && role == ChatRole.Assistant)
            {
                continue;
            }

            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1] = turns[^1] with { Text = $"{turns[^1].Text}\n{text}" };
            }
            else
            {
                turns.Add(new ChatTurn(role, text));
            }
        }

        return turns;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gọi AI lỗi ({ErrorType}), trả câu dự phòng và chuyển người")]
    private static partial void LogAiFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tìm kiến thức lỗi ({ErrorType}), trả lời không có dữ liệu tham khảo")]
    private static partial void LogSearchFailed(ILogger logger, string errorType);

    private sealed record Decision(
        string Reply,
        bool NeedsHuman,
        string? HandoffReason,
        Urgency Urgency,
        string Confidence,
        string? Sentiment,
        IReadOnlyList<KeyValuePair<string, string>> LeadFields);

    /// <summary>Thông tin chẩn đoán cho màn "Vì sao bot trả lời vậy?" — KHÔNG chứa nội dung tin hay dữ liệu cá nhân của khách.</summary>
    private sealed class TurnTrace(IndustryTemplate template)
    {
        public List<string> Guards { get; } = [];

        public string? DangerSignal { get; set; }

        public IReadOnlyList<string> Forbidden { get; set; } = [];

        public IReadOnlyList<PromptChunk> Chunks { get; set; } = [];

        public IReadOnlyList<string> UsedChunkIds { get; set; } = [];

        public int BelowThreshold { get; set; }

        public int Calls { get; set; }

        public string? Model { get; set; }

        public long LatencyMs { get; set; }

        public bool HealthTopic { get; set; }

        public int PiiMasked { get; set; }

        public string Confidence { get; set; } = "low";

        public bool NeedsHuman { get; set; }

        public string? HandoffReason { get; set; }

        public Urgency Urgency { get; set; }

        public string? Sentiment { get; set; }

        public IReadOnlyList<string> LeadKeys { get; set; } = [];

        public bool FirstReply { get; set; }

        public string ToJson() => new JsonObject
        {
            ["template"] = $"{template.Slug}@{template.Version}",
            ["model"] = Model,
            ["calls"] = Calls,
            ["latencyMs"] = LatencyMs,
            ["dangerSignal"] = DangerSignal,
            ["guards"] = new JsonArray(Guards.Select(g => (JsonNode?)g).ToArray()),
            ["forbidden"] = new JsonArray(Forbidden.Select(f => (JsonNode?)f).ToArray()),
            ["chunks"] = new JsonArray(Chunks.Select(c => (JsonNode?)new JsonObject
            {
                ["id"] = c.Id,
                ["chunkId"] = c.Hit.ChunkId,
                ["source"] = c.Hit.Source,
                ["sourceId"] = c.Hit.SourceId,
                ["code"] = c.Hit.Code,
                ["title"] = c.Hit.Title,
                ["excerpt"] = c.Hit.Content.Length > 200 ? c.Hit.Content[..200] + "…" : c.Hit.Content,
                ["score"] = c.Hit.Score,
                ["medicallyReviewed"] = c.Hit.MedicallyReviewed,
                ["used"] = UsedChunkIds.Contains(c.Id),
            }).ToArray()),
            ["belowThreshold"] = BelowThreshold,
            ["confidence"] = Confidence,
            ["needsHuman"] = NeedsHuman,
            ["handoffReason"] = HandoffReason,
            ["urgency"] = Urgency.ToString().ToLowerInvariant(),
            ["sentiment"] = Sentiment,
            ["healthTopic"] = HealthTopic,
            ["leadKeys"] = new JsonArray(LeadKeys.Select(k => (JsonNode?)k).ToArray()),
            ["piiMasked"] = PiiMasked,
            ["firstReply"] = FirstReply,
        }.ToJsonString();
    }
}

/// <summary>Câu cố định của bot, đã thay {bot} {khach} {ten_doanh_nghiep}.</summary>
internal sealed class BotTexts(IndustryTemplate template, BotProfile profile)
{
    public string Urgent => Fill(template.UrgentReply);

    public string Handoff => Fill("Dạ {bot} xin phép chuyển {khach} sang nhân viên của {ten_doanh_nghiep} để hỗ trợ chính xác hơn ạ. Nhân viên sẽ phản hồi {khach} sớm nhất có thể.");

    public string Media => Fill("Dạ {bot} đã nhận được tin của {khach}. Hiện {bot} chưa xem được hình ảnh, âm thanh hay tệp đính kèm, {khach} mô tả giúp {bot} bằng chữ nhé. {bot} cũng đã báo nhân viên của {ten_doanh_nghiep} hỗ trợ {khach} ạ.");

    public string Sticker => Fill("Dạ, {bot} có thể hỗ trợ gì thêm cho {khach} ạ?");

    public string MedicalDisclaimer => Fill("Để xác định chính xác tình trạng, {khach} cần bác sĩ/chuyên viên thăm khám trực tiếp ạ.");

    /// <summary>Câu báo trợ lý AI + link chính sách (CLAUDE.md mục 7) — không tắt được.</summary>
    public string Disclosure
    {
        get
        {
            var text = Fill($"Dạ {{bot}} là {profile.BotName}, trợ lý AI (trí tuệ nhân tạo) của {{ten_doanh_nghiep}}, hỗ trợ {{khach}} 24/7.");
            return string.IsNullOrWhiteSpace(profile.PrivacyUrl)
                ? text
                : $"{text} {Fill("Thông tin {khach} chia sẻ được bảo vệ theo chính sách bảo mật:")} {profile.PrivacyUrl}";
        }
    }

    private string Fill(string text)
    {
        var filled = text
            .Replace("{bot}", profile.BotPronoun, StringComparison.Ordinal)
            .Replace("{khach}", template.CustomerAddress, StringComparison.Ordinal)
            .Replace("{ten_doanh_nghiep}", profile.BusinessName, StringComparison.Ordinal);
        return CapitalizeSentences(filled);
    }

    private static string CapitalizeSentences(string text)
    {
        var chars = text.ToCharArray();
        var start = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (start && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                start = false;
            }
            else if (chars[i] is '.' or '!' or '?')
            {
                start = true;
            }
            else if (!char.IsWhiteSpace(chars[i]))
            {
                start = false;
            }
        }

        return new string(chars);
    }
}

/// <summary>JSON AI trả về (schema trong <see cref="PromptBuilder.ReplySchema"/>). Thiếu "reply" → coi như sai định dạng.</summary>
internal sealed record BotAnswer(
    string Reply,
    IReadOnlyList<string> UsedChunkIds,
    string Confidence,
    bool NeedsHuman,
    string? HandoffReason,
    string Urgency,
    bool HealthTopic,
    string? Sentiment,
    IReadOnlyList<KeyValuePair<string, string>> LeadFields)
{
    private static readonly string[] Confidences = ["high", "medium", "low"];
    private static readonly string[] Sentiments = ["positive", "neutral", "negative"];

    public static BotAnswer? TryParse(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            json = json.Trim('`').Trim();
            if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                json = json[4..];
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("reply", out var reply)
                || reply.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            var ids = root.TryGetProperty("used_chunk_ids", out var idArray) && idArray.ValueKind == JsonValueKind.Array
                ? idArray.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
                : [];
            var leads = new List<KeyValuePair<string, string>>();
            if (root.TryGetProperty("lead_fields", out var leadArray) && leadArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var lead in leadArray.EnumerateArray())
                {
                    if (lead.ValueKind == JsonValueKind.Object
                        && lead.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                        && lead.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        leads.Add(new(k.GetString()!, v.GetString()!));
                    }
                }
            }

            var confidence = Str("confidence");
            var sentiment = Str("sentiment");
            var reason = Str("handoff_reason");
            return new BotAnswer(
                reply.GetString()!,
                ids,
                Confidences.Contains(confidence) ? confidence! : "low",
                Bool("needs_human"),
                string.IsNullOrWhiteSpace(reason) || reason == "null" ? null : reason,
                Str("urgency") ?? "none",
                Bool("health_topic"),
                Sentiments.Contains(sentiment) ? sentiment : null,
                leads);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
