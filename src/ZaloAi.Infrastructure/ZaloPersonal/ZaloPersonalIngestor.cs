using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Một sự kiện sidecar gửi lên (CONTRACT.md mục 4, PersonalEvent). Trường nào dùng tùy <see cref="Type"/>.</summary>
public sealed record PersonalEvent(
    string Type,
    string? MsgId = null,
    string? ThreadId = null,
    string? FromUid = null,
    string? FromName = null,
    bool IsSelf = false,
    bool IsFriend = false,
    string? Kind = null,
    string? Text = null,
    long Ts = 0,
    string? Event = null,
    string? Uid = null,
    string? State = null,
    int? Code = null,
    string? Detail = null);

/// <summary>
/// Sự kiện từ nick Zalo cá nhân → dữ liệu CRM (docs/ZALO-PERSONAL-PLAN.md mục 6.2–6.5). Lọc chuyện riêng TRƯỚC khi lưu:
/// bạn bè chưa phân loại / riêng tư → chỉ lưu tên + giờ (hoặc không gì), không lưu nội dung, không gửi AI. Tin khách lưu mã hóa,
/// chống trùng theo msgId (Redis + unique DB), xếp job trả lời sau vài giây để gom tin. Không log nội dung, tên, uid.
/// </summary>
public sealed partial class ZaloPersonalIngestor(
    ZaloPersonalLookup lookup,
    TenantContext tenantContext,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    ConversationRepository conversations,
    OutreachRepository outreach,
    IDistributedStore store,
    IFieldEncryptor encryptor,
    IBackgroundJobClient jobs,
    IInboxNotifier notifier,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ZaloPersonalIngestor> logger)
{
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromDays(1);

    /// <summary>false = nick không còn (đã ngắt) → sidecar dừng nick đó.</summary>
    public async Task<bool> HandleAsync(Guid connectionId, IReadOnlyList<PersonalEvent> events, CancellationToken cancellationToken)
    {
        var route = await lookup.FindRouteAsync(connectionId, cancellationToken);
        if (route is null)
        {
            return false;
        }

        tenantContext.Set(route.TenantId, userId: null, role: null, isSuperAdmin: false);
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case "state":
                    await ApplyStateAsync(route, e, cancellationToken);
                    break;
                case "message" when route is { Status: ConnectionStatus.Active, TenantActive: true }:
                    await HandleMessageAsync(route, e, cancellationToken);
                    break;
                case "friend":
                    await HandleFriendAsync(route, e, cancellationToken);
                    break;
                default:
                    LogIgnored(logger, e.Type);
                    break;
            }
        }

        return true;
    }

    /// <summary>Zalo làm mới cookie trong lúc chạy → lưu phiên mới (mã hóa). false = nick không còn.</summary>
    public async Task<bool> ReplaceSessionAsync(Guid connectionId, System.Text.Json.JsonElement session, CancellationToken cancellationToken)
    {
        var route = await lookup.FindRouteAsync(connectionId, cancellationToken);
        if (route is null || !ZaloPersonalLoginService.IsValidSession(session))
        {
            return false;
        }

        tenantContext.Set(route.TenantId, userId: null, role: null, isSuperAdmin: false);
        var account = await personal.GetAccountAsync(route.TenantId, connectionId, cancellationToken);
        if (account is null)
        {
            return false;
        }

        account.SessionEnc = encryptor.Encrypt(session.GetRawText());
        account.SessionSavedAt = time.GetUtcNow();
        await personal.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task HandleMessageAsync(PersonalRoute route, PersonalEvent e, CancellationToken cancellationToken)
    {
        var tenantId = route.TenantId;
        var customerUid = e.IsSelf ? e.ThreadId : e.FromUid;
        if (string.IsNullOrEmpty(e.MsgId) || string.IsNullOrEmpty(customerUid) || customerUid.Length > 100)
        {
            return;
        }

        var now = time.GetUtcNow();
        var peer = await personal.GetPeerAsync(tenantId, route.ConnectionId, customerUid, cancellationToken);
        if (e.IsSelf)
        {
            // Chủ nick nhắn tay. Chỉ lưu khi người kia là KHÁC; nhắn bạn bè/gia đình thì không đụng tới.
            if (peer is not { Kind: PeerKind.Customer })
            {
                return;
            }

            await SaveStaffMessageAsync(route, peer, e, now, cancellationToken);
            return;
        }

        var account = await personal.GetAccountAsync(tenantId, route.ConnectionId, cancellationToken);
        if (peer is null)
        {
            var kind = e.IsFriend && account is not { TreatFriendsAsCustomers: true } ? PeerKind.Pending : PeerKind.Customer;
            peer = personal.AddPeer(tenantId, new ZaloPeer
            {
                ConnectionId = route.ConnectionId,
                ExternalUserId = customerUid,
                DisplayName = Short(e.FromName),
                IsFriend = e.IsFriend,
                Kind = kind,
                FirstSeenAt = now,
                LastSeenAt = now,
            });
        }
        else
        {
            peer.LastSeenAt = now;
            peer.IsFriend = e.IsFriend;
            peer.DisplayName = Short(e.FromName) ?? peer.DisplayName;
        }

        if (peer.Kind != PeerKind.Customer)
        {
            // Bạn bè chưa phân loại / riêng tư: chỉ cập nhật tên + giờ, KHÔNG lưu nội dung, không gửi AI.
            await TrySaveAsync(cancellationToken);
            if (peer.Kind == PeerKind.Pending)
            {
                await notifier.PublishAsync(tenantId, new InboxEvent("peer", route.ConnectionId), cancellationToken);
            }

            return;
        }

        if (!await store.SetIfNotExistsAsync($"zp:event:{route.ConnectionId:N}:{e.MsgId}", "1", DedupeTtl, cancellationToken))
        {
            return;
        }

        var contact = await conversations.GetOrAddContactAsync(tenantId, ChannelKind.ZaloPersonal, customerUid, Short(e.FromName), cancellationToken);
        peer.ContactId ??= contact.Id;
        var conversation = await GetConversationAsync(route, contact, cancellationToken);
        var message = conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = encryptor.Encrypt(ContentOf(e)),
            ExternalMessageId = e.MsgId,
        });
        conversation.LastCustomerMessageAt = conversation.LastMessageAt = now;
        if (conversation.Mode == ConversationMode.Human)
        {
            conversation.NeedsAttentionSince ??= now; // nhân viên đang xử lý: khách chờ người trả lời
        }

        // Người DN dán SĐT đã trả lời → từ đây bot tư vấn như khách bình thường (màn hình "Đang chăm sóc": đang tư vấn).
        // Chưa là bạn bè và chưa từng mời → vài phút sau gửi lời mời kết bạn (chủ dự án chốt 2026-11-14: mời SAU khi khách trả lời —
        // khách đã trò chuyện nên ít bị từ chối, nick an toàn hơn mời cùng tin chào). Job chăm sóc gửi, theo giới hạn lời mời/ngày.
        if (await outreach.FindActiveByUidAsync(tenantId, route.ConnectionId, customerUid, cancellationToken) is { } target)
        {
            target.State = OutreachState.Replied;
            target.RepliedAt = now;
            target.NextActionAt = !e.IsFriend && target.FriendRequestSentAt is null
                ? now + OutreachTenantJob.InviteAfterReplyDelay
                : null;
        }

        if (!await TrySaveAsync(cancellationToken))
        {
            return;
        }

        // Gom tin: job chạy sau vài giây; khách nhắn tiếp thì job của tin cũ thấy có tin mới hơn và để job tin mới trả lời gộp.
        // Ban đêm gom lâu hơn (~1–2 phút) — nick cá nhân trả lời trong vài giây lúc 3 giờ sáng nhìn rất giống máy.
        var messageId = message.Id;
        var delay = TimeSpan.FromSeconds(PersonalPacing.IsNight(options.Value, now) ? options.Value.NightDebounceSeconds : options.Value.DebounceSeconds);
        if (delay > TimeSpan.Zero)
        {
            jobs.Schedule<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, messageId, CancellationToken.None), delay);
        }
        else
        {
            jobs.Enqueue<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, messageId, CancellationToken.None));
        }

        await notifier.PublishAsync(tenantId, new InboxEvent(conversation.Mode == ConversationMode.Human ? "attention" : "message", conversation.Id), cancellationToken);
    }

    /// <summary>Chủ nick nhắn tay trên điện thoại → lưu tin nhân viên, bot tạm im ở hội thoại đó (không chuyển hẳn sang nhân viên).</summary>
    private async Task SaveStaffMessageAsync(PersonalRoute route, ZaloPeer peer, PersonalEvent e, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (peer.ContactId is not { } contactId
            || await conversations.GetContactAsync(route.TenantId, contactId, cancellationToken) is not { } contact
            || !await store.SetIfNotExistsAsync($"zp:event:{route.ConnectionId:N}:{e.MsgId}", "1", DedupeTtl, cancellationToken))
        {
            return;
        }

        var account = await personal.GetAccountAsync(route.TenantId, route.ConnectionId, cancellationToken);
        var conversation = await GetConversationAsync(route, contact, cancellationToken);
        conversations.AddMessage(route.TenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            Sender = MessageSender.Staff,
            ContentEnc = encryptor.Encrypt(e.Kind == "text" && !string.IsNullOrWhiteSpace(e.Text) ? Clip(e.Text) : "[Tin nhắn từ điện thoại]"),
            ExternalMessageId = e.MsgId,
            DeliveryStatus = DeliveryStatus.Sent,
        });
        conversation.BotPausedUntil = now.AddMinutes(account?.StaffPauseMinutes ?? 30);
        conversation.NeedsAttentionSince = null; // chủ nick đã trả lời
        conversation.LastReminderAt = null;
        conversation.LastMessageAt = now;

        if (await TrySaveAsync(cancellationToken))
        {
            await notifier.PublishAsync(route.TenantId, new InboxEvent("message", conversation.Id), cancellationToken);
        }
    }

    private async Task HandleFriendAsync(PersonalRoute route, PersonalEvent e, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(e.Uid))
        {
            return;
        }

        var peer = await personal.GetPeerAsync(route.TenantId, route.ConnectionId, e.Uid, cancellationToken);
        if (peer is null)
        {
            return;
        }

        var target = await outreach.FindActiveByUidAsync(route.TenantId, route.ConnectionId, e.Uid, cancellationToken);
        switch (e.Event)
        {
            case "add":
                peer.IsFriend = true;
                if (target is { State: OutreachState.Invited })
                {
                    // Khách chấp nhận lời mời → tới lượt gửi tin đầu (job hàng đợi, trong khung giờ được nhắn).
                    target.State = OutreachState.Friend;
                    target.NextActionAt = time.GetUtcNow();
                }
                else if (target is { State: OutreachState.Messaged, FriendRequestSentAt: not null })
                {
                    // Đã nhận tin chào, chưa trả lời, giờ chấp nhận lời mời gửi sau tin chào → một tin cảm ơn + giới thiệu doanh nghiệp
                    // (chủ dự án chốt 2026-11-25). Đợi vài phút: job còn kiểm hội thoại yên mới gửi.
                    target.NextActionAt = time.GetUtcNow() + OutreachTenantJob.ThanksDelay;
                }
                else if (target is null
                         && await outreach.FindRepliedByUidAsync(route.TenantId, route.ConnectionId, e.Uid, cancellationToken) is { FriendRequestSentAt: not null } replied)
                {
                    // Đã nói chuyện rồi mới chấp nhận lời mời của bên mình → một câu cảm ơn + giới thiệu sơ (chủ dự án chốt 2026-11-15).
                    // Đợi vài phút: khách có thể đang chat dở, job còn kiểm hội thoại yên mới gửi.
                    replied.NextActionAt = time.GetUtcNow() + OutreachTenantJob.ThanksDelay;
                }

                break;
            case "remove":
                peer.IsFriend = false;
                break;
            case "block" or "reject_request":
                // Khách chặn / từ chối kết bạn → không bao giờ chủ động liên hệ lại (docs/ZALO-PERSONAL-PLAN.md mục 6.7).
                if (peer.ContactId is { } contactId
                    && await conversations.GetContactAsync(route.TenantId, contactId, cancellationToken) is { ProactiveOptOutAt: null } contact)
                {
                    contact.ProactiveOptOutAt = time.GetUtcNow();
                    contact.ProactiveOptOutSource = e.Event == "block" ? "zalo_block" : "zalo_reject";
                }

                if (target is not null)
                {
                    target.State = OutreachState.Declined;
                    target.NextActionAt = null;
                }

                break;
        }

        await TrySaveAsync(cancellationToken);
    }

    /// <summary>Sidecar báo tình trạng nick (CONTRACT.md mục 4, bảng state).</summary>
    private async Task ApplyStateAsync(PersonalRoute route, PersonalEvent e, CancellationToken cancellationToken)
    {
        var account = await personal.GetAccountAsync(route.TenantId, route.ConnectionId, cancellationToken);
        var connection = await connections.GetAsync(route.TenantId, route.ConnectionId, cancellationToken);
        if (account is null || connection is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var (listener, status, error) = e.State switch
        {
            "connected" => (ListenerState.Connected, (ConnectionStatus?)null, (string?)null),
            "reconnecting" => (ListenerState.Reconnecting, null, null),
            "opened_elsewhere" => (ListenerState.OpenedElsewhere, ConnectionStatus.Disconnected, "zp:opened_elsewhere"),
            "session_expired" => (ListenerState.SessionExpired, ConnectionStatus.NeedsReauth, "zp:session_expired"),
            "proxy_failing" => (ListenerState.ProxyFailing, ConnectionStatus.Disconnected, "zp:proxy_failing"),
            "stopped" => (ListenerState.Stopped, null, null),
            _ => (account.ListenerState, null, null),
        };

        account.ListenerState = listener;
        account.ListenerStateChangedAt = now;
        account.LastStateCode = e.Code;
        account.LastSeenAt = now;

        var alert = false;
        if (status is { } s && connection.Status != s)
        {
            alert = connection.Status == ConnectionStatus.Active;
            connection.Status = s;
            connection.LastError = error;
        }
        else if (listener == ListenerState.Connected && connection.Status == ConnectionStatus.Active)
        {
            connection.LastError = null;
        }

        await connections.SaveChangesAsync(cancellationToken);
        await notifier.PublishAsync(route.TenantId, new InboxEvent("channel", route.ConnectionId), cancellationToken);
        if (alert && error is not null)
        {
            var tenantId = route.TenantId;
            var connectionId = route.ConnectionId;
            var reason = error;
            jobs.Enqueue<ZaloPersonalAlertJob>(job => job.RunAsync(tenantId, connectionId, reason, CancellationToken.None));
            LogStateAlert(logger, connectionId, tenantId, reason);
        }
    }

    private async Task<Conversation> GetConversationAsync(PersonalRoute route, Contact contact, CancellationToken cancellationToken) =>
        await conversations.FindOpenConversationAsync(route.TenantId, contact.Id, route.ConnectionId, cancellationToken)
        ?? conversations.AddConversation(route.TenantId, new Conversation
        {
            ContactId = contact.Id,
            Channel = ChannelKind.ZaloPersonal,
            ConnectionId = route.ConnectionId,
        });

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await conversations.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private static string ContentOf(PersonalEvent e)
    {
        if (e.Kind == "text" && !string.IsNullOrWhiteSpace(e.Text))
        {
            return Clip(e.Text);
        }

        return InboundText.ForAttachment(e.Kind ?? "file") ?? InboundText.ForAttachment("file")!;
    }

    private static string Clip(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 4000 ? trimmed[..4000] : trimmed;
    }

    private static string? Short(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > 200 ? value.Trim()[..200] : value.Trim();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sự kiện Zalo cá nhân bỏ qua: {Type}")]
    private static partial void LogIgnored(ILogger logger, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nick Zalo cá nhân {ConnectionId} của tenant {TenantId} cần chủ DN xử lý ({Reason})")]
    private static partial void LogStateAlert(ILogger logger, Guid connectionId, Guid tenantId, string reason);
}
