namespace ZaloAi.Core.Entities;

/// <summary>
/// Tỷ giá ngoại tệ → VND theo ngày (giờ Việt Nam), để quy chi phí AI (tính bằng USD) ra tiền Việt theo đúng ngày phát sinh. BẢNG HỆ
/// THỐNG, không thuộc tenant. Nguồn mặc định: Vietcombank (giá BÁN — giá ngân hàng bán USD khi thẻ trả tiền cho Google/Groq).
/// </summary>
public sealed class ExchangeRate
{
    /// <summary>Mã ngoại tệ ISO (USD).</summary>
    public required string Currency { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Giá bán (VND cho 1 đơn vị ngoại tệ).</summary>
    public decimal Sell { get; set; }

    /// <summary>Giá mua chuyển khoản (để tham khảo).</summary>
    public decimal Transfer { get; set; }

    /// <summary>vietcombank</summary>
    public required string Source { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}
