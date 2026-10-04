using System.Text.Json;
using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Chat;

public sealed record ChatTraceChunkResponse(
    string Id,
    Guid ChunkId,
    string Source,
    Guid SourceId,
    string? Code,
    string? Title,
    string Excerpt,
    double Score,
    bool MedicallyReviewed,
    bool Used);

/// <summary>"Vì sao bot trả lời vậy?" — không chứa nội dung tin hay dữ liệu cá nhân của khách.</summary>
/// <param name="Guards">Các lớp an toàn đã can thiệp: danger_keyword, forbidden_retry, price_without_source, ...</param>
public sealed record ChatTraceResponse(
    string Template,
    string? Model,
    int Calls,
    long LatencyMs,
    string? DangerSignal,
    IReadOnlyList<string> Guards,
    IReadOnlyList<string> Forbidden,
    IReadOnlyList<ChatTraceChunkResponse> Chunks,
    int BelowThreshold,
    string Confidence,
    bool NeedsHuman,
    string? HandoffReason,
    string Urgency,
    string? Sentiment,
    bool HealthTopic,
    IReadOnlyList<string> LeadKeys,
    int PiiMasked,
    bool FirstReply);

/// <param name="Sender">customer | bot | staff | system</param>
public sealed record ChatMessageResponse(Guid Id, string Sender, string Text, DateTimeOffset CreatedAt, ChatTraceResponse? Trace);

/// <param name="Mode">bot | human</param>
/// <param name="Urgency">none | normal | urgent</param>
/// <param name="WaitingForBot">Tin cuối là của khách và bot đang xử lý → giao diện hiện "đang trả lời...".</param>
public sealed record ChatTestConversationResponse(
    Guid Id,
    string Mode,
    string? HandoffReason,
    string Urgency,
    bool WaitingForBot,
    decimal CostUsd,
    int AiCalls,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChatMessageResponse> Messages);

public sealed record ChatTestConversationSummary(Guid Id, string Mode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SendChatTestMessageRequest(string Text);

internal sealed class SendChatTestMessageRequestValidator : AbstractValidator<SendChatTestMessageRequest>
{
    public const int MaxLength = 1000;

    public SendChatTestMessageRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(MaxLength).WithName("Tin nhắn");
    }
}

/// <summary>
/// Khung "Chat thử" trong trang quản trị: đóng vai khách nhắn cho bot qua kênh webchat, cùng hàng đợi + worker như Zalo (M4).
/// Chỉ thao tác hội thoại thử (is_test) của tenant hiện tại; tenantId luôn từ ITenantContext.
/// </summary>
internal static class ChatTestEndpoints
{
    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/chat-test").WithTags("ChatTest");

        group.MapGet("/conversations", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPost("/conversations", CreateAsync).RequireTenantRole(TenantRole.Staff);
        group.MapGet("/conversations/{conversationId:guid}", GetAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/messages", SendAsync)
            .RequireTenantRole(TenantRole.Staff)
            .RequireRateLimiting(RateLimitSettings.ChatTestPolicy)
            .Validate<SendChatTestMessageRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/conversations/{conversationId:guid}/return-to-bot", ReturnToBotAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/conversations/{conversationId:guid}", DeleteAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<List<ChatTestConversationSummary>>> ListAsync(
        ITenantContext tenant,
        ConversationRepository conversations,
        CancellationToken cancellationToken)
    {
        var list = await conversations.ListConversationsAsync(tenant.RequireTenantId(), isTest: true, limit: 20, cancellationToken);
        return TypedResults.Ok(list.Select(c => new ChatTestConversationSummary(c.Id, Lower(c.Mode), c.CreatedAt, c.UpdatedAt)).ToList());
    }

    private static async Task<Ok<ChatTestConversationResponse>> CreateAsync(
        ITenantContext tenant,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var contact = await conversations.GetOrAddContactAsync(
            tenantId, ChannelKind.Webchat, $"test-{Guid.CreateVersion7():N}", null, cancellationToken);
        var conversation = conversations.AddConversation(tenantId, new Conversation
        {
            ContactId = contact.Id,
            Channel = ChannelKind.Webchat,
            IsTest = true,
        });
        await conversations.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(tenantId, conversation, conversations, encryptor, cancellationToken));
    }

    private static async Task<Ok<ChatTestConversationResponse>> GetAsync(
        Guid conversationId,
        ITenantContext tenant,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await RequireTestAsync(tenantId, conversationId, conversations, cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(tenantId, conversation, conversations, encryptor, cancellationToken));
    }

    private static async Task<Accepted<ChatMessageResponse>> SendAsync(
        Guid conversationId,
        SendChatTestMessageRequest request,
        ITenantContext tenant,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        IBackgroundJobClient jobs,
        InboxService inbox,
        IInboxNotifier notifier,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await RequireTestAsync(tenantId, conversationId, conversations, cancellationToken);
        var text = request.Text.Trim();
        var message = conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = encryptor.Encrypt(text),
        });
        conversation.LastCustomerMessageAt = conversation.LastMessageAt = DateTimeOffset.UtcNow;
        var human = conversation.Mode == ConversationMode.Human;
        if (human)
        {
            inbox.MarkNeedsAttention(conversation); // nhân viên đang xử lý: tin hiện nổi bật ở Hộp thư, bot không trả lời
        }

        await conversations.SaveChangesAsync(cancellationToken);

        var messageId = message.Id;
        jobs.Enqueue<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, messageId, CancellationToken.None));
        await notifier.PublishAsync(tenantId, new InboxEvent(human ? "attention" : "message", conversation.Id), cancellationToken);

        return TypedResults.Accepted((string?)null, new ChatMessageResponse(message.Id, "customer", text, message.CreatedAt, null));
    }

    private static async Task<Ok<ChatTestConversationResponse>> ReturnToBotAsync(
        Guid conversationId,
        ITenantContext tenant,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        InboxService inbox,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await RequireTestAsync(tenantId, conversationId, conversations, cancellationToken);
        await inbox.ReturnToBotAsync(tenantId, conversationId, tenant.UserId, cancellationToken);
        var conversation = await RequireTestAsync(tenantId, conversationId, conversations, cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(tenantId, conversation, conversations, encryptor, cancellationToken));
    }

    private static async Task<NoContent> DeleteAsync(
        Guid conversationId,
        ITenantContext tenant,
        ConversationRepository conversations,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await RequireTestAsync(tenantId, conversationId, conversations, cancellationToken);
        conversations.Remove(tenantId, conversation);
        await conversations.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Hội thoại của tenant VÀ là hội thoại thử — không cho trang chat thử đụng hội thoại khách thật.</summary>
    private static async Task<Conversation> RequireTestAsync(
        Guid tenantId,
        Guid conversationId,
        ConversationRepository conversations,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken);
        return conversation is { IsTest: true } ? conversation : throw new NotFoundException();
    }

    private static async Task<ChatTestConversationResponse> ToResponseAsync(
        Guid tenantId,
        Conversation conversation,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var messages = await conversations.ListMessagesAsync(tenantId, conversation.Id, after: null, cancellationToken);
        var (cost, calls) = await conversations.UsageForConversationAsync(tenantId, conversation.Id, cancellationToken);
        var waiting = conversation.Mode == ConversationMode.Bot && messages.Count > 0 && messages[^1].Sender == MessageSender.Customer;
        return new ChatTestConversationResponse(
            conversation.Id,
            Lower(conversation.Mode),
            conversation.HandoffReason,
            Lower(conversation.Urgency),
            waiting,
            Math.Round(cost, 6),
            calls,
            conversation.CreatedAt,
            messages.Select(m => new ChatMessageResponse(
                m.Id,
                Lower(m.Sender),
                encryptor.Decrypt(m.ContentEnc),
                m.CreatedAt,
                m.AiTraceJson is null ? null : JsonSerializer.Deserialize<ChatTraceResponse>(m.AiTraceJson, TraceJson))).ToList());
    }

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
