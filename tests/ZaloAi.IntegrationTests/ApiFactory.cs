using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ZaloAi.IntegrationTests;

/// <summary>
/// Chạy API trong môi trường "Testing" (không đọc user-secrets của máy dev), cấu hình hoàn toàn từ test.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static string NewEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    protected virtual Dictionary<string, string?> Settings => new()
    {
        ["App:AdminUrl"] = "http://localhost:3000",
        ["App:ApiUrl"] = "http://localhost:4000",
        ["Security:EncryptionKey"] = NewEncryptionKey(),
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings));
    }
}
