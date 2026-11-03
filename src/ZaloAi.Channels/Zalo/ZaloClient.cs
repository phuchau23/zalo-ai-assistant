using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;

namespace ZaloAi.Channels.Zalo;

/// <param name="ExpiresInSeconds">Hạn access token (Zalo trả chuỗi giây, thường "90000" = 25 giờ).</param>
public sealed record ZaloTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <param name="MessageId">message_id Zalo cấp cho tin vừa gửi.</param>
/// <param name="QuotaType">Nguồn quota (reply = khung 48h miễn phí...). null = tin tính phí.</param>
public sealed record ZaloSendResult(string? MessageId, string? QuotaType);

/// <summary>
/// Gọi API Zalo (docs/zalo-api-notes.md mục 2, 3, 6 — kiểm chứng 2026-10-04). Typed HttpClient có timeout + retry (resilience).
/// Không bao giờ log hay đưa token/secret/nội dung tin vào message lỗi.
/// </summary>
public sealed class ZaloClient(HttpClient http, IOptions<ZaloOptions> options)
{
    /// <summary>Giới hạn độ dài một tin tư vấn văn bản.</summary>
    public const int MaxTextLength = 2000;

    private readonly ZaloOptions _options = options.Value;

    /// <summary>Trang cấp quyền OA cho app (kiểm chứng bằng console: app_id, redirect_uri, code_challenge, state).</summary>
    public string BuildPermissionUrl(string codeChallenge, string state)
    {
        RequireConfigured();
        var query = string.Join("&",
            $"app_id={Uri.EscapeDataString(_options.AppId!)}",
            $"redirect_uri={Uri.EscapeDataString(_options.OAuthRedirectUrl!)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            $"state={Uri.EscapeDataString(state)}");
        return $"{_options.OAuthBaseUrl.TrimEnd('/')}/permission?{query}";
    }

    /// <summary>Đổi authorization code (1 lần, sống 10 phút) lấy access + refresh token.</summary>
    public Task<ZaloTokens> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("code", code),
                new("app_id", _options.AppId ?? ""),
                new("grant_type", "authorization_code"),
                new("code_verifier", codeVerifier),
            ],
            cancellationToken);

    /// <summary>Làm mới token. Refresh token chỉ dùng được 1 lần; access token cũ hết hiệu lực ngay khi có token mới.</summary>
    public Task<ZaloTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("refresh_token", refreshToken),
                new("app_id", _options.AppId ?? ""),
                new("grant_type", "refresh_token"),
            ],
            cancellationToken);

    /// <summary>Gửi tin tư vấn văn bản (≤ 2.000 ký tự — tin dài hơn phải cắt trước bằng <see cref="SplitText"/>).</summary>
    public async Task<ZaloSendResult> SendTextAsync(string accessToken, string userId, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Length > MaxTextLength)
        {
            throw new ArgumentException($"Tin vượt {MaxTextLength} ký tự.", nameof(text));
        }

        var body = new JsonObject
        {
            ["recipient"] = new JsonObject { ["user_id"] = userId },
            ["message"] = new JsonObject { ["text"] = text },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.OpenApiBaseUrl), "v3.0/oa/message/cs"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("access_token", accessToken);

        using var json = await SendAsync(request, cancellationToken);
        var root = json.RootElement;
        ThrowIfApiError(root);
        var data = root.TryGetProperty("data", out var d) ? d : default;
        string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var quota = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("quota", out var q) ? q : default;
        return new ZaloSendResult(Str(data, "message_id"), Str(quota, "quota_type"));
    }

    /// <summary>Cắt câu trả lời dài thành các tin ≤ 2.000 ký tự, ưu tiên cắt ở xuống dòng, rồi dấu câu, rồi khoảng trắng.</summary>
    public static IReadOnlyList<string> SplitText(string text, int max = MaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var window = rest[..max];
            var cut = new[] { window.LastIndexOf('\n'), window.LastIndexOf(". ", StringComparison.Ordinal) + 1, window.LastIndexOf(' ') }
                .FirstOrDefault(i => i > max / 2, max);
            parts.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            parts.Add(rest);
        }

        return parts;
    }

    private async Task<ZaloTokens> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        RequireConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.OAuthBaseUrl), "access_token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("secret_key", _options.AppSecret);

        using var json = await SendAsync(request, cancellationToken);
        var root = json.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var access = Str("access_token");
        var refresh = Str("refresh_token");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
        {
            // Định dạng lỗi của endpoint OAuth chưa kiểm chứng: lấy mã lỗi nếu có, KHÔNG đưa nguyên body vào message.
            ThrowIfApiError(root);
            throw new ZaloApiException(0, "Zalo không trả token (mã code/refresh token có thể sai hoặc hết hạn).");
        }

        var expires = root.TryGetProperty("expires_in", out var e)
            ? e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0
            : 0;
        return new ZaloTokens(access, refresh, expires > 0 ? expires : 25 * 3600);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ZaloTransientException("Không kết nối được Zalo (mạng/timeout).", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new ZaloTransientException($"Zalo trả HTTP {(int)response.StatusCode}.");
            }

            try
            {
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new ZaloApiException(0, $"Zalo trả dữ liệu không đọc được (HTTP {(int)response.StatusCode}).", ex);
            }
        }
    }

    private static void ThrowIfApiError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
        {
            return;
        }

        var code = error.ValueKind == JsonValueKind.Number ? error.GetInt32()
            : int.TryParse(error.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
        if (code == 0)
        {
            return;
        }

        // message của Zalo là câu mô tả lỗi cố định (vd "Access token is invalid"), không chứa dữ liệu khách — giữ để chẩn đoán.
        var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        throw code == -32 ? new ZaloTransientException($"Zalo -32: vượt giới hạn tốc độ ({message}).") : new ZaloApiException(code, message);
    }

    private void RequireConfigured()
    {
        if (!_options.IsConfigured)
        {
            throw new ZaloNotConfiguredException();
        }
    }
}

/// <summary>Lỗi tạm thời (mạng, 5xx, -32 vượt tốc độ): job thử lại sau.</summary>
public sealed class ZaloTransientException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Chưa đặt Zalo:AppId / AppSecret / OAuthRedirectUrl.</summary>
public sealed class ZaloNotConfiguredException()
    : Exception("Chưa cấu hình Zalo (Zalo:AppId, Zalo:AppSecret, Zalo:OAuthRedirectUrl).");

/// <summary>Zalo trả lỗi nghiệp vụ (trường "error" khác 0). Phân loại theo bảng mã lỗi trong docs/zalo-api-notes.md mục 7.</summary>
public sealed class ZaloApiException(int code, string? zaloMessage, Exception? innerException = null)
    : Exception($"Zalo lỗi {code}: {zaloMessage ?? "không rõ"}", innerException)
{
    public int Code { get; } = code;

    /// <summary>Access token hỏng/hết hạn → làm mới rồi thử lại 1 lần.</summary>
    public bool IsTokenError => Code is -216 or -220;

    /// <summary>OA/app không còn quyền → kết nối cần cấp quyền lại.</summary>
    public bool IsAuthorizationError => Code is -223 or -219 or -209 or -212 or -204 or -205;

    /// <summary>Người nhận không nhận được tin (chưa tương tác 7 ngày, khóa, chặn...) → không gửi lại.</summary>
    public bool IsRecipientError => Code is -213 or -227 or -230 or -232 or -244 or -218;

    public string ShortCode => $"zalo:{Code.ToString(CultureInfo.InvariantCulture)}";
}
