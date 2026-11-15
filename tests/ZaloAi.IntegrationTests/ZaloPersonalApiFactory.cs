using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.IntegrationTests;

/// <summary>Sidecar Zalo cá nhân giả: trả lời theo CONTRACT.md mục 3, ghi lại mọi request (không chạy zca-js, không gọi Zalo).</summary>
public sealed class FakeSidecarServer : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, string Path, string Body, string? Signature)> Calls { get; } = new();

    private int _messageCounter;

    /// <summary>Tìm nick theo SĐT: mặc định SĐT tận cùng 000 → không có Zalo, còn lại uid "u-<SĐT>".</summary>
    public Func<string, (HttpStatusCode Status, string Json)> FindUserResponse { get; set; } = body =>
    {
        var phone = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("phone").GetString()!;
        return phone.EndsWith("000", StringComparison.Ordinal)
            ? (HttpStatusCode.NotFound, """{"error":"not_found"}""")
            : (HttpStatusCode.OK, $$"""{"uid":"u-{{phone}}","displayName":"Zalo {{phone[^3..]}}"}""");
    };

    /// <summary>Kết quả kiểm tra proxy; mặc định proxy tốt.</summary>
    public Func<string, string> ProxyTestResponse { get; set; } = _ => """{"ok":true,"exitIp":"203.0.113.7","latencyMs":120}""";

    /// <summary>Phản hồi cho lệnh gửi tin; mặc định 200 {msgId: zp-msg-N}.</summary>
    public Func<string, (HttpStatusCode Status, string Json)> SendResponse { get; set; }

    public FakeSidecarServer()
    {
        SendResponse = _ => (HttpStatusCode.OK, $$"""{"msgId":"zp-msg-{{Interlocked.Increment(ref _messageCounter)}}"}""");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.PathAndQuery;
        request.Headers.TryGetValues(ZaloAi.Core.Security.RequestSignature.HeaderName, out var signature);
        Calls.Enqueue((request.Method.Method, path, body, signature?.FirstOrDefault()));

        var (status, json) =
            path.EndsWith("/send", StringComparison.Ordinal) ? SendResponse(body)
            : path == "/proxies/test" ? (HttpStatusCode.OK, ProxyTestResponse(body))
            : path.EndsWith("/find-user", StringComparison.Ordinal) ? FindUserResponse(body)
            : path == "/login-qr" || path.EndsWith("/start", StringComparison.Ordinal) ? (HttpStatusCode.Accepted, "")
            : (HttpStatusCode.NoContent, "");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>API test có cấu hình Zalo cá nhân + sidecar giả.</summary>
public sealed class ZaloPersonalApiFactory(
    string connectionString, string redisConnectionString, FakeSidecarServer sidecar, Dictionary<string, string?>? extra = null)
    : ApiFactory(connectionString, redisConnectionString: redisConnectionString)
{
    public const string SharedSecret = "test-shared-secret-test-shared-secret-123";

    /// <summary>Khóa mã hóa chung cho mọi test Zalo cá nhân: phiên + proxy là dữ liệu hệ thống dùng chung giữa các test cùng DB.</summary>
    public static readonly string EncryptionKey = NewEncryptionKey();

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Security:EncryptionKey"] = EncryptionKey;
            settings["ZaloPersonal:SidecarUrl"] = "http://sidecar.test";
            settings["ZaloPersonal:SharedSecret"] = SharedSecret;
            settings["ZaloPersonal:DebounceSeconds"] = "0";
            foreach (var (key, value) in extra ?? [])
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<SidecarClient>().ConfigurePrimaryHttpMessageHandler(() => sidecar));
    }
}
