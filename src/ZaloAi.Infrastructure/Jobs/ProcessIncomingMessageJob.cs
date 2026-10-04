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
    TenantRepository tenants,
    IBotEngine bot,
    IEnumerable<IChannelAdapter> channels,
    IFieldEncryptor encryptor,
    IOptions<AiOptions> options,
    ILogger<ProcessIncomingMessageJob> logger)
{
    /// <summary>Số tin chưa tóm tắt vượt (cửa sổ lịch sử + mức này) thì tóm tắt phần cũ.</summary>
    private const int SummarizeSlack = 6;

    public async Task RunAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);

        var message = await conversations.GetMessageAsync(tenantId, messageId, cancellationToken);
        if (message is null || message.Sender != MessageSender.Customer)
        {
            return;
        }

        if (await conversations.FindReplyAsync(tenantId, messageId, cancellationToken) is not null)
        {
            return; // đã trả lời ở lần chạy trước
        }

        var conversation = await conversations.GetConversationAsync(tenantId, message.ConversationId, cancellationToken);
        if (conversation is null || conversation.Mode == ConversationMode.Human || conversation.Status == ConversationStatus.Closed)
        {
            return;
        }

        if (await conversations.LatestCustomerMessageIdAsync(tenantId, conversation.Id, cancellationToken) != messageId)
        {
            return; // có tin mới hơn, job của tin đó sẽ trả lời gộp
        }

        var tenant = await tenants.GetAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant không tồn tại.");
        var contact = await conversations.GetContactAsync(tenantId, conversation.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("Hội thoại không có khách.");

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
                CustomerName: contact.DisplayName),
            cancellationToken);

        var reply = conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            Sender = MessageSender.Bot,
            ContentEnc = encryptor.Encrypt(result.Reply),
            ReplyToMessageId = message.Id,
            AiTraceJson = result.TraceJson,
        });
        AddUsage(tenantId, conversation.Id, result.Usage);

        if (result.NeedsHuman)
        {
            conversation.Mode = ConversationMode.Human;
            conversation.HandoffReason = result.HandoffReason;
        }

        if (result.Urgency > conversation.Urgency)
        {
            conversation.Urgency = result.Urgency;
        }

        MergeLeadFields(contact, result.LeadFields);

        try
        {
            await conversations.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return; // job khác vừa trả lời cùng tin này
        }

        var channel = channels.FirstOrDefault(c => c.Channel == conversation.Channel)
            ?? throw new InvalidOperationException($"Chưa có adapter cho kênh {conversation.Channel}.");
        await channel.SendAsync(new OutgoingMessage(tenantId, conversation.Id, reply.Id, contact.ExternalUserId, result.Reply), cancellationToken);

        await SummarizeIfLongAsync(tenantId, conversation, unsummarized, cancellationToken);
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

    /// <summary>Gộp thông tin khách tự cung cấp vào hồ sơ khách (mã hóa cả khối, vì có tên/SĐT).</summary>
    private void MergeLeadFields(Contact contact, IReadOnlyDictionary<string, string> leadFields)
    {
        if (leadFields.Count == 0)
        {
            return;
        }

        var current = contact.LeadFieldsEnc is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(encryptor.Decrypt(contact.LeadFieldsEnc)) ?? [];
        foreach (var (key, value) in leadFields)
        {
            current[key] = value;
        }

        contact.LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(current));
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
