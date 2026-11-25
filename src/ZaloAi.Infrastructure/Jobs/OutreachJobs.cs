using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Mỗi phút: tenant nào có người tới lượt chăm sóc qua Zalo cá nhân → xếp job xử lý cho tenant đó.</summary>
[Queue(JobQueues.Default)]
public sealed class OutreachSweepJob(OutreachLookup lookup, IBackgroundJobClient jobs, TimeProvider time)
{
    public const string RecurringId = "zalo-personal-outreach";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await lookup.TenantsWithDueAsync(time.GetUtcNow(), cancellationToken))
        {
            jobs.Enqueue<OutreachTenantJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }
    }
}

/// <summary>
/// Đi hàng đợi chăm sóc của một tenant (docs/ZALO-PERSONAL-PLAN.md mục 6.6–6.9, chủ dự án chốt lại 2026-11-25): tìm Zalo theo SĐT →
/// NHẮN TRƯỚC (lời chào AI viết) → 2–5 phút sau MỜI KẾT BẠN luôn (câu ngắn, lượt mời của người mới; hết lượt thì hôm sau / khi khách trả
/// lời) → khách chấp nhận mà chưa trả lời: một tin cảm ơn + giới thiệu doanh nghiệp + câu hỏi mở; khách trả lời trước khi được mời: mời
/// sau vài phút; đã chat rồi mới chấp nhận: một câu cảm ơn. Nhắn trước bị chặn tin người lạ → kết bạn kèm lời chào, chấp nhận rồi mới nhắn.
/// Lời chào do AI viết riêng từng người (<see cref="IOutreachWriter"/>), sai luật thì dùng mẫu <see cref="OutreachGreeting"/>.
/// Luôn trong khung giờ được nhắn, trong giới hạn từng nick (tra SĐT, người mới, lời mời — giữ ~1/3 cho người đã trả lời, khoảng cách
/// ≥ 10 phút giữa 2 lời mời), mỗi người chỉ được liên hệ chủ động một lần, người đã từ chối không bao giờ bị liên hệ lại.
/// </summary>
[Queue(JobQueues.Default)]
public sealed partial class OutreachTenantJob(
    TenantContext tenantContext,
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    ContactRepository contacts,
    ConversationRepository conversations,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    MessageDeliveryService delivery,
    ContactPhoneLinker phoneLinker,
    PersonalSendLimiter limiter,
    SidecarClient sidecar,
    IOutreachWriter writer,
    IDistributedStore store,
    IFieldEncryptor encryptor,
    OutreachQueueWatcher queueWatcher,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<OutreachTenantJob> logger)
{
    /// <summary>Khách trả lời tin chào → chờ chừng này rồi mới gửi lời mời kết bạn (không mời ngay giữa lúc đang trò chuyện).</summary>
    public static readonly TimeSpan InviteAfterReplyDelay = TimeSpan.FromMinutes(3);

    /// <summary>Khách đã chat rồi mới chấp nhận lời mời → chờ chừng này rồi gửi câu cảm ơn.</summary>
    public static readonly TimeSpan ThanksDelay = TimeSpan.FromMinutes(3);

    /// <summary>Hội thoại có tin trong chừng này → khách đang chat dở, dời câu cảm ơn.</summary>
    private static readonly TimeSpan ChatQuietFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Nhắn tin chào xong → chờ ngẫu nhiên trong khoảng này rồi mới gửi lời mời kết bạn (chủ dự án chốt 2026-11-25: "vài phút sau", giống
    /// người thật hơn gửi cùng lúc — tin + lời mời dồn liền nhau với người lạ là kiểu Zalo hay soi).
    /// </summary>
    public static readonly TimeSpan FollowUpInviteMin = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan FollowUpInviteMax = TimeSpan.FromMinutes(5);

    private const int PerRun = 15;

    private const string StrangerBlocked = "stranger_blocked";

    private const string InviteRejected = "invite_rejected";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        await using var running = await store.TryAcquireLockAsync($"zp:outreach:{tenantId:N}", TimeSpan.FromMinutes(5), cancellationToken);
        if (running is null)
        {
            return; // lượt trước còn chạy
        }

        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        if (!CareDecision.InSendWindow(settings, now))
        {
            return; // ngoài khung giờ được nhắn: tới giờ thì lượt sau tự chạy
        }

        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var tomorrow = CareDecision.NextSendTime(settings, StartOfNextDay(now));
        foreach (var target in await outreach.ListDueAsync(tenantId, now, PerRun, cancellationToken))
        {
            try
            {
                await ProcessAsync(tenant, target, tomorrow, cancellationToken);
            }
            catch (ChannelUnavailableException)
            {
                target.NextActionAt = time.GetUtcNow().AddMinutes(15); // sidecar / mạng tạm lỗi: thử lại sau
            }

            await outreach.SaveChangesAsync(cancellationToken);
        }

        await queueWatcher.CheckAsync(tenantId, cancellationToken); // vừa nhắn người cuối → báo "hết danh sách" ngay
    }

    private async Task ProcessAsync(Tenant tenant, OutreachTarget target, DateTimeOffset tomorrow, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Id;
        var now = time.GetUtcNow();
        var connection = target.ConnectionId is { } cid ? await connections.GetAsync(tenantId, cid, cancellationToken) : null;
        var account = connection is null ? null : await personal.GetAccountAsync(tenantId, connection.Id, cancellationToken);
        if (connection is null || account is null)
        {
            Finish(target, OutreachState.Failed, "no_nick");
            return;
        }

        if (connection.Status != ConnectionStatus.Active)
        {
            target.NextActionAt = now.AddMinutes(30); // nick đang mất kết nối: đợi chủ DN xử lý
            return;
        }

        var contact = await contacts.GetAsync(tenantId, target.ContactId, cancellationToken);
        if (contact is null)
        {
            Finish(target, OutreachState.Failed, "no_contact");
            return;
        }

        if (contact.ProactiveOptOutAt is not null)
        {
            Finish(target, OutreachState.Declined, null);
            return;
        }

        var limits = PersonalPacing.Effective(account, options.Value, now);

        // Khách đã trả lời: chưa là bạn → mời kết bạn (một lần, 2026-11-14); đã chấp nhận lời mời của bên mình → câu cảm ơn (2026-11-15).
        if (target.State == OutreachState.Replied)
        {
            await RepliedAsync(tenant, target, contact, connection, limits, tomorrow, cancellationToken);
            return;
        }

        // Đã nhận tin chào, chưa trả lời: tới giờ mời kết bạn, hoặc khách vừa chấp nhận lời mời → giới thiệu doanh nghiệp (2026-11-25).
        if (target.State == OutreachState.Messaged)
        {
            await MessagedAsync(tenant, target, contact, connection, limits, tomorrow, cancellationToken);
            return;
        }

        // Bước 1: tìm nick Zalo theo SĐT.
        if (target.ZaloUid is null)
        {
            if (!await limiter.HasRoomAsync(connection.Id, PacingCounter.FindUser, limits.FindUserPerDay, cancellationToken))
            {
                target.NextActionAt = tomorrow;
                return;
            }

            if (contact.PhoneEnc is null)
            {
                Finish(target, OutreachState.Failed, "no_phone");
                return;
            }

            var phone = encryptor.Decrypt(contact.PhoneEnc);
            await limiter.CountAsync(connection.Id, PacingCounter.FindUser, cancellationToken);
            var found = await sidecar.FindUserAsync(connection.Id, phone, cancellationToken);
            if (found is null)
            {
                Finish(target, OutreachState.NoZalo, null); // không có Zalo → nhân viên gọi điện
                return;
            }

            target.ZaloUid = found.Uid;
            contact = await LinkContactAsync(tenantId, target, contact, phone, found, cancellationToken);
            if (contact is null)
            {
                return; // người này đã đang chat với DN → dừng (target đã cập nhật)
            }

            var peer = await personal.GetPeerAsync(tenantId, connection.Id, found.Uid, cancellationToken);
            if (peer is { Kind: PeerKind.Private })
            {
                Finish(target, OutreachState.Stopped, "private"); // chủ nick đã đánh dấu người này là riêng tư
                return;
            }

            if (peer is null)
            {
                personal.AddPeer(tenantId, new ZaloPeer
                {
                    ConnectionId = connection.Id,
                    ExternalUserId = found.Uid,
                    DisplayName = found.DisplayName,
                    Kind = PeerKind.Customer,
                    ContactId = contact.Id,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
            else
            {
                peer.Kind = PeerKind.Customer;
                peer.ContactId ??= contact.Id;
            }
        }

        var batch = await outreach.GetBatchAsync(tenantId, target.BatchId, cancellationToken);
        var intro = new Intro(
            contact.Origin ?? batch?.Origin ?? "customer",
            batch?.ReferrerName,
            batch?.NoteEnc is null ? null : encryptor.Decrypt(batch.NoteEnc));

        // Bước 2: khách chấp nhận lời mời → tin đầu: cảm ơn + giới thiệu sơ + hỏi xin phép (lượt "người mới" đã tính lúc mời / nhắn).
        if (target.State == OutreachState.Friend)
        {
            if (!await SendFirstMessageAsync(tenant, target, contact, connection, intro, OutreachMessageKind.AcceptedGreeting, tomorrow, countNewContact: false, cancellationToken))
            {
                // Đã là bạn bè mà Zalo vẫn từ chối tin → dừng, không thử lại mỗi phút (gửi dồn bị từ chối là kiểu dễ bị khóa nick nhất).
                Finish(target, OutreachState.Failed, "send_rejected");
            }

            return;
        }

        // Dành lượt tin cho khách đang chat (Backpressure, 2026-11-25): nick đã dùng 70% lượt tin hôm nay → người MỚI đợi hôm sau, khách
        // đang hỏi luôn được trả lời. Người đã chấp nhận kết bạn (bước 2 ở trên) không bị giữ lại.
        if (!await limiter.HasRoomAsync(connection.Id, PacingCounter.Messages, OutreachLifecycle.NewOutreachMessageCap(limits.MessagesPerDay), cancellationToken))
        {
            target.NextActionAt = tomorrow;
            return;
        }

        // Bước 3 (chủ dự án chốt 2026-11-25): NHẮN TRƯỚC, rồi 2–5 phút sau mời kết bạn luôn (MessagedAsync).
        // Nhắn trước bị chặn tin người lạ → mời kết bạn kèm lời chào khi có lượt. Mỗi người chỉ được liên hệ chủ động một lần.
        if (target.LastError == StrangerBlocked)
        {
            await InviteAsync(tenant, target, contact, connection, limits, intro, tomorrow, InviteKind.Cold, countNewContact: false, cancellationToken);
            return;
        }

        if (!await limiter.HasRoomAsync(connection.Id, PacingCounter.NewContacts, limits.NewContactsPerDay, cancellationToken))
        {
            target.NextActionAt = tomorrow;
            return;
        }

        if (await SendFirstMessageAsync(tenant, target, contact, connection, intro, OutreachMessageKind.Greeting, tomorrow, countNewContact: true, cancellationToken))
        {
            var known = await personal.GetPeerAsync(tenantId, connection.Id, target.ZaloUid!, cancellationToken);
            if (target.State == OutreachState.Messaged && known is not { IsFriend: true })
            {
                var spread = (int)(FollowUpInviteMax - FollowUpInviteMin).TotalSeconds;
                target.NextActionAt = time.GetUtcNow() + FollowUpInviteMin + TimeSpan.FromSeconds(Random.Shared.Next(0, spread + 1));
            }

            return;
        }

        await InviteAsync(tenant, target, contact, connection, limits, intro, tomorrow, InviteKind.Cold, countNewContact: false, cancellationToken);
    }

    /// <summary>
    /// Lời mời cho người chưa trả lời (Cold: kết bạn kèm lời chào khi bị chặn tin người lạ; FollowUp: mời sau tin chào) chỉ dùng phần lượt
    /// mời không giữ cho người đã trả lời (AfterReply).
    /// </summary>
    private enum InviteKind
    {
        Cold,
        AfterReply,
        FollowUp,
    }

    /// <summary>Lý do liên hệ + ghi chú cho AI của đợt dán (bản gốc; bộ viết tự che trước khi gửi AI).</summary>
    private sealed record Intro(string Origin, string? Referrer, string? Note);

    /// <summary>
    /// Số lời mời/ngày người mới được dùng: giữ khoảng 1/3 (ít nhất 1) cho người ĐÃ trả lời — họ gần như chắc chấp nhận, không để người
    /// mới dùng hết (chủ dự án chốt 2026-11-15). Vd 3 → 2, 5 → 4, 10 → 7, 1 → 0.
    /// </summary>
    public static int ColdInviteCap(int invitesPerDay) => invitesPerDay <= 0 ? 0 : invitesPerDay - Math.Max(1, invitesPerDay / 3);

    private Task<bool> HasInviteRoomAsync(Guid connectionId, PacingLimits limits, InviteKind kind, CancellationToken cancellationToken) =>
        limiter.HasRoomAsync(connectionId, PacingCounter.Invites, kind == InviteKind.AfterReply ? limits.InvitesPerDay : ColdInviteCap(limits.InvitesPerDay), cancellationToken);

    /// <summary>
    /// Người đã nhận tin chào, chưa trả lời (chủ dự án chốt 2026-11-25): chưa là bạn → gửi lời mời kết bạn MỘT lần (câu ngắn, hết lượt thì
    /// hôm sau); khách vừa chấp nhận lời mời của bên mình → đúng MỘT tin cảm ơn + giới thiệu doanh nghiệp + câu hỏi mở (chủ động tìm khách),
    /// chỉ khi hội thoại đang yên và bot đang tự lo.
    /// </summary>
    private async Task MessagedAsync(
        Tenant tenant, OutreachTarget target, Contact contact, ChannelConnection connection, PacingLimits limits, DateTimeOffset tomorrow, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Id;
        var now = time.GetUtcNow();
        var peer = target.ZaloUid is null ? null : await personal.GetPeerAsync(tenantId, connection.Id, target.ZaloUid, cancellationToken);
        if (peer is null or { Kind: PeerKind.Private })
        {
            target.NextActionAt = null;
            return;
        }

        if (!peer.IsFriend)
        {
            if (target.FriendRequestSentAt is not null || target.LastError == InviteRejected)
            {
                target.NextActionAt = null; // đã mời (chờ khách chấp nhận) hoặc Zalo đã từ chối lời mời: không mời lại
                return;
            }

            await InviteAsync(tenant, target, contact, connection, limits, new Intro("customer", null, null), tomorrow, InviteKind.FollowUp, countNewContact: false, cancellationToken);
            return;
        }

        // Đã là bạn: chỉ giới thiệu khi CHÍNH bên mình đã mời (vốn là bạn / khách tự kết bạn thì không có gì để cảm ơn).
        var conversation = target.FriendRequestSentAt is null
            ? null
            : await conversations.FindOpenConversationAsync(tenantId, contact.Id, connection.Id, cancellationToken);
        if (conversation is null || conversation.Mode == ConversationMode.Human || conversation.BotPausedUntil > now)
        {
            target.NextActionAt = null; // nhân viên / chủ nick đang lo thì bot không chen vào
            return;
        }

        if (conversation.LastMessageAt > now - ChatQuietFor)
        {
            target.NextActionAt = now + ThanksDelay; // vừa có tin (tin chào / khách đang gõ) → đợi yên rồi mới giới thiệu
            return;
        }

        if (!await limiter.HasRoomAsync(connection.Id, PacingCounter.Messages, limits.MessagesPerDay, cancellationToken))
        {
            target.NextActionAt = tomorrow;
            return;
        }

        // Đúng một lần cho mỗi người (khách xóa rồi kết bạn lại cũng không nhận lần hai).
        target.NextActionAt = null;
        if (!await store.SetIfNotExistsAsync($"zp:accepted-intro:{target.Id:N}", "1", TimeSpan.FromDays(180), cancellationToken))
        {
            return;
        }

        var recent = (await conversations.ListMessagesAsync(tenantId, conversation.Id, null, cancellationToken))
            .Where(m => m.Sender != MessageSender.System && m.DeliveryStatus != DeliveryStatus.Skipped)
            .TakeLast(6)
            .Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc)))
            .ToList();
        var texts = await ComposeAsync(
            tenant, contact, OutreachMessageKind.AcceptedIntro, new Intro(contact.Origin ?? "customer", null, null), recent, conversation.Id, conversation.ConnectionId, cancellationToken);
        var messages = AddProactive(tenantId, conversation, texts);
        await conversations.SaveChangesAsync(cancellationToken);
        await DeliverAllAsync(tenantId, conversation, contact, messages, cancellationToken);
    }

    /// <summary>
    /// Người đã trả lời: chưa là bạn → mời kết bạn một lần (đợi vài phút sau tin trả lời); đã chấp nhận lời mời của bên mình (sự kiện
    /// friend add hẹn giờ) → câu cảm ơn + giới thiệu sơ bên mình giúp được gì, chỉ khi hội thoại đang yên và bot đang tự lo.
    /// </summary>
    private async Task RepliedAsync(
        Tenant tenant, OutreachTarget target, Contact contact, ChannelConnection connection, PacingLimits limits, DateTimeOffset tomorrow, CancellationToken cancellationToken)
    {
        var tenantId = tenant.Id;
        var now = time.GetUtcNow();
        var peer = target.ZaloUid is null ? null : await personal.GetPeerAsync(tenantId, connection.Id, target.ZaloUid, cancellationToken);
        if (peer is null or { Kind: PeerKind.Private })
        {
            target.NextActionAt = null;
            return;
        }

        if (!peer.IsFriend)
        {
            if (target.FriendRequestSentAt is not null)
            {
                target.NextActionAt = null; // mỗi người chỉ mời một lần
                return;
            }

            await InviteAsync(tenant, target, contact, connection, limits, new Intro("customer", null, null), tomorrow, InviteKind.AfterReply, countNewContact: false, cancellationToken);
            return;
        }

        // Đã là bạn: chỉ cảm ơn khi CHÍNH bên mình đã mời (khách tự kết bạn thì không có gì để cảm ơn).
        var conversation = target.FriendRequestSentAt is null
            ? null
            : await conversations.FindOpenConversationAsync(tenantId, contact.Id, connection.Id, cancellationToken);
        if (conversation is null || conversation.Mode == ConversationMode.Human || conversation.BotPausedUntil > now)
        {
            target.NextActionAt = null; // nhân viên / chủ nick đang lo thì bot không chen vào
            return;
        }

        if (conversation.LastMessageAt > now - ChatQuietFor)
        {
            target.NextActionAt = now + ThanksDelay; // khách đang chat dở → đợi yên rồi mới cảm ơn
            return;
        }

        if (!await limiter.HasRoomAsync(connection.Id, PacingCounter.Messages, limits.MessagesPerDay, cancellationToken))
        {
            target.NextActionAt = tomorrow;
            return;
        }

        var recent = (await conversations.ListMessagesAsync(tenantId, conversation.Id, null, cancellationToken))
            .Where(m => m.Sender != MessageSender.System && m.DeliveryStatus != DeliveryStatus.Skipped)
            .TakeLast(6)
            .Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc)))
            .ToList();
        var texts = await ComposeAsync(tenant, contact, OutreachMessageKind.AcceptedThanks, new Intro("customer", null, null), recent, conversation.Id, conversation.ConnectionId, cancellationToken);
        target.NextActionAt = null; // gửi đúng một lần: lưu cùng tin trước khi gửi
        var messages = AddProactive(tenantId, conversation, texts);
        await conversations.SaveChangesAsync(cancellationToken);
        await DeliverAllAsync(tenantId, conversation, contact, messages, cancellationToken);
    }

    /// <summary>
    /// AI viết tin cho đúng người (lý do liên hệ, ghi chú, nhóm dịch vụ của DN); AI lỗi hoặc viết sai luật → mẫu soạn sẵn, chọn ngẫu nhiên
    /// một biến thể. Ghi chi phí AI theo tenant.
    /// </summary>
    private async Task<IReadOnlyList<string>> ComposeAsync(
        Tenant tenant, Contact contact, OutreachMessageKind kind, Intro intro, IReadOnlyList<BotHistoryTurn> recent, Guid? conversationId, Guid? connectionId, CancellationToken cancellationToken)
    {
        var offerings = await outreach.OfferingsAsync(tenant.Id, 5, cancellationToken);
        var result = await writer.WriteAsync(
            new OutreachMessageRequest(
                tenant.Id,
                new BotProfile(tenant.Name, tenant.IndustrySlug, tenant.BotName, tenant.BotPronoun, tenant.BotTone, tenant.BotInstructions, tenant.PrivacyUrl),
                kind, contact.DisplayName, contact.Salutation, intro.Origin, intro.Referrer, intro.Note, offerings, recent),
            cancellationToken);
        foreach (var u in result.Usage)
        {
            conversations.AddUsage(tenant.Id, new UsageRecord
            {
                Kind = u.Kind,
                Provider = u.Provider,
                Model = u.Model,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CostUsd = u.CostUsd,
                ConversationId = conversationId,
                ConnectionId = connectionId,
            });
        }

        if (result.Messages.Count > 0)
        {
            return result.Messages;
        }

        LogTemplateFallback(logger, kind);
        return kind switch
        {
            OutreachMessageKind.Invite =>
                [OutreachGreeting.Invite(tenant.BotPronoun, tenant.Name, contact.Salutation, contact.DisplayName, intro.Origin, intro.Referrer)],
            OutreachMessageKind.AcceptedGreeting =>
                [OutreachGreeting.AcceptedGreeting(tenant.BotName, tenant.BotPronoun, tenant.Name, contact.Salutation, contact.DisplayName, offerings,
                    Random.Shared.Next(OutreachGreeting.AcceptedVariants))],
            OutreachMessageKind.AcceptedThanks =>
                [OutreachGreeting.AcceptedThanks(tenant.BotPronoun, contact.Salutation, contact.DisplayName, offerings, Random.Shared.Next(OutreachGreeting.AcceptedVariants))],
            OutreachMessageKind.AcceptedIntro =>
                [OutreachGreeting.AcceptedIntro(tenant.BotPronoun, tenant.Name, contact.Salutation, contact.DisplayName, offerings, Random.Shared.Next(OutreachGreeting.AcceptedVariants))],
            _ =>
                [OutreachGreeting.FirstMessage(tenant.BotName, tenant.BotPronoun, tenant.Name, contact.Salutation, contact.DisplayName, intro.Origin, intro.Referrer,
                    Random.Shared.Next(OutreachGreeting.FirstMessageVariants))],
        };
    }

    private List<Message> AddProactive(Guid tenantId, Conversation conversation, IReadOnlyList<string> texts)
    {
        var now = time.GetUtcNow();
        var messages = new List<Message>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.Bot, texts[i]);
            message.Proactive = true;
            message.CreatedAt = now.AddMilliseconds(i); // thứ tự hiển thị/gửi lại luôn đúng
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Gửi lần lượt; đoạn đầu không đi được thì các đoạn sau thôi (không để khách nhận nửa lời chào).</summary>
    private async Task DeliverAllAsync(Guid tenantId, Conversation conversation, Contact contact, List<Message> messages, CancellationToken cancellationToken)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            await delivery.DeliverAsync(tenantId, conversation, contact, messages[i], cancellationToken);
            if (messages[i].DeliveryStatus != DeliveryStatus.Sent)
            {
                foreach (var rest in messages.Skip(i + 1))
                {
                    rest.DeliveryStatus = DeliveryStatus.Failed;
                    rest.DeliveryError = "zp:first_part_failed";
                }

                await conversations.SaveChangesAsync(cancellationToken);
                return;
            }
        }
    }

    /// <param name="kind">Greeting (nhắn trước) hoặc AcceptedGreeting (khách vừa chấp nhận lời mời).</param>
    /// <returns>true = đã xử lý xong lượt này (gửi được / chờ thử lại); false = khách chặn tin người lạ → mời kết bạn.</returns>
    private async Task<bool> SendFirstMessageAsync(
        Tenant tenant,
        OutreachTarget target,
        Contact contact,
        ChannelConnection connection,
        Intro intro,
        OutreachMessageKind kind,
        DateTimeOffset tomorrow,
        bool countNewContact,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.Id;
        var conversation = await conversations.FindOpenConversationAsync(tenantId, contact.Id, connection.Id, cancellationToken)
            ?? conversations.AddConversation(tenantId, new Conversation { ContactId = contact.Id, Channel = ChannelKind.ZaloPersonal, ConnectionId = connection.Id });
        var texts = await ComposeAsync(tenant, contact, kind, intro, [], conversation.Id, connection.Id, cancellationToken);
        var messages = AddProactive(tenantId, conversation, texts);
        var message = messages[0];
        await conversations.SaveChangesAsync(cancellationToken);
        if (countNewContact)
        {
            await limiter.CountAsync(connection.Id, PacingCounter.NewContacts, cancellationToken);
        }

        try
        {
            await DeliverAllAsync(tenantId, conversation, contact, messages, cancellationToken);
        }
        catch (ChannelUnavailableException)
        {
            foreach (var m in messages.Where(m => m.DeliveryStatus == DeliveryStatus.Pending))
            {
                m.DeliveryStatus = DeliveryStatus.Failed;
                m.DeliveryError = "zp:unavailable";
            }

            if (message.DeliveryStatus != DeliveryStatus.Sent)
            {
                target.NextActionAt = time.GetUtcNow().AddMinutes(15);
                return true;
            }
        }

        var now = time.GetUtcNow();
        if (message.DeliveryStatus == DeliveryStatus.Sent)
        {
            target.State = OutreachState.Messaged;
            target.FirstMessageAt = now;
            target.LastError = null;
            target.NextActionAt = null; // job dọn hằng giờ: 2 ngày không trả lời → danh sách "Cần gọi điện"
            contact.LastProactiveAt = now;
            contact.ProactiveAwaitingReply = true;
            return true;
        }

        switch (message.DeliveryError)
        {
            case "zp:disconnected":
                target.NextActionAt = now.AddMinutes(30);
                return true;
            case "zp:send_limit":
                target.NextActionAt = tomorrow;
                return true;
            default:
                // Zalo từ chối tin đầu: thường là khách chỉ nhận tin từ bạn bè → chuyển sang mời kết bạn.
                target.LastError = StrangerBlocked;
                return false;
        }
    }

    /// <param name="kind">
    /// Cold = bị chặn tin người lạ (kết bạn kèm lời chào): AI viết lời chào, chỉ dùng phần lượt mời cho người mới.
    /// FollowUp = đã nhận tin chào, chưa trả lời: câu ngắn theo mẫu, lượt mời cho người mới, giữ trạng thái Messaged.
    /// AfterReply = khách đã trả lời tin chào: câu ngắn theo mẫu, dùng cả lượt giữ riêng, giữ trạng thái Replied.
    /// </param>
    /// <param name="countNewContact">Lời mời là lần chạm ĐẦU TIÊN với người này → tính vào "người mới hôm nay".</param>
    private async Task InviteAsync(
        Tenant tenant,
        OutreachTarget target,
        Contact contact,
        ChannelConnection connection,
        PacingLimits limits,
        Intro intro,
        DateTimeOffset tomorrow,
        InviteKind kind,
        bool countNewContact,
        CancellationToken cancellationToken)
    {
        var shortNote = kind is InviteKind.AfterReply or InviteKind.FollowUp; // khách đã nhận tin của bên mình → không cần chào lại
        if (!await HasInviteRoomAsync(connection.Id, limits, kind, cancellationToken))
        {
            target.NextActionAt = tomorrow;
            return;
        }

        // Hai lời mời cách nhau ít nhất 10 phút (+ ngẫu nhiên 0–10 phút) — gửi dồn là kiểu hành vi dễ bị hạn chế nhất.
        var gap = TimeSpan.FromMinutes(10 + Random.Shared.Next(0, 11));
        if (!await store.SetIfNotExistsAsync($"zp:invite-gap:{connection.Id:N}", "1", gap, cancellationToken))
        {
            target.NextActionAt = time.GetUtcNow().AddMinutes(5);
            return;
        }

        var greeting = shortNote
            ? OutreachGreeting.InviteAfterReply(tenant.BotName, tenant.BotPronoun, tenant.Name, contact.Salutation, contact.DisplayName)
            : (await ComposeAsync(tenant, contact, OutreachMessageKind.Invite, intro, [], null, connection.Id, cancellationToken))[0];
        var sent = await sidecar.SendFriendRequestAsync(connection.Id, target.ZaloUid!, greeting, cancellationToken);
        await limiter.CountAsync(connection.Id, PacingCounter.Invites, cancellationToken);
        if (countNewContact)
        {
            await limiter.CountAsync(connection.Id, PacingCounter.NewContacts, cancellationToken);
        }
        target.NextActionAt = null; // chờ khách chấp nhận (sự kiện friend add) — 14 ngày không chấp nhận thì job dọn rút lời mời
        if (!sent)
        {
            // Zalo từ chối lời mời (đã là bạn, đã mời, chặn lời mời...) → không thử mời lại (gửi dồn bị từ chối dễ bị khóa nick).
            if (shortNote)
            {
                target.LastError = InviteRejected; // tin chào đã tới khách: giữ nguyên, chỉ không mời nữa
            }
            else if (target.LastError == StrangerBlocked)
            {
                Finish(target, OutreachState.Failed, InviteRejected); // nhắn thẳng lẫn mời đều không được
            }
            else
            {
                // Kết bạn trước không được → lượt sau nhắn trước (lời mời bị từ chối chưa tới người nhận, vẫn là lần chạm đầu).
                target.LastError = InviteRejected;
                target.NextActionAt = time.GetUtcNow();
            }

            return;
        }

        target.FriendRequestSentAt = time.GetUtcNow();
        if (kind == InviteKind.Cold)
        {
            target.State = OutreachState.Invited; // chưa nhắn được gì: chờ chấp nhận rồi mới gửi tin đầu
        }

        LogInvited(logger, target.Id);
    }

    /// <summary>
    /// Gắn hồ sơ "chỉ có SĐT" với nick Zalo vừa tìm thấy. Chưa có hồ sơ Zalo cá nhân cho uid này → đổi tại chỗ (giữ ghi chú, nguồn, người
    /// giới thiệu). Đã có (người này từng nhắn DN qua nick) → gộp bản SĐT vào hồ sơ đó và dừng chăm sóc (đã đang chat). null = đã dừng.
    /// </summary>
    private async Task<Contact?> LinkContactAsync(
        Guid tenantId, OutreachTarget target, Contact contact, string phone, SidecarFoundUser found, CancellationToken cancellationToken)
    {
        var existing = await contacts.FindByExternalIdAsync(tenantId, ChannelKind.ZaloPersonal, found.Uid, cancellationToken);
        if (existing is null || existing.Id == contact.Id)
        {
            contact.Channel = ChannelKind.ZaloPersonal;
            contact.ExternalUserId = found.Uid;
            contact.DisplayName ??= found.DisplayName;
            return contact;
        }

        target.ContactId = existing.Id; // trỏ sang hồ sơ đang chat TRƯỚC khi gộp (gộp xóa bản SĐT)
        await outreach.SaveChangesAsync(cancellationToken);
        await phoneLinker.LinkAsync(tenantId, existing, phone, cancellationToken);
        Finish(target, OutreachState.Stopped, "already_chatting");
        return null;
    }

    private static void Finish(OutreachTarget target, OutreachState state, string? error)
    {
        target.State = state;
        target.LastError = error;
        target.NextActionAt = null;
    }

    private static DateTimeOffset StartOfNextDay(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(7));
        return new DateTimeOffset(local.Date.AddDays(1), TimeSpan.FromHours(7)).ToUniversalTime();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Đã gửi lời mời kết bạn cho người trong hàng đợi {TargetId}")]
    private static partial void LogInvited(ILogger logger, Guid targetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tin chủ động ({Kind}) dùng mẫu soạn sẵn (AI lỗi hoặc viết sai luật)")]
    private static partial void LogTemplateFallback(ILogger logger, OutreachMessageKind kind);
}

/// <summary>Mỗi giờ: dọn hàng đợi và tính phanh tự động cho mọi tenant đang chăm sóc qua Zalo cá nhân.</summary>
[Queue(JobQueues.Default)]
public sealed class OutreachMaintenanceSweepJob(OutreachLookup lookup, IBackgroundJobClient jobs)
{
    public const string RecurringId = "zalo-personal-outreach-maintenance";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await lookup.TenantsWithOutreachAsync(cancellationToken))
        {
            jobs.Enqueue<OutreachMaintenanceJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }
    }
}

/// <summary>
/// Dọn hàng đợi + phanh tự động của một tenant (docs/ZALO-PERSONAL-PLAN.md mục 6.7, 7.1) — chuyển trạng thái theo thời gian của vòng
/// đời số điện thoại (<see cref="OutreachLifecycle"/>, chủ dự án chốt 2026-11-25): khách nói "hủy" → từ chối; có lịch hẹn / hóa đơn mới →
/// đã thành khách; lời mời 14 ngày không chấp nhận → rút lời mời; im 2 ngày sau tin cuối bên mình → cần gọi điện; đang tư vấn mà im
/// 14 ngày → đã nguội; nằm trong danh sách gọi 30 ngày không ai xử lý → lưu trữ. Mỗi ngày báo Telegram số người cần gọi (một lần, lúc mở
/// khung giờ nhắn). Phanh: chỉ GIẢM tốc khi khách phản hồi xấu.
/// </summary>
[Queue(JobQueues.Default)]
public sealed partial class OutreachMaintenanceJob(
    TenantContext tenantContext,
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    SidecarClient sidecar,
    IBackgroundJobClient jobs,
    OutreachQueueWatcher queueWatcher,
    CallListDigest callDigest,
    TimeProvider time,
    ILogger<OutreachMaintenanceJob> logger)
{
    /// <summary>Ngưỡng phanh (điểm xuất phát, chỉ chỉnh theo hướng an toàn hơn — docs/ZALO-PERSONAL-PLAN.md mục 7.1).</summary>
    public const double SlowNegativeRate = 0.15;

    public const double PauseNegativeRate = 0.30;

    public const double SlowAcceptRate = 0.20;

    public const int MinSample = 20;

    public static readonly TimeSpan PauseFor = TimeSpan.FromHours(72);

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var now = time.GetUtcNow();

        foreach (var target in await outreach.ListOptedOutActiveAsync(tenantId, cancellationToken))
        {
            target.State = OutreachState.Declined;
            target.NextActionAt = null;
        }

        await outreach.SaveChangesAsync(cancellationToken); // lưu trước: bước dưới đọc lại từ DB, không được ghi đè người đã từ chối

        // Thành khách trước các bước theo thời gian: người vừa đặt lịch không bị đẩy sang "Cần gọi điện".
        foreach (var target in await outreach.ListConvertedAsync(tenantId, cancellationToken))
        {
            target.State = OutreachState.Converted;
            target.NextActionAt = null;
        }

        await outreach.SaveChangesAsync(cancellationToken);

        foreach (var target in await outreach.ListStaleInvitesAsync(tenantId, now.AddDays(-14), cancellationToken))
        {
            target.State = OutreachState.Expired;
            target.NextActionAt = null;
            if (target.ConnectionId is { } cid && target.ZaloUid is { } uid)
            {
                try
                {
                    await sidecar.UndoFriendRequestAsync(cid, uid, cancellationToken);
                }
                catch (ChannelUnavailableException)
                {
                    // Không rút được lúc này thì lời mời vẫn hết hạn phía Remi; Zalo tự giữ lời mời, không gửi lại.
                }
            }
        }

        foreach (var target in await outreach.ListSilentAsync(tenantId, now, cancellationToken))
        {
            target.State = OutreachState.NoReply; // "Cần gọi điện": bot thôi nhắn, nhân viên gọi
            target.NextActionAt = null;
        }

        foreach (var target in await outreach.ListGoneColdAsync(tenantId, now, cancellationToken))
        {
            target.State = OutreachState.Cold; // "Đã nguội": bot thôi nhắc, đứng đầu danh sách gọi
            target.NextActionAt = null;
        }

        await outreach.SaveChangesAsync(cancellationToken);

        foreach (var target in await outreach.ListToArchiveAsync(tenantId, now, cancellationToken))
        {
            target.State = OutreachState.Archived;
            target.NextActionAt = null;
        }

        await outreach.SaveChangesAsync(cancellationToken);

        foreach (var account in await personal.ListAccountsAsync(tenantId, cancellationToken))
        {
            await BrakeAsync(tenantId, account.ConnectionId, now, cancellationToken);
        }

        await queueWatcher.CheckAsync(tenantId, cancellationToken); // danh sách SĐT sắp hết / đã hết → Telegram
        await callDigest.CheckAsync(tenantId, cancellationToken); // mỗi ngày một tin: hôm nay có N số cần gọi
    }

    /// <summary>Tính mức phanh cho một nick từ số liệu 7 ngày.</summary>
    public static (OutreachLevel Level, string? Reason) Decide(int invites, int accepted, int firstMessages, int negatives)
    {
        var sent = invites + firstMessages;
        var negativeRate = sent >= MinSample ? negatives / (double)sent : 0;
        var acceptRate = invites >= MinSample ? accepted / (double)invites : 1;
        return negativeRate >= PauseNegativeRate ? (OutreachLevel.Paused, "negative_rate")
            : negativeRate >= SlowNegativeRate ? (OutreachLevel.Slow, "negative_rate")
            : acceptRate < SlowAcceptRate ? (OutreachLevel.Slow, "accept_rate")
            : (OutreachLevel.Normal, null);
    }

    private async Task BrakeAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = await personal.GetAccountAsync(tenantId, connectionId, cancellationToken);
        if (account is null || (account.PacingMode == PacingMode.Custom && !account.AutoBrake))
        {
            return;
        }

        if (account.OutreachLevel == OutreachLevel.Paused && account.OutreachPausedUntil > now)
        {
            return; // đang dừng 72 giờ
        }

        var (invites, accepted, firstMessages, negatives) = await outreach.RatesAsync(tenantId, connectionId, now.AddDays(-7), cancellationToken);
        var (level, reason) = Decide(invites, accepted, firstMessages, negatives);
        if (level == account.OutreachLevel)
        {
            return;
        }

        var worse = level > account.OutreachLevel;
        account.OutreachLevel = level;
        account.OutreachLevelReason = reason;
        account.OutreachLevelChangedAt = now;
        account.OutreachPausedUntil = level == OutreachLevel.Paused ? now + PauseFor : null;
        await personal.SaveChangesAsync(cancellationToken);
        LogLevelChanged(logger, connectionId, tenantId, level.ToString());
        if (await connections.GetAsync(tenantId, connectionId, cancellationToken) is not null)
        {
            // Xấu đi: chậm lại / tạm dừng. Đỡ hơn: hết tạm dừng nhưng còn chậm, hoặc chạy lại bình thường (2026-11-17).
            var code = level switch
            {
                OutreachLevel.Paused => "zp:outreach_paused",
                OutreachLevel.Slow => "zp:outreach_slow",
                _ => worse ? null : "zp:outreach_normal",
            };
            if (code is not null)
            {
                jobs.Enqueue<ZaloPersonalAlertJob>(job => job.RunAsync(tenantId, connectionId, code, CancellationToken.None));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phanh tự động nick {ConnectionId} (tenant {TenantId}) đổi sang {Level}")]
    private static partial void LogLevelChanged(ILogger logger, Guid connectionId, Guid tenantId, string level);
}
