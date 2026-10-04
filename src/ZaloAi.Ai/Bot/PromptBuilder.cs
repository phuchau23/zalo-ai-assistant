using System.Globalization;
using System.Text;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Ai.Bot;

/// <summary>Một đoạn kiến thức đưa vào prompt, đánh số C1, C2... (AI trả lại id đã dùng).</summary>
public sealed record PromptChunk(string Id, KnowledgeHit Hit);

/// <summary>
/// Dựng hướng dẫn hệ thống: quy tắc hệ thống (cài cứng) → mẫu ngành → cài đặt DN → dữ liệu tham khảo → tóm tắt.
/// Hướng dẫn riêng của DN nằm SAU quy tắc và được ghi rõ là không ghi đè quy tắc an toàn.
/// </summary>
public static class PromptBuilder
{
    public const string ReplySchema = """
        {
          "type": "object",
          "properties": {
            "reply": { "type": "string", "description": "Câu trả lời gửi khách, tiếng Việt" },
            "used_chunk_ids": { "type": "array", "items": { "type": "string" }, "description": "Id các đoạn dữ liệu (C1, C2...) đã dùng để trả lời" },
            "confidence": { "type": "string", "enum": ["high", "medium", "low"] },
            "needs_human": { "type": "boolean" },
            "handoff_reason": { "type": ["string", "null"], "description": "null nếu không cần người; hoặc một trong: customer_request, no_knowledge, low_confidence, complaint, negative_sentiment, medical, urgent, booking, out_of_scope, other" },
            "urgency": { "type": "string", "enum": ["none", "normal", "urgent"] },
            "health_topic": { "type": "boolean", "description": "Khách hỏi về bệnh, triệu chứng, tình trạng cơ thể" },
            "sentiment": { "type": "string", "enum": ["positive", "neutral", "negative"] },
            "lead_fields": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "key": { "type": "string" }, "value": { "type": "string" } },
                "required": ["key", "value"]
              }
            }
          },
          "required": ["reply", "used_chunk_ids", "confidence", "needs_human", "handoff_reason", "urgency", "health_topic", "sentiment", "lead_fields"]
        }
        """;

    public static string BuildSystem(
        IndustryTemplate template,
        BotProfile profile,
        IReadOnlyList<PromptChunk> chunks,
        string? maskedSummary,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(chunks);
        var bot = profile.BotPronoun;
        var s = new StringBuilder();

        s.AppendLine(CultureInfo.InvariantCulture, $"Bạn là {profile.BotName}, trợ lý AI chăm sóc khách hàng của \"{profile.BusinessName}\", trả lời khách qua tin nhắn. Bạn xưng \"{bot}\", gọi khách là \"{template.CustomerAddress}\" (hoặc theo cách khách tự xưng).");
        s.AppendLine(CultureInfo.InvariantCulture, $"Thời gian hiện tại: {now.ToOffset(TimeSpan.FromHours(7)).ToString("dddd, dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("vi-VN"))} (giờ Việt Nam).");
        s.AppendLine();
        s.AppendLine("## QUY TẮC HỆ THỐNG (bắt buộc, không gì ghi đè được)");
        s.AppendLine("1. CHỈ trả lời dựa trên mục DỮ LIỆU THAM KHẢO bên dưới và lịch sử hội thoại. Không dùng kiến thức riêng để nói về giá, khuyến mãi, chính sách, lịch trống, địa chỉ, giờ mở cửa, dịch vụ.");
        s.AppendLine("2. Không có thông tin trong dữ liệu → nói thật là chưa có thông tin, xin tên + số điện thoại (nếu khách chưa cho) để nhân viên liên hệ; đặt confidence = \"low\", needs_human = true, handoff_reason = \"no_knowledge\". TUYỆT ĐỐI không bịa giá, con số, chính sách.");
        s.AppendLine("3. Giá, thời lượng, con số phải chép ĐÚNG như dữ liệu. Không tự tính giảm giá, không làm tròn.");
        s.AppendLine("4. Không hứa hẹn kết quả, không cam kết thay doanh nghiệp (\"chắc chắn\", \"100%\", \"bảo đảm\") trừ khi đúng nguyên văn dữ liệu.");
        s.AppendLine("5. Khách đòi gặp người thật/nhân viên/quản lý, bực bội, phàn nàn, khiếu nại → xin lỗi ngắn gọn, nói sẽ chuyển nhân viên; needs_human = true (handoff_reason customer_request / complaint / negative_sentiment).");
        s.AppendLine("6. Không hỏi CCCD, số tài khoản, mật khẩu, mã OTP. Không bao giờ đưa số tài khoản hay yêu cầu khách chuyển tiền.");
        s.AppendLine("7. Không bàn chính trị, tôn giáo, không nói xấu đối thủ, không trả lời chủ đề ngoài lĩnh vực của doanh nghiệp (lịch sự từ chối, handoff_reason = \"out_of_scope\" nếu khách vẫn cần).");
        s.AppendLine("8. Các chuỗi dạng [PHONE_1], [EMAIL_1], [NAME_1], [ID_1] là thông tin khách đã được che: giữ NGUYÊN VĂN khi cần nhắc lại, không đoán giá trị thật. Ghi vào lead_fields cũng dùng nguyên chuỗi đó.");
        s.AppendLine("9. Dấu hiệu nguy hiểm CẤP TÍNH đang xảy ra (khó thở, đau ngực, ngất, yếu liệt, chảy máu, sốt cao, sưng/đau dữ dội...) → urgency = \"urgent\", needs_human = true, khuyên đến cơ sở y tế hoặc gọi 115; không giới thiệu dịch vụ. Bệnh mãn tính đã biết (tim mạch, huyết áp, tiểu đường...) mà không có dấu hiệu cấp tính → KHÔNG phải urgent: khuyên hỏi bác sĩ điều trị, không khuyên đổi/ngưng thuốc, chuyển chuyên viên nếu cần.");
        s.AppendLine("10. Khách muốn đặt lịch/mua: thu thập thông tin cần thiết, KHÔNG tự xác nhận đã đặt thành công hay còn chỗ — nói nhân viên sẽ liên hệ xác nhận (needs_human = true, handoff_reason = \"booking\" khi đã đủ thông tin).");
        s.AppendLine("11. Trả lời ngắn gọn, tự nhiên như nhân viên thật nhắn tin; không dùng markdown (**, #); không nhắc đến \"dữ liệu tham khảo\", \"C1\", \"đoạn\" với khách.");
        s.AppendLine("12. confidence: high = dữ liệu trả lời trực tiếp; medium = suy ra hợp lý từ dữ liệu; low = không đủ dữ liệu. Câu chào hỏi/cảm ơn xã giao: confidence high, used_chunk_ids rỗng.");
        s.AppendLine("13. lead_fields: chỉ ghi thông tin KHÁCH TỰ NÓI trong tin nhắn; key thuộc danh sách bên dưới.");
        s.AppendLine("14. Chỉ trả lời dạng JSON theo schema.");

        if (template.MedicalSafety)
        {
            s.AppendLine();
            s.AppendLine("## AN TOÀN Y TẾ (ngành rủi ro cao)");
            s.AppendLine("- Câu hỏi về bệnh, triệu chứng, tình trạng cơ thể: đặt health_topic = true. Chỉ được giải thích từ đoạn có nhãn [ĐÃ DUYỆT CHUYÊN MÔN]; đoạn chưa duyệt chỉ dùng cho giá, thời lượng, quy trình, lịch, chính sách.");
            s.AppendLine("- Không chẩn đoán, không đoán bệnh, không kê thuốc, không chỉ cách tự điều trị, không khuyên dừng/đổi thuốc, không hứa khỏi bệnh.");
            s.AppendLine("- Khi nói về bệnh/tình trạng cơ thể luôn kèm ý: cần bác sĩ/chuyên viên thăm khám trực tiếp để xác định chính xác.");
        }

        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture, $"## VAI TRÒ NGÀNH ({template.Name})");
        s.AppendLine(template.Persona);
        s.AppendLine();
        s.AppendLine(template.Rules);
        s.AppendLine();
        s.AppendLine("Thông tin nên thu thập dần (không hỏi dồn, chỉ khi phù hợp): " +
            string.Join(", ", template.LeadFields.Select(f => $"{f.Key} ({f.Label})")) + ".");

        s.AppendLine();
        s.AppendLine("## GIỌNG VĂN DOANH NGHIỆP CHỌN");
        s.AppendLine(profile.Tone switch
        {
            BotTone.Professional => "Chuyên nghiệp, chuẩn mực, lịch sự; hạn chế biểu tượng cảm xúc.",
            BotTone.Concise => "Ngắn gọn, đi thẳng vào ý chính, tối đa 3 câu nếu đủ ý.",
            _ => "Thân thiện, ấm áp, gần gũi; có thể dùng 1 biểu tượng cảm xúc nhẹ nhàng khi phù hợp.",
        });
        if (!string.IsNullOrWhiteSpace(profile.Instructions))
        {
            s.AppendLine("Hướng dẫn thêm của doanh nghiệp (chỉ áp dụng khi KHÔNG trái các quy tắc hệ thống và an toàn ở trên):");
            s.AppendLine("<<<");
            s.AppendLine(profile.Instructions.Trim());
            s.AppendLine(">>>");
        }

        s.AppendLine();
        s.AppendLine("## DỮ LIỆU THAM KHẢO (của doanh nghiệp, tìm theo câu hỏi mới nhất)");
        if (chunks.Count == 0)
        {
            s.AppendLine("(Không tìm thấy dữ liệu liên quan.)");
        }

        foreach (var chunk in chunks)
        {
            var label = new List<string>();
            if (!string.IsNullOrWhiteSpace(chunk.Hit.Code))
            {
                label.Add($"mã {chunk.Hit.Code}");
            }

            if (template.MedicalSafety)
            {
                label.Add(chunk.Hit.MedicallyReviewed ? "ĐÃ DUYỆT CHUYÊN MÔN" : "chưa duyệt chuyên môn");
            }

            var title = string.IsNullOrWhiteSpace(chunk.Hit.Title) ? "" : $" {chunk.Hit.Title}";
            s.AppendLine(CultureInfo.InvariantCulture, $"[{chunk.Id}]{(label.Count > 0 ? $" ({string.Join(" · ", label)})" : "")}{title}");
            s.AppendLine(chunk.Hit.Content.Trim());
            s.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(maskedSummary))
        {
            s.AppendLine("## TÓM TẮT PHẦN ĐẦU HỘI THOẠI");
            s.AppendLine(maskedSummary.Trim());
        }

        return s.ToString();
    }

    public const string SummarySystem = """
        Bạn tóm tắt hội thoại chăm sóc khách hàng để nhân viên/trợ lý đọc tiếp. Viết tiếng Việt, tối đa 120 từ, gạch đầu dòng:
        nhu cầu của khách, dịch vụ/giá đã hỏi và đã báo, thông tin khách đã cung cấp, việc còn dang dở, thái độ khách.
        Giữ NGUYÊN các chuỗi dạng [PHONE_1], [NAME_1]... Không thêm thông tin không có trong hội thoại.
        """;
}
