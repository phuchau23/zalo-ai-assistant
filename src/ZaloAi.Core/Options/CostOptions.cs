using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>
/// Quy chi phí AI ra tiền Việt (chủ dự án chốt 2026-11-15: chỉ tính AI, tỷ giá Vietcombank tự động theo ngày, super admin + chủ DN xem).
/// Chi phí gốc ghi bằng USD (token × bảng giá <see cref="AiOptions.Pricing"/>); VND = USD × giá bán USD của Vietcombank đúng ngày phát
/// sinh × (1 + phí thẻ). Số thật trên sao kê còn phụ thuộc ngày ngân hàng trừ tiền và thuế — xem DECISIONS 2026-11-15.
/// </summary>
public sealed class CostOptions
{
    public const string SectionName = "Cost";

    /// <summary>Phí giao dịch ngoại tệ của thẻ trả tiền AI (%), ví dụ 2.5. 0 = không cộng.</summary>
    [Range(0, 10)]
    public decimal CardFeePercent { get; set; }

    /// <summary>Kiểm chứng 2026-11-15: GET {base}api/exchangerates?date=yyyy-MM-dd → Data[].currencyCode/cash/transfer/sell (chuỗi "25910.00").</summary>
    [Required, Url]
    public string VietcombankBaseUrl { get; set; } = "https://www.vietcombank.com.vn/";

    /// <summary>Báo cáo tối đa bao nhiêu ngày một lần (lấy tỷ giá còn thiếu cho từng ngày).</summary>
    [Range(1, 400)]
    public int MaxReportDays { get; set; } = 93;
}
