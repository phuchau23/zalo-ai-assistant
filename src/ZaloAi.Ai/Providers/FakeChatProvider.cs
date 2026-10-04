using System.Text.Json;
using System.Text.RegularExpressions;
using ZaloAi.Core.Ai;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// AI giả, tính tại chỗ, không gọi mạng — cho test và chạy dev không key (Ai:ChatProvider = fake). KHÔNG dùng ở production.
/// Có JSON schema: nếu prompt có đoạn kiến thức "[C1] ..." thì trả lời bằng nội dung đoạn đó (confidence high),
/// không có thì confidence low + cần người. Không schema (tóm tắt): trả câu tóm tắt cố định kèm tin cuối.
/// </summary>
public sealed partial class FakeChatProvider : IChatProvider
{
    public const string ModelName = "fake-chat";

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var last = request.Turns.Count > 0 ? request.Turns[^1].Text : "";
        string text;
        if (request.JsonSchema is null)
        {
            text = $"Tóm tắt (giả lập): {Truncate(last, 120)}";
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
                draft_message = declined ? "" : "Dạ em chào anh/chị, em nhắn hỏi thăm mình còn quan tâm dịch vụ bên em không ạ?",
            });
        }
        else
        {
            var chunk = ChunkHeader().Match(request.System);
            text = chunk.Success
                ? JsonSerializer.Serialize(new
                {
                    reply = $"Dạ, theo thông tin bên em: {Truncate(chunk.Groups["body"].Value.Trim().ReplaceLineEndings(" "), 300)}",
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
                    reply = "Dạ câu này em chưa có thông tin, em chuyển nhân viên hỗ trợ mình nhé.",
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

    /// <summary>Dòng tiêu đề "[C1] ..." rồi nội dung đến dòng trống (định dạng của PromptBuilder).</summary>
    [GeneratedRegex(@"^\[(?<id>C\d+)\][^\n]*\r?\n(?<body>(?:[^\r\n]+\r?\n?)+)", RegexOptions.Multiline)]
    private static partial Regex ChunkHeader();
}
