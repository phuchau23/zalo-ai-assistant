namespace ZaloAi.Infrastructure.Knowledge;

public static partial class KnowledgeTemplate
{
    /// <summary>
    /// Câu lệnh mẫu để doanh nghiệp nhờ AI bên ngoài (ChatGPT, Claude, Gemini) chuyển tài liệu lộn xộn sang JSON đúng mẫu.
    /// Đổi định dạng mẫu thì sửa cả ở đây và docs/KNOWLEDGE-FORMAT.md mục 6.
    /// </summary>
    public const string AiPrompt = """
        Bạn là trợ lý nhập liệu. Hãy chuyển tài liệu tôi gửi kèm thành JSON đúng định dạng dưới đây, KHÔNG thêm lời giải thích, chỉ trả về JSON.

        Định dạng:
        {
          "format": "zaloai-knowledge",
          "version": 1,
          "info":     [ { "code", "title", "content" } ],
          "services": [ { "code", "group", "name", "durationMinutes", "price", "priceUnit", "priceNote", "description", "suitableFor", "notSuitableFor", "branches", "note" } ],
          "packages": [ { "code", "group", "name", "sessions", "minutesPerSession", "price", "priceNote", "description", "suitableFor", "notSuitableFor", "note" } ],
          "faqs":     [ { "code", "question", "answer" } ],
          "policies": [ { "code", "topic", "content" } ]
        }

        Quy tắc:
        1. "code": chữ in hoa không dấu, số, dấu gạch ngang, 2–50 ký tự, không trùng nhau. Tiền tố: TT- (thông tin chung), DV- (dịch vụ), GOI- (gói nhiều buổi), FAQ- (câu hỏi), CS- (chính sách). Đặt mã dễ hiểu từ tên, ví dụ "Massage Đông y 60 phút" → "DV-MASSAGE-DONG-Y-60".
        2. "price": số nguyên VNĐ (450.000đ → 450000). Không rõ giá → null và ghi lý do vào "priceNote". KHÔNG tự đoán giá.
        3. Mỗi mức thời lượng/giá khác nhau của cùng dịch vụ là một mục riêng.
        4. Gói nhiều buổi đưa vào "packages", ghi "sessions" và "minutesPerSession".
        5. Địa chỉ, chi nhánh, hotline, giờ mở cửa, cách đặt lịch, thanh toán → "info" (mỗi thông tin một mục).
        6. Đặt lịch, hủy/đổi lịch, hoàn tiền, bảo hành, ưu đãi → "policies".
        7. Giữ nguyên nội dung gốc, không thêm thông tin không có trong tài liệu. Trường không có thông tin → null.
        8. Nội dung tiếng Việt có dấu.
        """;
}
