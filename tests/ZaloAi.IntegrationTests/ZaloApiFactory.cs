using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Channels.Zalo;

namespace ZaloAi.IntegrationTests;

/// <summary>Zalo giả: trả token / kết quả gửi tin theo URL, ghi lại mọi request (không gọi Zalo thật).</summary>
public sealed class FakeZaloServer : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, string Body, Dictionary<string, string> Headers)> Calls { get; } = new();

    /// <summary>Phản hồi cho endpoint gửi tin; mặc định thành công.</summary>
    public Func<string, string> SendResponse { get; set; } =
        _ => """{"data":{"message_id":"zalo-msg-1","user_id":"u","sent_time":"1","quota":{"quota_type":"reply"}},"error":0,"message":"Success"}""";

    private int _tokenCounter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var url = request.RequestUri!.ToString();
        Calls.Enqueue((url, body, headers));

        var json = url.EndsWith("/access_token", StringComparison.Ordinal)
            ? $$"""{"access_token":"AT-{{Interlocked.Increment(ref _tokenCounter)}}","refresh_token":"RT-{{_tokenCounter}}","expires_in":"90000"}"""
            : SendResponse(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>API test có cấu hình Zalo + Zalo giả.</summary>
public sealed class ZaloApiFactory(string connectionString, string redisConnectionString, FakeZaloServer zalo)
    : ApiFactory(connectionString, redisConnectionString: redisConnectionString)
{
    public const string AppId = "35296570815181295";
    public const string WebhookSecret = "test-oa-secret-key";

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Zalo:AppId"] = AppId;
            settings["Zalo:AppSecret"] = "test-app-secret";
            settings["Zalo:OAuthRedirectUrl"] = "https://dev-api.test.vn/connect/zalo/callback";
            settings["Zalo:WebhookSecret"] = WebhookSecret;
            return settings;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<ZaloClient>().ConfigurePrimaryHttpMessageHandler(() => zalo));
    }
}
