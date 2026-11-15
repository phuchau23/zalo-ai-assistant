using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <param name="Code">Mã lỗi Zalo/zca-js (không chứa nội dung tin).</param>
public sealed record SidecarRejection(string? Code, string? Reason);

/// <summary>
/// Kết quả gửi: có <see cref="MessageId"/> = đã gửi; có <see cref="Rejection"/> = Zalo từ chối (gửi lại vô ích);
/// <see cref="Superseded"/> = khách nhắn tin mới hơn trong lúc "đang soạn" → sidecar không gửi (CONTRACT.md mục 5).
/// </summary>
public sealed record SidecarSendResult(string? MessageId, SidecarRejection? Rejection, bool Superseded = false);

public sealed record SidecarFoundUser(string Uid, string? DisplayName, string? AvatarUrl);

public sealed record SidecarProxyTest(bool Ok, string? ExitIp, int? LatencyMs, string? Error);

/// <summary>
/// Gọi sidecar Zalo cá nhân (connectors/zalo-personal/CONTRACT.md mục 3). Mọi request ký HMAC. Sidecar chưa chạy / lỗi mạng →
/// <see cref="ChannelUnavailableException"/> (lỗi tạm, job thử lại). Không tự thử lại ở tầng HTTP (gửi tin không idempotent ở Zalo;
/// sidecar tự chống gửi trùng theo messageId, job thử lại an toàn).
/// </summary>
public sealed class SidecarClient(HttpClient http, IOptions<ZaloPersonalOptions> options, TimeProvider time)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Task StartLoginAsync(string loginId, string userAgent, string? proxyUrl, CancellationToken cancellationToken) =>
        ExpectSuccessAsync(HttpMethod.Post, "/login-qr", new { loginId, userAgent, proxyUrl }, cancellationToken);

    public Task CancelLoginAsync(string loginId, CancellationToken cancellationToken) =>
        ExpectSuccessAsync(HttpMethod.Post, $"/login-qr/{Uri.EscapeDataString(loginId)}/cancel", null, cancellationToken);

    /// <param name="session">JSON phiên đã giải mã — chỉ nằm trong bộ nhớ, không log.</param>
    public Task StartAccountAsync(Guid connectionId, JsonElement session, string? proxyUrl, CancellationToken cancellationToken) =>
        ExpectSuccessAsync(HttpMethod.Post, $"/accounts/{connectionId}/start", new { session, proxyUrl }, cancellationToken);

    public Task StopAccountAsync(Guid connectionId, CancellationToken cancellationToken) =>
        ExpectSuccessAsync(HttpMethod.Post, $"/accounts/{connectionId}/stop", null, cancellationToken);

    /// <param name="replyTo">Id Zalo của tin khách mà tin này trả lời: tin khách mới nhất trong thread đã khác → sidecar bỏ, không gửi.</param>
    public async Task<SidecarSendResult> SendAsync(
        Guid connectionId, Guid messageId, string threadId, string text, int typingMs, CancellationToken cancellationToken, string? replyTo = null)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, $"/accounts/{connectionId}/send", new { messageId, threadId, text, typingMs, replyTo }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var ok = await response.Content.ReadFromJsonAsync<SendOk>(Json, cancellationToken);
            return new SidecarSendResult(ok?.MsgId, null);
        }

        if (response.StatusCode == HttpStatusCode.Conflict && (await ReadErrorAsync(response, cancellationToken))?.Error == "superseded")
        {
            return new SidecarSendResult(null, null, Superseded: true);
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            var code = error?.Code is { ValueKind: JsonValueKind.String or JsonValueKind.Number } c
                ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText())
                : null;
            return new SidecarSendResult(null, new SidecarRejection(Trim(code, 40), Trim(error?.Reason, 200)));
        }

        throw Unavailable(response.StatusCode);
    }

    /// <summary>null = không có nick Zalo nào đăng ký SĐT này.</summary>
    public async Task<SidecarFoundUser?> FindUserAsync(Guid connectionId, string phone, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"/accounts/{connectionId}/find-user", new { phone }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Unavailable(response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<SidecarFoundUser>(Json, cancellationToken);
    }

    /// <returns>true = đã gửi lời mời; false = Zalo từ chối (422 — gửi lại vô ích, vd đã là bạn / đã mời / bị chặn).</returns>
    public async Task<bool> SendFriendRequestAsync(Guid connectionId, string uid, string message, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"/accounts/{connectionId}/friend-request", new { uid, message }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable(response.StatusCode);
        }

        return true;
    }

    public Task UndoFriendRequestAsync(Guid connectionId, string uid, CancellationToken cancellationToken) =>
        ExpectSuccessAsync(HttpMethod.Post, $"/accounts/{connectionId}/undo-friend-request", new { uid }, cancellationToken);

    /// <summary>Sidecar thử kết nối QUA proxy tới dịch vụ trả IP công khai (CONTRACT.md mục 3). URL proxy có mật khẩu — không log.</summary>
    public async Task<SidecarProxyTest> TestProxyAsync(string proxyUrl, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Post, "/proxies/test", new { proxyUrl }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Unavailable(response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<SidecarProxyTest>(Json, cancellationToken) ?? new SidecarProxyTest(false, null, null, "empty");
    }

    /// <summary>Chỉ dev + sidecar chế độ giả lập: chuyển nguyên lệnh giả lập (CONTRACT.md mục 7).</summary>
    /// <summary>
    /// Chỉ dev (sidecar giả lập). Sidecar giữ phiên QR / nick trong bộ nhớ: 404 = không còn phiên đó (thường vì sidecar vừa khởi động
    /// lại), 409 = mã QR không còn ở bước chờ quét. Báo rõ để người test không tưởng máy chủ hỏng.
    /// </summary>
    public async Task SimulateAsync(JsonElement command, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(HttpMethod.Post, "/dev/simulate", command, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new ConflictException(
                "Máy chủ kết nối Zalo cá nhân không còn phiên này (nick chưa chạy, hoặc máy chủ vừa khởi động lại). Với mã QR: bấm Hủy rồi kết nối lại.",
                "zalo_personal_sim_gone"),
            HttpStatusCode.Conflict => new ConflictException("Mã QR không còn ở bước chờ quét. Bấm Hủy rồi kết nối lại.", "zalo_personal_sim_not_waiting"),
            _ => (Exception)Unavailable(response.StatusCode),
        };
    }

    private async Task ExpectSuccessAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable(response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            throw new ChannelUnavailableException("Chưa cấu hình ZaloPersonal:SidecarUrl / SharedSecret.");
        }

        var json = body is null ? "" : JsonSerializer.Serialize(body, Json);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(o.SidecarUrl!), path));
        if (body is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation(
            RequestSignature.HeaderName,
            RequestSignature.Create(o.SharedSecret!, time.GetUtcNow(), method.Method, request.RequestUri!.PathAndQuery, json));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ChannelUnavailableException("Không gọi được máy chủ kết nối Zalo cá nhân.", ex);
        }
    }

    private static async Task<ErrorBody?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChannelUnavailableException Unavailable(HttpStatusCode status) =>
        new($"Máy chủ kết nối Zalo cá nhân trả {(int)status}.");

    private static string? Trim(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;

    private sealed record SendOk(string? MsgId);

    private sealed record ErrorBody(string? Error, JsonElement? Code, string? Reason);
}
