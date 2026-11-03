using System.Text.Json;
using System.Text.RegularExpressions;
using ZaloAi.Core.Ai;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// AI giả, tính tại chỗ, không gọi mạng — cho test và chạy dev không key (Ai:ChatProvider = fake). KHÔNG dùng ở production.
/// Có JSON schema: nếu prompt có đoạn kiến thức "[C1] ..." thì trả lời bằng nội dung đoạn đó (confidence high),
/// không có thì confidence low + cần người. Có đoạn kiến thức → 2 tin (giống cách nhắn nhiều tin ngắn); khách mở đầu bằng
/// "chị/anh/cô/chú/bác ..." → customer_salutation. Không schema (tóm tắt): trả câu tóm tắt cố định kèm tin cuối.
/// </summary>
public sealed partial class FakeChatProvider : IChatProvider
{
    public const string ModelName = "fake-chat";

    private static readonly string[] NoKnowledgeMessages = ["Dạ câu này em chưa có thông tin, em chuyển nhân viên hỗ trợ mình nhé."];
    private static readonly string[] BookingLeadIn = ["Dạ để em kiểm tra lịch cho mình nha."];

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var last = request.Turns.Count > 0 ? request.Turns[^1].Text : "";
        string text;
        if (request.JsonSchema is null)
        {
            text = $"Tóm tắt (giả lập): {Truncate(last, 120)}";
        }
        else if (request.JsonSchema.Contains("\"unanswerable\"", StringComparison.Ordinal))
        {
            // Hỏi số liệu (C6): "doanh thu" → revenue tháng này; "nhân viên" → staff; "khách" → customers; "thời tiết" → không trả lời được.
            var lower = last.ToLowerInvariant();
            var report = lower.Contains("nhân viên", StringComparison.Ordinal) ? "staff"
                : lower.Contains("khách", StringComparison.Ordinal) ? "customers"
                : lower.Contains("nợ", StringComparison.Ordinal) ? "outstanding"
                : lower.Contains("hôm nay", StringComparison.Ordinal) ? "today"
                : lower.Contains("bot", StringComparison.Ordinal) || lower.Contains("ai", StringComparison.Ordinal) ? "ai"
                : "revenue";
            var unanswerable = lower.Contains("thời tiết", StringComparison.Ordinal);
            text = JsonSerializer.Serialize(new { report, from = (string?)null, to = (string?)null, group_by = "day", focus = (string?)null, unanswerable, explanation = unanswerable ? "Không phải số liệu kinh doanh." : null });
        }
        else if (request.JsonSchema.Contains("ask_human", StringComparison.Ordinal))
        {
            // Phân tích chăm sóc: khách nói "không cần" → none; có "phàn nàn"/"đau" → cần nhân viên; tới giờ hẹn → follow_up; còn lại → nhắn.
            var followUp = request.System.Contains("hẹn chăm sóc lại khách", StringComparison.Ordinal);
            var declined = last.Contains("không cần", StringComparison.OrdinalIgnoreCase);
            var sensitive = last.Contains("phàn nàn", StringComparison.OrdinalIgnoreCase) || last.Contains("đau", StringComparison.OrdinalIgnoreCase);
            text = JsonSerializer.Serialize(new
            {
                action = declined ? "none" : sensitive ? "ask_human" : "send",
                human_reason = sensitive && !declined ? "sensitive" : null,
                promotional = false,
                temperature = declined ? "cold" : "hot",
                trigger = followUp ? "follow_up" : "thinking",
                reason = declined ? "Khách đã từ chối." : "Khách hỏi dịch vụ nhưng chưa đặt lịch (giả lập).",
                suggested_action = "Hỏi lại nhu cầu và mời đặt lịch.",
                draft_message = declined ? "" : "Dạ em chào mình, em nhắn hỏi thăm mình còn quan tâm dịch vụ bên em không ạ?",
            });
        }
        else
        {
            var chunk = ChunkHeader().Match(request.System);
            var self = SelfReference().Match(last);
            var salutation = self.Success ? self.Groups["s"].Value.ToLowerInvariant() : "none";
            var booking = FakeBooking(request.System, last);
            text = booking is not null
                ? JsonSerializer.Serialize(new
                {
                    messages = BookingLeadIn,
                    customer_salutation = salutation,
                    used_chunk_ids = chunk.Success ? new[] { chunk.Groups["id"].Value } : Array.Empty<string>(),
                    confidence = "high",
                    needs_human = false,
                    handoff_reason = (string?)null,
                    urgency = "none",
                    sentiment = "neutral",
                    lead_fields = Array.Empty<object>(),
                    booking,
                })
                : chunk.Success
                ? JsonSerializer.Serialize(new
                {
                    messages = new[]
                    {
                        $"Dạ, theo thông tin bên em: {Truncate(chunk.Groups["body"].Value.Trim().ReplaceLineEndings(" "), 300)}",
                        "Mình cần em tư vấn thêm gì không ạ?",
                    },
                    customer_salutation = salutation,
                    used_chunk_ids = new[] { chunk.Groups["id"].Value },
                    confidence = "high",
                    needs_human = false,
                    handoff_reason = (string?)null,
                    urgency = "none",
                    sentiment = "neutral",
                    lead_fields = Array.Empty<object>(),
                })
                : JsonSerializer.Serialize(new
                {
                    messages = NoKnowledgeMessages,
                    customer_salutation = salutation,
                    used_chunk_ids = Array.Empty<string>(),
                    confidence = "low",
                    needs_human = true,
                    handoff_reason = "no_knowledge",
                    urgency = "none",
                    sentiment = "neutral",
                    lead_fields = Array.Empty<object>(),
                });
        }

        var input = request.System.Length / 4 + request.Turns.Sum(t => t.Text.Length) / 4;
        return Task.FromResult(new ChatResult(text, "fake", ModelName, input, text.Length / 4, 0m, 0));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Ý định đặt lịch giả lập (CRM C2), chỉ khi prompt có mục ĐẶT LỊCH QUA CHAT: "đặt lịch ... 15h mai" → book (mã dịch vụ = mã của
    /// đoạn đầu tiên trong prompt, ngày mai giờ VN); "hủy lịch" → cancel lịch đầu trong LỊCH SẮP TỚI; "dời ... 16h" → reschedule.
    /// </summary>
    private static object? FakeBooking(string system, string last)
    {
        if (!system.Contains("## ĐẶT LỊCH QUA CHAT", StringComparison.Ordinal))
        {
            return null;
        }

        var lower = last.ToLowerInvariant();
        var hour = HourMention().Match(lower);
        var upcoming = UpcomingId().Match(system);
        var code = ChunkCode().Match(system);
        var tomorrow = DateTime.UtcNow.AddHours(7).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var time = hour.Success ? $"{int.Parse(hour.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture):00}:00" : null;

        if (lower.Contains("hủy lịch", StringComparison.Ordinal) && upcoming.Success)
        {
            return Intent("cancel", [], null, null, upcoming.Groups["id"].Value);
        }

        if (lower.Contains("dời", StringComparison.Ordinal) && upcoming.Success && time is not null)
        {
            return Intent("reschedule", [], tomorrow, time, upcoming.Groups["id"].Value);
        }

        if (lower.Contains("giờ nào trống", StringComparison.Ordinal) && code.Success)
        {
            return Intent("check_slots", [code.Groups["code"].Value], tomorrow, null, null);
        }

        if ((lower.Contains("đặt lịch", StringComparison.Ordinal) || lower.Contains("đặt cho", StringComparison.Ordinal)) && code.Success && time is not null)
        {
            var party = lower.Contains("3 người", StringComparison.Ordinal) ? 3 : 1;
            return Intent("book", [code.Groups["code"].Value], tomorrow, time, null, party);
        }

        return null;

        static object Intent(string action, string[] codes, string? date, string? time, string? appointmentId, int party = 1) => new
        {
            action,
            service_codes = codes,
            date,
            time,
            staff_name = (string?)null,
            branch_name = (string?)null,
            party_size = party,
            appointment_id = appointmentId,
        };
    }

    [GeneratedRegex(@"(?<h>\d{1,2})\s*(?:h|giờ)", RegexOptions.IgnoreCase)]
    private static partial Regex HourMention();

    [GeneratedRegex(@"id=(?<id>[0-9a-fA-F-]{36})")]
    private static partial Regex UpcomingId();

    [GeneratedRegex(@"\(mã (?<code>[A-Z0-9-]+)")]
    private static partial Regex ChunkCode();

    /// <summary>Dòng tiêu đề "[C1] ..." rồi nội dung đến dòng trống (định dạng của PromptBuilder).</summary>
    [GeneratedRegex(@"^\[(?<id>C\d+)\][^\n]*\r?\n(?<body>(?:[^\r\n]+\r?\n?)+)", RegexOptions.Multiline)]
    private static partial Regex ChunkHeader();

    /// <summary>Tin của khách mở đầu bằng cách tự xưng ("chị muốn hỏi...").</summary>
    [GeneratedRegex(@"(?:^|\n)(?<s>anh|chị|cô|chú|bác)\s", RegexOptions.IgnoreCase)]
    private static partial Regex SelfReference();
}
