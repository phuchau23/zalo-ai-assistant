using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.IntegrationTests;

/// <summary>
/// Chạy API trong môi trường "Testing" (không đọc user-secrets của máy dev), cấu hình hoàn toàn từ test.
/// </summary>
public class ApiFactory(
    string connectionString = "Host=localhost;Database=unused",
    int loginPermitPerMinute = 1000,
    string redisConnectionString = "localhost:1")
    : WebApplicationFactory<Program>
{
    public static string NewEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Thư mục file tạm dùng chung cho các test (không đụng thư mục dev thật).</summary>
    public static string TestStorageRoot { get; } = Path.Combine(Path.GetTempPath(), "zaloai-tests", Guid.NewGuid().ToString("N"));

    protected virtual Dictionary<string, string?> Settings => new()
    {
        ["App:AdminUrl"] = "http://localhost:3000",
        ["App:ApiUrl"] = "http://localhost:4000",
        ["Security:EncryptionKey"] = NewEncryptionKey(),
        ["ConnectionStrings:Postgres"] = connectionString,
        ["ConnectionStrings:Redis"] = redisConnectionString,
        ["RateLimit:LoginPermitPerMinute"] = loginPermitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Ai:EmbedProvider"] = "fake",
        ["Ai:ChatProvider"] = "fake",
        ["Ai:BotMessageGapMs"] = "0",
        ["Storage:LocalRoot"] = TestStorageRoot,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings));

        // Không gọi Vietcombank thật trong test: mọi ngày trả giá bán USD cố định.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<VietcombankRateClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeVietcombankHandler()));
    }
}

/// <summary>Vietcombank giả: giá bán USD luôn 25.000 đ (định dạng thật của /api/exchangerates).</summary>
public sealed class FakeVietcombankHandler : HttpMessageHandler
{
    public const decimal Sell = 25000m;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Data":[{"currencyCode":"USD","cash":"24600.00","transfer":"24630.00","sell":"25000.00"}]}""", Encoding.UTF8, "application/json"),
        });
}
