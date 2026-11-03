using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>Lệnh kết nối gửi trong nhóm Telegram: "/ketnoi MÃ" hoặc "/start MÃ" (link mời bot vào nhóm), có thể kèm "@tên_bot".</summary>
public static partial class TelegramLinkCommand
{
    /// <summary>Bỏ chữ dễ nhầm (0/O, 1/I/L) cho người gõ tay.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int CodeLength = 6;

    public static string NewCode() =>
        string.Create(CodeLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });

    /// <returns>Mã (in hoa) nếu là lệnh kết nối; "" nếu là lệnh kết nối nhưng thiếu mã; null nếu không phải.</returns>
    public static string? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Command().Match(text.Trim());
        return match.Success ? match.Groups["code"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"^/(?:ketnoi|start)(?:@\w+)?(?:\s+(?<code>[A-Za-z0-9]{1,20}))?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Command();
}

/// <summary>
/// Kết nối nhóm Telegram tự phục vụ: chủ DN lấy mã trên trang Cài đặt (hạn 10 phút, lưu Redis), gõ "/ketnoi MÃ" trong nhóm có bot →
/// hệ thống lưu chat id của nhóm vào cài đặt của đúng DN và chào nhóm. Mã dùng 1 lần. Không ghi log nội dung tin nhóm.
/// </summary>
public sealed partial class TelegramLinkService(
    IDistributedStore store,
    TenantContext tenantContext,
    TenantRepository tenants,
    HandoffSettingsRepository settings,
    AuditLogRepository audit,
    TelegramClient telegram,
    ILogger<TelegramLinkService> logger)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private static string Key(string code) => $"telegram:link:{code}";

    public async Task<string> CreateCodeAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = TelegramLinkCommand.NewCode();
            if (await store.SetIfNotExistsAsync(Key(code), $"{tenantId:N}|{userId:N}", CodeLifetime, cancellationToken))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Không tạo được mã kết nối Telegram.");
    }

    /// <summary>Xử lý một tin trong nhóm (đã lọc là lệnh kết nối). Trả true nếu đã kết nối được.</summary>
    public async Task<bool> HandleCommandAsync(string chatId, string code, CancellationToken cancellationToken)
    {
        var value = code.Length == 0 ? null : await store.TakeAsync(Key(code), cancellationToken);
        var parts = value?.Split('|');
        if (parts is not { Length: 2 } || !Guid.TryParseExact(parts[0], "N", out var tenantId))
        {
            await telegram.SendAsync(chatId, "❌ Mã kết nối không đúng hoặc đã hết hạn (10 phút). Vào trang Cài đặt → Chuyển nhân viên → \"Kết nối Telegram\" để lấy mã mới.", cancellationToken);
            return false;
        }

        Guid? userId = Guid.TryParseExact(parts[1], "N", out var u) ? u : null;
        tenantContext.Set(tenantId, userId, role: null, isSuperAdmin: false);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        var s = await settings.GetOrCreateAsync(tenantId, cancellationToken);
        s.TelegramChatId = chatId;
        audit.Add(tenantId, userId, "tenant.telegram_connected", tenantId.ToString());
        await settings.SaveChangesAsync(cancellationToken);
        LogConnected(logger, tenantId);

        await telegram.SendAsync(
            chatId,
            $"✅ Đã kết nối nhóm này với \"{tenant.Name}\".\nTừ giờ nhóm sẽ nhận thông báo khi khách cần nhân viên, khách chờ lâu và ca khẩn cấp. Thông báo chỉ có loại sự việc và link hộp thư, không có nội dung tin của khách.",
            cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Đã kết nối nhóm Telegram cho tenant {TenantId}")]
    private static partial void LogConnected(ILogger logger, Guid tenantId);
}

/// <summary>Gọi getUpdates (long polling) — HttpClient riêng vì một lần chờ tới 25 giây, dài hơn timeout gửi tin.</summary>
public sealed class TelegramUpdatesClient(HttpClient http, IOptions<TelegramOptions> options)
{
    public const int PollSeconds = 25;

    private static readonly ConcurrentDictionary<string, string> Usernames = new(StringComparer.Ordinal);

    public sealed record Update(long UpdateId, string? ChatId, string? Text);

    /// <summary>Tin mới kể từ <paramref name="offset"/> (gọi với offset = update_id cuối + 1 là xác nhận đã xử lý các tin trước).</summary>
    public async Task<IReadOnlyList<Update>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var uri = Method("getUpdates", $"?timeout={PollSeconds}&offset={offset.ToString(CultureInfo.InvariantCulture)}&allowed_updates=%5B%22message%22%5D");
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var updates = new List<Update>();
        foreach (var item in json.RootElement.GetProperty("result").EnumerateArray())
        {
            string? chatId = null, text = null;
            if (item.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id))
                {
                    chatId = id.GetRawText();
                }

                if (message.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    text = t.GetString();
                }
            }

            updates.Add(new Update(item.GetProperty("update_id").GetInt64(), chatId, text));
        }

        return updates;
    }

    /// <summary>Tên bot (getMe) để dựng link mời bot vào nhóm; lưu tạm trong bộ nhớ. null nếu chưa cấu hình / lỗi.</summary>
    public async Task<string?> GetBotUsernameAsync(CancellationToken cancellationToken)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (Usernames.TryGetValue(token, out var cached))
        {
            return cached;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var me = await http.GetFromJsonAsync<JsonElement>(Method("getMe", ""), timeout.Token);
            var username = me.GetProperty("result").GetProperty("username").GetString();
            if (username is not null)
            {
                Usernames[token] = username;
            }

            return username;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Ghép chuỗi (token có dấu ":"), xem TelegramClient.SendMessageUri.
    private Uri Method(string name, string query) =>
        new($"{options.Value.ApiBaseUrl.TrimEnd('/')}/bot{options.Value.BotToken}/{name}{query}");
}

/// <summary>
/// Chạy trong Worker: đọc tin gửi cho bot (long polling) để nhận lệnh kết nối nhóm. Nhiều bản Worker → chỉ một bản đọc tại một
/// thời điểm (khóa Redis), vì Telegram chỉ cho một kết nối getUpdates. Bot chưa cấu hình → không làm gì.
/// </summary>
public sealed partial class TelegramPollingService(
    IServiceScopeFactory scopes,
    IDistributedStore store,
    IOptions<TelegramOptions> options,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            return;
        }

        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var held = await store.TryAcquireLockAsync("telegram:lock:poll", TimeSpan.FromSeconds(TelegramUpdatesClient.PollSeconds + 20), stoppingToken);
                if (held is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }

                using var scope = scopes.CreateScope();
                var updates = await scope.ServiceProvider.GetRequiredService<TelegramUpdatesClient>().GetUpdatesAsync(offset, stoppingToken);
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    if (update.ChatId is not null && TelegramLinkCommand.TryParse(update.Text) is { } code)
                    {
                        // Scope riêng mỗi lệnh: TenantContext chỉ được đặt một lần trong một scope.
                        using var commandScope = scopes.CreateScope();
                        await commandScope.ServiceProvider.GetRequiredService<TelegramLinkService>().HandleCommandAsync(update.ChatId, code, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Chỉ ghi loại lỗi: message của HttpClient có thể chứa URL kèm token.
                LogPollFailed(logger, ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Đọc tin Telegram lỗi ({ErrorType}), thử lại sau 30 giây")]
    private static partial void LogPollFailed(ILogger logger, string errorType);
}
