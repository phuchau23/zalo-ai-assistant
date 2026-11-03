using System.Text.Json;

namespace ZaloAi.Channels.Zalo;

/// <param name="Type">image, gif, link, audio, video, sticker, location, file...</param>
public sealed record ZaloAttachment(string Type, string? Url);

/// <summary>
/// Sự kiện webhook Zalo đã đọc (docs/zalo-api-notes.md mục 4–5). Chỉ giữ trường cần dùng.
/// Tin khách gửi (user_send_*): Sender = user, Recipient = OA. OA gửi (oa_send_*): Sender = OA, Recipient = user,
/// SenderAdminId có khi nhân viên nhắn bằng tool chat OA (không có = tin gửi qua API, tức chính bot).
/// </summary>
public sealed record ZaloWebhookEvent(
    string AppId,
    string EventName,
    string? SenderId,
    string? SenderAdminId,
    string? RecipientId,
    string? MessageId,
    string? Text,
    IReadOnlyList<ZaloAttachment> Attachments,
    string Timestamp)
{
    public bool IsUserMessage => EventName.StartsWith("user_send_", StringComparison.Ordinal);

    public bool IsOaMessage => EventName.StartsWith("oa_send_", StringComparison.Ordinal);

    /// <summary>OA ID của sự kiện tin nhắn (khách gửi → người nhận là OA; OA gửi → người gửi là OA).</summary>
    public string? OaId => IsOaMessage ? SenderId : RecipientId;

    /// <summary>User ID khách của sự kiện tin nhắn.</summary>
    public string? UserId => IsOaMessage ? RecipientId : SenderId;

    /// <summary>Đọc body JSON. Sai định dạng / thiếu trường bắt buộc → null.</summary>
    public static ZaloWebhookEvent? Parse(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                    ? v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number => v.GetRawText(),
                        _ => null,
                    }
                    : null;
            static JsonElement Obj(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

            var appId = Str(root, "app_id");
            var eventName = Str(root, "event_name");
            var timestamp = Str(root, "timestamp");
            if (appId is null || eventName is null || timestamp is null)
            {
                return null;
            }

            var sender = Obj(root, "sender");
            var message = Obj(root, "message");
            var attachments = new List<ZaloAttachment>();
            if (message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var type = Str(a, "type");
                    if (type is not null)
                    {
                        attachments.Add(new ZaloAttachment(type, Str(Obj(a, "payload"), "url")));
                    }
                }
            }

            return new ZaloWebhookEvent(
                appId,
                eventName,
                Str(sender, "id"),
                Str(sender, "admin_id"),
                Str(Obj(root, "recipient"), "id"),
                Str(message, "msg_id"),
                Str(message, "text"),
                attachments,
                timestamp);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
