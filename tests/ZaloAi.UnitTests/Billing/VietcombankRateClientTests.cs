using System.Text.Json;
using Shouldly;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.UnitTests.Billing;

/// <summary>Đọc tỷ giá Vietcombank (định dạng kiểm chứng 2026-11-15 từ /api/exchangerates?date=...).</summary>
public sealed class VietcombankRateClientTests
{
    private const string Sample = """
        {"Count":3,"Date":"2026-11-14T00:00:00","UpdatedDate":"2026-11-14T23:00:00+07:00","Data":[
          {"currencyName":"EURO","currencyCode":"EUR","cash":"29500.00","transfer":"29800.00","sell":"31000.00"},
          {"currencyName":"US DOLLAR","currencyCode":"USD","cash":"25500.00","transfer":"25530.00","sell":"25910.00"},
          {"currencyName":"KUWAITI DINAR","currencyCode":"KWD","cash":"0.00","transfer":"83000.00","sell":"0.00"}]}
        """;

    private static readonly DateOnly Day = new(2026, 11, 14);

    [Fact]
    public void Reads_usd_sell_and_transfer()
    {
        using var json = JsonDocument.Parse(Sample);

        var rate = VietcombankRateClient.Parse(json.RootElement, "USD", Day).ShouldNotBeNull();

        rate.Sell.ShouldBe(25910.00m);
        rate.Transfer.ShouldBe(25530.00m);
        rate.Date.ShouldBe(Day);
    }

    [Fact]
    public void Zero_or_missing_currency_means_no_rate()
    {
        using var json = JsonDocument.Parse(Sample);

        VietcombankRateClient.Parse(json.RootElement, "KWD", Day).ShouldBeNull(); // "0.00" = không có giá, không phải 0đ
        VietcombankRateClient.Parse(json.RootElement, "JPY", Day).ShouldBeNull();
        using var empty = JsonDocument.Parse("""{"Count":0}""");
        VietcombankRateClient.Parse(empty.RootElement, "USD", Day).ShouldBeNull();
    }

    [Fact]
    public void Thousand_separators_are_accepted()
    {
        using var json = JsonDocument.Parse("""{"Data":[{"currencyCode":"USD","transfer":"25,530.00","sell":"25,910.00"}]}""");

        VietcombankRateClient.Parse(json.RootElement, "USD", Day)!.Sell.ShouldBe(25910m);
    }
}
