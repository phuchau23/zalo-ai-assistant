using System.Globalization;
using System.Text.Json;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;

namespace ZaloAi.Ai.Reports;

/// <summary>AI chọn báo cáo + tham số từ câu hỏi tiếng Việt (CRM C6); sau đó diễn giải số liệu hệ thống tính. Không có PII trong prompt.</summary>
public sealed class ReportQuestionInterpreter(IChatProvider chat) : IReportQuestionInterpreter
{
    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "report": { "type": "string", "enum": ["revenue", "customers", "staff", "occupancy", "outstanding", "today", "ai"] },
            "from": { "type": ["string", "null"], "description": "yyyy-MM-dd; null = mặc định" },
            "to": { "type": ["string", "null"], "description": "yyyy-MM-dd (bao gồm); null = hôm nay" },
            "group_by": { "type": ["string", "null"], "enum": ["day", "week", "month", null] },
            "focus": { "type": ["string", "null"], "description": "khía cạnh khách hỏi, ví dụ: dịch vụ bán chạy, nhân viên nhiều khách quay lại, so với tháng trước" },
            "unanswerable": { "type": "boolean" },
            "explanation": { "type": ["string", "null"] }
          },
          "required": ["report", "from", "to", "group_by", "focus", "unanswerable", "explanation"]
        }
        """;

    public async Task<(ReportQuery Query, IReadOnlyList<BotUsage> Usage)> InterpretAsync(string question, DateTimeOffset nowLocal, CancellationToken cancellationToken)
    {
        var system = $"""
            Bạn là trợ lý phân tích số liệu của một doanh nghiệp dịch vụ (spa, phòng khám...). Người dùng (chủ doanh nghiệp) hỏi bằng tiếng Việt.
            Hôm nay: {nowLocal.ToString("dddd, dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"))}.
            Nhiệm vụ: CHỌN đúng một báo cáo có sẵn và khoảng thời gian. Báo cáo:
            - revenue: doanh thu (tổng, theo ngày/tuần/tháng, theo dịch vụ, nhân viên, chi nhánh, nguồn khách, hình thức thanh toán, so kỳ trước)
            - customers: khách mới / quay lại, giữ chân, chi tiêu trung bình, khách chi nhiều nhất
            - staff: hiệu suất nhân viên (lịch hoàn thành, doanh thu, hoa hồng, khách quay lại, giờ làm)
            - occupancy: lấp đầy lịch theo giờ / thứ
            - outstanding: công nợ, gói chưa dùng, số dư thẻ trả trước
            - today: tình hình hôm nay
            - ai: hiệu quả trợ lý AI (lịch bot chốt, doanh thu bot mang về, tin bot xử lý)
            Quy đổi "tháng này", "tuần trước", "quý 3"... thành from/to theo ngày hôm nay. Không thuộc báo cáo nào → unanswerable = true và giải thích ngắn.
            Chỉ trả JSON theo schema.
            """;
        var result = await chat.ChatAsync(new ChatRequest(system, [new ChatTurn(ChatRole.User, question)], Schema, 0.1, 400), cancellationToken);
        var usage = new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd);
        return (Parse(result.Text), [usage]);
    }

    public async Task<(string Answer, IReadOnlyList<BotUsage> Usage)> NarrateAsync(string question, string dataJson, CancellationToken cancellationToken)
    {
        const string System = """
            Bạn diễn giải số liệu cho chủ doanh nghiệp. Trả lời tiếng Việt, 2–5 câu, nêu con số CHÍNH XÁC như trong dữ liệu (tiền ghi dạng 1.250.000 đ),
            nhận xét ngắn gọn (tăng/giảm, nổi bật). TUYỆT ĐỐI không thêm số liệu không có trong dữ liệu; thiếu thì nói "chưa có dữ liệu". Không dùng markdown.
            """;
        var result = await chat.ChatAsync(new ChatRequest(System, [new ChatTurn(ChatRole.User, $"Câu hỏi: {question}\nDữ liệu (JSON):\n{dataJson}")], null, 0.2, 600), cancellationToken);
        var usage = new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd);
        return (result.Text.Trim(), [usage]);
    }

    public static ReportQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var doc = JsonDocument.Parse(Providers.JsonOutput.Clean(text));
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var report = Str("report") ?? "revenue";
            return new ReportQuery(
                report is "revenue" or "customers" or "staff" or "occupancy" or "outstanding" or "today" or "ai" ? report : "revenue",
                Str("from"),
                Str("to"),
                Str("group_by"),
                Str("focus"),
                root.TryGetProperty("unanswerable", out var u) && u.ValueKind == JsonValueKind.True,
                Str("explanation"));
        }
        catch (JsonException)
        {
            return new ReportQuery("revenue", null, null, null, null, true, "Không hiểu câu hỏi.");
        }
    }
}
