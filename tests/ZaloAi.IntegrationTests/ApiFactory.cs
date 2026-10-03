using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ZaloAi.IntegrationTests;

/// <summary>
/// Chạy API trong môi trường "Testing" (không đọc user-secrets của máy dev), cấu hình hoàn toàn từ test.
/// </summary>
public class ApiFactory(string connectionString = "Host=localhost;Database=unused", int loginPermitPerMinute = 1000)
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
        ["RateLimit:LoginPermitPerMinute"] = loginPermitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Ai:EmbedProvider"] = "fake",
        ["Storage:LocalRoot"] = TestStorageRoot,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings));
    }
}
