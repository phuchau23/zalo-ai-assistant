using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Billing;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Xử lý một tin khách gửi vào (mọi kênh dùng chung: webchat, Zalo ở M4). Luồng: CLAUDE.md mục 3.
/// Idempotent: tin đã có câu trả lời (reply_to_message_id, unique) → bỏ qua; chạy lại không gửi trùng.
/// Hội thoại đang do nhân viên xử lý → bot không chen vào. Khách nhắn liên tiếp → chỉ trả lời tin cuối (đã thấy các tin trước).
/// Nội dung tin chỉ nằm trong bộ nhớ khi xử lý, lưu database dạng mã hóa; không ghi log nội dung.
/// </summary>
[Queue(JobQueues.Messages)]
public sealed partial class ProcessIncomingMessageJob(
    TenantContext tenantContext,
    ConversationRepository conversations,
    ContactRepository contacts,
    TenantRepository tenants,
    ChannelConnectionRepository connectionRepository,
    IBotEngine bot,
    MessageDeliveryService delivery,
    InboxService inbox,
    ConversationQuotaService quota,
    ContactPhoneLinker phoneLinker,
    IFieldEncryptor encryptor,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<ProcessIncomingMessageJob> logger)
{
    /// <summary>Số tin chưa tóm tắt vượt (cửa sổ lịch sử + mức này) thì tóm tắt phần cũ.</summary>
    private const int SummarizeSlack = 6;

    public const string OptOutReply =
        "Dạ em đã ghi nhận, bên em sẽ không chủ động nhắn tin cho mình nữa ạ. Khi cần, mình cứ nhắn em nhé. (Nếu ý mình là hủy lịch hẹn, mình nhắn rõ giúp em để nhân viên hỗ trợ ạ.)";

    public async Task RunAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);

        var message = await conversations.GetMessageAsync(tenantId, messageId, cancellationToken);
        if (message is null || message.Sender != MessageSender.Customer)
        {
            return;
        }

        var conversation = await conversations.GetConversationAsync(tenantId, message.ConversationId, cancellationToken);
        var contact = conversation is null ? null : await conversations.GetContactAsync(tenantId, conversation.ContactId, cancellationToken);
        if (conversation is null || contact is null)
        {
            return;
        }

        await TrackCustomerActivityAsync(tenantId, contact, message, cancellationToken);

        if (await conversations.FindReplyAsync(tenantId, messageId, cancellationToken) is not null)
        {
            // Đã soạn ở lần chạy trước: gửi nốt các tin ra còn pending (câu trả lời bot, câu chuyển tiếp) — không gọi AI lần 2.
            foreach (var pending in await conversations.ListPendingOutgoingAsync(tenantId, conversation.Id, cancellationToken))
            {
                await delivery.DeliverAsync(tenantId, conversation, contact, pending, cancellationToken);
            }

            return;
        }

        if (conversation.Mode == ConversationMode.Human || conversation.Status == ConversationStatus.Closed)
        {
            return;
        }

        // Kênh Zalo đã ngắt/mất quyền: không gọi AI (tốn tiền) cho tin không gửi được.
        if (conversation.Channel == ChannelKind.Zalo
            && (conversation.ConnectionId is not { } connectionId
                || await connectionRepository.GetAsync(tenantId, connectionId, cancellationToken) is not { Status: ConnectionStatus.Active }))
        {
            return;
        }

        if (await conversations.LatestCustomerMessageIdAsync(tenantId, conversation.Id, cancellationToken) != messageId)
        {
            return; // có tin mới hơn, job của tin đó sẽ trả lời gộp
        }

        // Tin chỉ có lệnh từ chối ("hủy", "dừng"...): xác nhận cố định, không gọi AI. Nhắc khách nói rõ nếu ý là hủy lịch hẹn.
        if (CareDecision.IsOptOutCommand(encryptor.Decrypt(message.ContentEnc)))
        {
            var ack = delivery.AddOutgoing(tenantId, conversation, MessageSender.Bot, OptOutReply, replyToMessageId: message.Id);
            try
            {
                await conversations.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return;
            }

            await delivery.DeliverAsync(tenantId, conversation, contact, ack, cancellationToken);
            return;
        }

        var tenant = await tenants.GetAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant không tồn tại.");

        // Hết hạn mức hội thoại tháng và đây là khách mới của tháng → không gọi AI, chuyển nhân viên (CLAUDE.md M6 đợt B4).
        if (!await quota.AllowBotAsync(tenantId, tenant, conversation, cancellationToken))
        {
            await HandOffOverQuotaAsync(tenantId, conversation, contact, message.Id, cancellationToken);
            return;
        }

        var unsummarized = await conversations.ListMessagesAsync(tenantId, conversation.Id, conversation.SummarizedUntil, cancellationToken);
        var history = unsummarized
            .TakeWhile(m => m.Id != message.Id)
            .Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc)))
            .ToList();

        var result = await bot.ReplyAsync(
            new BotTurnRequest(
                tenantId,
                new BotProfile(tenant.Name, tenant.IndustrySlug, tenant.BotName, tenant.BotPronoun, tenant.BotTone, tenant.BotInstructions, tenant.PrivacyUrl),
                encryptor.Decrypt(message.ContentEnc),
                history,
                conversation.SummaryEnc is null ? null : encryptor.Decrypt(conversation.SummaryEnc),
                IsFirstBotReply: !await conversations.HasBotReplyAsync(tenantId, conversation.Id, cancellationToken),
                CustomerName: contact.DisplayName,
                Salutation: contact.Salutation,
                SalutationByStaff: contact.SalutationSource == CustomerSalutation.SourceStaff),
            cancellationToken);

        var replies = AddReplies(tenantId, conversation, message.Id, result);
        AddUsage(tenantId, conversation.Id, result.Usage);

        // Khách tự xưng (anh/chị/cô/chú/bác) → nhớ để lần sau gọi đúng; cách gọi nhân viên đã đặt thì giữ nguyên.
        if (result.DetectedSalutation is { } detected && contact.SalutationSource != CustomerSalutation.SourceStaff && contact.Salutation != detected)
        {
            contact.Salutation = detected;
            contact.SalutationSource = CustomerSalutation.SourceCustomer;
        }

        if (result.NeedsHuman)
        {
            conversation.Mode = ConversationMode.Human;
            conversation.HandoffReason = result.HandoffReason;
        }

        if (result.Urgency > conversation.Urgency)
        {
            conversation.Urgency = result.Urgency;
        }

        var leadFields = MergeLeadFields(contact, result.LeadFields);

        // Khách tự cho SĐT → gắn vào hồ sơ; trùng khách DN đã nhập từ danh sách thì gộp lịch sử chăm sóc vào hồ sơ đang chat.
        await phoneLinker.LinkAsync(tenantId, contact, leadFields.GetValueOrDefault("phone"), cancellationToken);
        var customerMessages = history.Count(t => t.Sender == MessageSender.Customer) + 1 + (conversation.SummaryEnc is null ? 0 : 3);
        LeadRules.Promote(contact, LeadRules.FromBotTurn(leadFields, result.HandoffReason, customerMessages), time.GetUtcNow());

        try
        {
            await conversations.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return; // job khác vừa trả lời cùng tin này
        }

        // Chuyển người: câu chuyển tiếp (trong/ngoài giờ) + chờ người + Telegram + báo hộp thư.
        var notice = result.NeedsHuman
            ? await inbox.AfterBotHandoffAsync(tenantId, conversation, result.Urgency, contact.Salutation, replyToMessageId: null, cancellationToken)
            : null;

        for (var i = 0; i < replies.Count; i++)
        {
            if (i > 0 && options.Value.BotMessageGapMs > 0)
            {
                await Task.Delay(options.Value.BotMessageGapMs, cancellationToken); // nhịp gõ như người thật giữa các tin
            }

            await delivery.DeliverAsync(tenantId, conversation, contact, replies[i], cancellationToken);
        }

        if (notice is not null)
        {
            await delivery.DeliverAsync(tenantId, conversation, contact, notice, cancellationToken);
        }

        await SummarizeIfLongAsync(tenantId, conversation, unsummarized, cancellationToken);
    }

    /// <summary>
    /// Hết hạn mức: chế độ nhân viên + câu chuyển tiếp (gắn reply_to_message_id = tin khách → chạy lại không gửi trùng) + Telegram.
    /// </summary>
    private async Task HandOffOverQuotaAsync(Guid tenantId, Conversation conversation, Contact contact, Guid customerMessageId, CancellationToken cancellationToken)
    {
        conversation.Mode = ConversationMode.Human;
        conversation.HandoffReason = ConversationQuotaService.HandoffReason;
        Message? notice;
        try
        {
            notice = await inbox.AfterBotHandoffAsync(tenantId, conversation, Urgency.None, contact.Salutation, customerMessageId, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return; // job khác vừa xử lý cùng tin này
        }

        if (notice is not null)
        {
            await delivery.DeliverAsync(tenantId, conversation, contact, notice, cancellationToken);
        }
    }

    /// <summary>
    /// Mỗi tin bot là một dòng (gửi thành một tin riêng trên kênh). Tin ĐẦU giữ reply_to_message_id (unique), tin CUỐI giữ ai_trace
    /// (nút "Vì sao bot trả lời vậy?" nằm cuối lượt trả lời, không nằm ở câu báo trợ lý AI).
    /// Job chạy song song cùng tin khách → lỗi unique, cả lượt bị bỏ; chạy lại → gửi nốt các tin pending theo thứ tự.
    /// created_at cách nhau 1 ms để thứ tự hiển thị/gửi lại luôn đúng.
    /// </summary>
    private List<Message> AddReplies(Guid tenantId, Conversation conversation, Guid customerMessageId, BotTurnResult result)
    {
        var now = time.GetUtcNow();
        var replies = new List<Message>(result.Messages.Count);
        for (var i = 0; i < result.Messages.Count; i++)
        {
            var reply = delivery.AddOutgoing(
                tenantId,
                conversation,
                MessageSender.Bot,
                result.Messages[i],
                replyToMessageId: i == 0 ? customerMessageId : null,
                traceJson: i == result.Messages.Count - 1 ? result.TraceJson : null);
            reply.CreatedAt = now.AddMilliseconds(i);
            replies.Add(reply);
        }

        return replies;
    }

    private void AddUsage(Guid tenantId, Guid conversationId, IEnumerable<BotUsage> usage)
    {
        foreach (var u in usage)
        {
            conversations.AddUsage(tenantId, new UsageRecord
            {
                Kind = u.Kind,
                Provider = u.Provider,
                Model = u.Model,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CostUsd = u.CostUsd,
                ConversationId = conversationId,
            });
        }
    }

    /// <summary>
    /// Mỗi tin khách: cập nhật mốc tương tác của khách; khách đã nhắn lại → gợi ý "Cần chăm sóc" đang mở tự xong (không cần nhắn nữa).
    /// Chạy lại an toàn (chỉ tiến mốc thời gian, gợi ý đã đóng thì thôi).
    /// </summary>
    private async Task TrackCustomerActivityAsync(Guid tenantId, Contact contact, Message message, CancellationToken cancellationToken)
    {
        var changed = false;
        if (contact.LastCustomerMessageAt is null || contact.LastCustomerMessageAt < message.CreatedAt)
        {
            contact.LastCustomerMessageAt = message.CreatedAt;
            changed = true;
        }

        if (contact.ProactiveAwaitingReply && (contact.LastProactiveAt is null || contact.LastProactiveAt < message.CreatedAt))
        {
            contact.ProactiveAwaitingReply = false; // khách đã trả lời tin bot chủ động nhắn
            changed = true;
        }

        if (contact.ProactiveOptOutAt is null && CareDecision.IsOptOutCommand(encryptor.Decrypt(message.ContentEnc)))
        {
            // Khách từ chối nhận tin chủ động (CLAUDE.md mục 7). Khách tự nhắn tới vẫn được trả lời bình thường.
            contact.ProactiveOptOutAt = time.GetUtcNow();
            contact.ProactiveOptOutSource = "customer";
            changed = true;
        }

        if (await contacts.GetOpenSuggestionAsync(tenantId, contact.Id, cancellationToken) is { } open && open.CreatedAt < message.CreatedAt)
        {
            open.Status = CareSuggestionStatus.Done;
            open.Outcome = "customer_replied";
            open.ResolvedAt = time.GetUtcNow();
            changed = true;
        }

        if (changed)
        {
            await conversations.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Gộp thông tin khách tự cung cấp vào hồ sơ khách (mã hóa cả khối, vì có tên/SĐT). Trả toàn bộ thông tin sau khi gộp.</summary>
    private Dictionary<string, string> MergeLeadFields(Contact contact, IReadOnlyDictionary<string, string> leadFields)
    {
        var current = contact.LeadFieldsEnc is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(encryptor.Decrypt(contact.LeadFieldsEnc)) ?? [];
        if (leadFields.Count == 0)
        {
            return current;
        }

        foreach (var (key, value) in leadFields)
        {
            current[key] = value;
        }

        contact.LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(current));
        return current;
    }

    /// <summary>
    /// Hội thoại dài: tóm tắt các tin cũ (giữ nguyên văn <see cref="AiOptions.BotHistoryTurns"/> tin gần nhất) để tiết kiệm token.
    /// Lỗi AI ở bước này không ảnh hưởng câu trả lời đã gửi — lần sau thử lại.
    /// </summary>
    private async Task SummarizeIfLongAsync(Guid tenantId, Conversation conversation, IReadOnlyList<Message> unsummarized, CancellationToken cancellationToken)
    {
        var keep = options.Value.BotHistoryTurns;
        if (unsummarized.Count <= keep + SummarizeSlack)
        {
            return;
        }

        var old = unsummarized.Take(unsummarized.Count - keep).ToList();
        try
        {
            var summary = await bot.SummarizeAsync(
                tenantId,
                conversation.SummaryEnc is null ? null : encryptor.Decrypt(conversation.SummaryEnc),
                old.Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc))).ToList(),
                cancellationToken);
            conversation.SummaryEnc = encryptor.Encrypt(summary.Summary);
            conversation.SummarizedUntil = old[^1].CreatedAt;
            AddUsage(tenantId, conversation.Id, summary.Usage);
            await conversations.SaveChangesAsync(cancellationToken);
        }
        catch (AiProviderException ex)
        {
            LogSummaryFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tóm tắt hội thoại lỗi ({ErrorType}), sẽ thử ở tin sau")]
    private static partial void LogSummaryFailed(ILogger logger, string errorType);
}
