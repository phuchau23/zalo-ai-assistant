using System.Globalization;
using System.Text;
using ZaloAi.Ai.Privacy;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;

namespace ZaloAi.Ai.Care;

/// <summary>Viết tóm tắt khách cho kỹ thuật viên (CRM C2) bằng chat model; PII che bằng <see cref="PiiMasker"/> rồi ghép lại.</summary>
public sealed class AppointmentBriefWriter(IChatProvider chat) : IAppointmentBriefWriter
{
    private const string System = """
        Bạn viết TÓM TẮT NGẮN cho kỹ thuật viên của doanh nghiệp đọc trước buổi làm cho khách. Tiếng Việt, tối đa 110 từ, gạch đầu dòng:
        - Khách đang gặp gì / mong muốn gì (chỉ từ dữ liệu đưa vào).
        - Lưu ý khi làm (bệnh nền, vùng nhạy cảm, điều khách phàn nàn, sở thích lực/nhiệt...).
        - Buổi trước đã làm gì và phản hồi (nếu có).
        - Việc nên làm trong buổi này (gợi ý ngắn, không hứa kết quả).
        Giữ NGUYÊN các chuỗi dạng [PHONE_1], [NAME_1]. Không bịa; thiếu thông tin thì ghi "chưa có thông tin".
        """;

    public async Task<AppointmentBriefResult> WriteAsync(Guid tenantId, AppointmentBriefRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, request.CustomerName);
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"Doanh nghiệp: {request.BusinessName} (ngành {request.IndustrySlug}).");
        s.AppendLine(CultureInfo.InvariantCulture, $"Buổi sắp tới: {string.Join(" + ", request.Services)}.");
        if (request.PreviousVisits.Count > 0)
        {
            s.AppendLine("Các buổi trước: " + string.Join("; ", request.PreviousVisits));
        }

        if (request.StaffNotes.Count > 0)
        {
            s.AppendLine("Ghi chú của nhân viên:");
            foreach (var note in request.StaffNotes)
            {
                s.AppendLine(CultureInfo.InvariantCulture, $"- {PiiMasker.Mask(note, vault)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ConversationSummary))
        {
            s.AppendLine("Tóm tắt hội thoại: " + PiiMasker.Mask(request.ConversationSummary, vault));
        }

        if (request.RecentMessages.Count > 0)
        {
            s.AppendLine("Tin gần nhất:");
            foreach (var turn in request.RecentMessages)
            {
                var who = turn.Sender == MessageSender.Customer ? "Khách" : "Bên mình";
                s.AppendLine(CultureInfo.InvariantCulture, $"{who}: {PiiMasker.Mask(turn.Text, vault)}");
            }
        }

        var result = await chat.ChatAsync(new ChatRequest(System, [new ChatTurn(ChatRole.User, s.ToString())], null, 0.2, 700), cancellationToken);
        var usage = new BotUsage(UsageKind.Chat, result.Provider, result.Model, result.InputTokens, result.OutputTokens, result.CostUsd);
        return new AppointmentBriefResult(PiiMasker.Restore(result.Text.Trim(), vault), [usage]);
    }
}
