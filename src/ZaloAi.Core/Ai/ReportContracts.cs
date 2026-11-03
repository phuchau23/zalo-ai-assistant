namespace ZaloAi.Core.Ai;

/// <summary>
/// Câu hỏi số liệu bằng tiếng Việt (CRM C6): AI chỉ CHỌN báo cáo có sẵn + tham số, KHÔNG sinh SQL, KHÔNG tự bịa số.
/// </summary>
/// <param name="Report">revenue | customers | staff | occupancy | outstanding | today | ai</param>
/// <param name="From">yyyy-MM-dd (giờ DN); null = mặc định 30 ngày.</param>
/// <param name="To">yyyy-MM-dd (bao gồm); null = hôm nay.</param>
/// <param name="GroupBy">day | week | month (revenue)</param>
/// <param name="Focus">Khía cạnh khách hỏi: tổng | theo dịch vụ | theo nhân viên | theo chi nhánh | theo nguồn | so với kỳ trước (ghi tự do, dùng để soạn câu trả lời).</param>
/// <param name="Unanswerable">Câu hỏi không thuộc báo cáo nào → true; <see cref="Explanation"/> nói vì sao.</param>
public sealed record ReportQuery(string Report, string? From, string? To, string? GroupBy, string? Focus, bool Unanswerable, string? Explanation);

public interface IReportQuestionInterpreter
{
    Task<(ReportQuery Query, IReadOnlyList<BotUsage> Usage)> InterpretAsync(string question, DateTimeOffset nowLocal, CancellationToken cancellationToken);

    /// <summary>
    /// Soạn câu trả lời ngắn từ số liệu (JSON) — chỉ diễn giải, không thêm số ngoài dữ liệu. <paramref name="customerNames"/>: tên khách có
    /// trong dữ liệu, được che trước khi gửi AI và ghép lại trong câu trả lời.
    /// </summary>
    Task<(string Answer, IReadOnlyList<BotUsage> Usage)> NarrateAsync(string question, string dataJson, IReadOnlyList<string> customerNames, CancellationToken cancellationToken);
}
