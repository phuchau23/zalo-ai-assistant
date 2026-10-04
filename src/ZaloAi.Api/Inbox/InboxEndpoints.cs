using System.Text.Json;
using System.Threading.Channels;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using StackExchange.Redis;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Common;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Inbox;

/// <param name="Mode">bot | human</param>
/// <param name="Urgency">none | normal | urgent</param>
/// <param name="NeedsAttentionSince">Khách chờ người từ lúc nào (null = không ai cần làm gì).</param>
/// <param name="LastSender">customer | bot | staff | system</param>
/// <param name="LeadStatus">new | interested | hot | won | lost</param>
public sealed record InboxConversationItem(
    Guid Id,
    string Channel,
    bool IsTest,
    string Mode,
    string Urgency,
    string? HandoffReason,
    DateTimeOffset? NeedsAttentionSince,
    Guid? AssignedUserId,
    Guid ContactId,
    string ContactName,
    string LeadStatus,
    DateTimeOffset? LastMessageAt,
    string? LastMessagePreview,
    string? LastSender);

/// <param name="Sender">customer | bot | staff | system</param>
/// <param name="DeliveryStatus">none | pending | sent | failed</param>
/// <param name="Proactive">Tin bot chủ động nhắn (chăm sóc), không phải trả lời khách.</param>
public sealed record InboxMessage(
    Guid Id,
    string Sender,
    Guid? SenderUserId,
    string Text,
    DateTimeOffset CreatedAt,
    string DeliveryStatus,
    string? DeliveryError,
    ChatTraceResponse? Trace,
    bool Proactive);

/// <param name="LeadFields">Thông tin khách tự cung cấp cho bot (tên, SĐT, dịch vụ quan tâm...).</param>
public sealed record InboxConversationDetail(
    InboxConversationItem Conversation,
    IReadOnlyDictionary<string, string> LeadFields,
    IReadOnlyList<InboxMessage> Messages);

public sealed record SendInboxMessageRequest(string Text);

public sealed record AssignConversationRequest(Guid? UserId);

/// <param name="Role">owner | staff</param>
public sealed record TenantMemberResponse(Guid UserId, string Name, string Role);

internal sealed class SendInboxMessageRequestValidator : AbstractValidator<SendInboxMessageRequest>
{
    public SendInboxMessageRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(1900).WithName("Tin nhắn"); // chừa chỗ cho chữ ký (Zalo ≤ 2.000 ký tự/tin)
    }
}

/// <summary>
/// Hộp thư nhân viên (CLAUDE.md M5). tenantId luôn từ ITenantContext. Nội dung tin chỉ giải mã khi trả cho người có quyền xem;
/// xem chi tiết hội thoại ghi audit log (giới hạn 1 lần / người / hội thoại / 30 phút để không ngập log).
/// </summary>
internal static class InboxEndpoints
{
    private static readonly JsonSerializerOptions TraceJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/inbox").WithTags("Inbox");

        group.MapGet("/conversations", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapGet("/conversations/{conversationId:guid}", GetAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/take-over", TakeOverAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/return-to-bot", ReturnToBotAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/messages", SendAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<SendInboxMessageRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/assign", AssignAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/stream", StreamAsync).RequireTenantRole(TenantRole.Staff).ExcludeFromDescription();

        app.MapGet("/tenant/members", MembersAsync).WithTags("Tenant").RequireTenantRole(TenantRole.Staff);

        return app;
    }

    /// <param name="filter">attention | bot | human | mine | all</param>
    private static async Task<Ok<List<InboxConversationItem>>> ListAsync(
        string? filter,
        bool? includeTest,
        ITenantContext tenant,
        ConversationRepository conversations,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var rows = await conversations.ListInboxAsync(
            tenant.RequireTenantId(), filter ?? "all", tenant.UserId, includeTest ?? true, limit: 100, cancellationToken);
        return TypedResults.Ok(rows.Select(r => ToItem(r.Conversation, r.Contact, r.LastMessage, encryptor)).ToList());
    }

    private static async Task<Ok<InboxConversationDetail>> GetAsync(
        Guid conversationId,
        ITenantContext tenant,
        ConversationRepository conversations,
        AuditLogRepository audit,
        IDistributedStore store,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken) ?? throw new NotFoundException();
        var contact = await conversations.GetContactAsync(tenantId, conversation.ContactId, cancellationToken) ?? throw new NotFoundException();
        var messages = await conversations.ListMessagesAsync(tenantId, conversationId, after: null, cancellationToken);

        if (await store.SetIfNotExistsAsync($"audit:conversation-view:{tenant.UserId:N}:{conversationId:N}", "1", TimeSpan.FromMinutes(30), cancellationToken))
        {
            audit.Add(tenantId, tenant.UserId, "conversation.viewed", conversationId.ToString());
            await audit.SaveChangesAsync(cancellationToken);
        }

        var leads = contact.LeadFieldsEnc is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(encryptor.Decrypt(contact.LeadFieldsEnc)) ?? [];
        return TypedResults.Ok(new InboxConversationDetail(
            ToItem(conversation, contact, messages.Count > 0 ? messages[^1] : null, encryptor),
            leads,
            messages.Select(m => new InboxMessage(
                m.Id,
                Lower(m.Sender),
                m.SenderUserId,
                encryptor.Decrypt(m.ContentEnc),
                m.CreatedAt,
                Lower(m.DeliveryStatus),
                m.DeliveryError,
                m.AiTraceJson is null ? null : JsonSerializer.Deserialize<ChatTraceResponse>(m.AiTraceJson, TraceJson),
                m.Proactive)).ToList()));
    }

    private static async Task<NoContent> TakeOverAsync(
        Guid conversationId,
        ITenantContext tenant,
        InboxService inbox,
        AccessQueries access,
        CancellationToken cancellationToken)
    {
        var (userId, name) = await CurrentUserAsync(tenant, access, cancellationToken);
        await inbox.TakeOverAsync(tenant.RequireTenantId(), conversationId, userId, name, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ReturnToBotAsync(Guid conversationId, ITenantContext tenant, InboxService inbox, CancellationToken cancellationToken)
    {
        await inbox.ReturnToBotAsync(tenant.RequireTenantId(), conversationId, tenant.UserId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Accepted> SendAsync(
        Guid conversationId,
        SendInboxMessageRequest request,
        ITenantContext tenant,
        InboxService inbox,
        AccessQueries access,
        CancellationToken cancellationToken)
    {
        var (userId, name) = await CurrentUserAsync(tenant, access, cancellationToken);
        await inbox.SendStaffMessageAsync(tenant.RequireTenantId(), conversationId, userId, name, request.Text.Trim(), cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<NoContent> AssignAsync(
        Guid conversationId,
        AssignConversationRequest request,
        ITenantContext tenant,
        InboxService inbox,
        MembershipRepository memberships,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (request.UserId is { } assignee && await memberships.GetAsync(tenantId, assignee, cancellationToken) is null)
        {
            throw new InvalidInputException("Người được gán không thuộc doanh nghiệp này.");
        }

        await inbox.AssignAsync(tenantId, conversationId, request.UserId, tenant.UserId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<TenantMemberResponse>>> MembersAsync(
        ITenantContext tenant,
        MembershipRepository memberships,
        CancellationToken cancellationToken)
    {
        var list = await memberships.ListAsync(tenant.RequireTenantId(), cancellationToken);
        return TypedResults.Ok(list.Select(m => new TenantMemberResponse(m.UserId, m.User?.Name ?? "", Lower(m.Role))).ToList());
    }

    /// <summary>
    /// Server-Sent Events: đẩy {type, conversationId} khi có tin mới / cần người / nhắc. Chỉ id, không nội dung — trình duyệt tự tải lại
    /// qua API có kiểm quyền. Kênh Redis riêng theo tenant (tenant từ cookie). Gửi "ping" mỗi 25 giây để giữ kết nối qua proxy.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, ITenantContext tenant, IConnectionMultiplexer redis, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        var subscriber = redis.GetSubscriber();
        var channel = RedisInboxNotifier.ChannelFor(tenantId);
        await subscriber.SubscribeAsync(channel, (_, value) => queue.Writer.TryWrite(value.ToString()));
        try
        {
            await http.Response.WriteAsync("event: ready\ndata: {}\n\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(25));
                string line;
                try
                {
                    line = $"data: {await queue.Reader.ReadAsync(heartbeat.Token)}\n\n";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    line = ": ping\n\n";
                }

                await http.Response.WriteAsync(line, cancellationToken);
                await http.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // trình duyệt đóng kết nối
        }
        finally
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    private static async Task<(Guid UserId, string Name)> CurrentUserAsync(ITenantContext tenant, AccessQueries access, CancellationToken cancellationToken)
    {
        var userId = tenant.UserId ?? throw new ForbiddenException();
        var user = await access.GetUserAsync(userId, cancellationToken) ?? throw new ForbiddenException();
        return (userId, user.Name);
    }

    internal static InboxConversationItem ToItem(Conversation c, Contact contact, Message? last, IFieldEncryptor encryptor)
    {
        string? preview = null;
        if (last is not null)
        {
            var text = encryptor.Decrypt(last.ContentEnc).ReplaceLineEndings(" ");
            preview = text.Length > 120 ? text[..120] + "…" : text;
        }

        return new InboxConversationItem(
            c.Id,
            Lower(c.Channel),
            c.IsTest,
            Lower(c.Mode),
            Lower(c.Urgency),
            c.HandoffReason,
            c.NeedsAttentionSince,
            c.AssignedUserId,
            contact.Id,
            ContactView.Name(contact, ContactView.LeadFields(contact, encryptor)),
            contact.LeadStatus.ToString().ToLowerInvariant(),
            c.LastMessageAt ?? last?.CreatedAt,
            preview,
            last is null ? null : Lower(last.Sender));
    }

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
