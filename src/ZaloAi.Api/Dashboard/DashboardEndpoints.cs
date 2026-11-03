using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ZaloAi.Ai.Privacy;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Billing;
using ZaloAi.Infrastructure.Dashboard;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Dashboard;

public sealed record DashboardDay(DateOnly Date, int Conversations);

/// <param name="Used">Hội thoại thật từ đầu tháng (giờ Việt Nam), không tính Chat thử.</param>
/// <param name="Quota">Hạn mức tháng; null = không giới hạn.</param>
/// <param name="Warning">Đã dùng từ 80% hạn mức.</param>
/// <param name="Exceeded">Đã hết: khách mới nhắn sẽ chuyển nhân viên.</param>
public sealed record UsageResponse(int Used, int? Quota, string PlanName, bool Warning, bool Exceeded);

/// <param name="Question">Tin khách (đã che số điện thoại, email, số giấy tờ). null nếu không tìm thấy tin.</param>
/// <param name="Reason">no_knowledge (thiếu dữ liệu) | low_confidence (bot không chắc)</param>
public sealed record UnansweredQuestion(Guid ConversationId, string? Question, string Reason, DateTimeOffset At);

/// <param name="Days">7 | 30</param>
/// <param name="Daily">Số hội thoại (khách khác nhau có nhắn) mỗi ngày, giờ Việt Nam, không tính Chat thử.</param>
/// <param name="Conversations">Số hội thoại có khách nhắn trong khoảng.</param>
/// <param name="BotHandled">Trong số đó, bot tự xử lý hết (không chuyển nhân viên, nhân viên không phải nhắn).</param>
/// <param name="NewContacts">Khách mới trong khoảng (không tính Chat thử).</param>
/// <param name="NewLeads">Khách mới đã thành khách tiềm năng (Quan tâm, Nóng, Đã chốt).</param>
/// <param name="AiCostUsdMonth">Chi phí AI từ đầu tháng (giờ Việt Nam), gồm cả Chat thử.</param>
/// <param name="Truncated">Quá nhiều tin để vẽ đủ biểu đồ (số liệu ngày có thể thiếu).</param>
public sealed record DashboardResponse(
    int Days,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DashboardDay> Daily,
    int Conversations,
    int BotHandled,
    int NewContacts,
    int NewLeads,
    decimal AiCostUsdMonth,
    IReadOnlyList<UnansweredQuestion> Unanswered,
    bool Truncated);

/// <summary>
/// Dashboard của chủ DN (CLAUDE.md M6). Chỉ owner (có chi phí và câu hỏi của khách). Câu bot không trả lời được dùng để bổ sung
/// kho kiến thức nên che dữ liệu cá nhân trước khi trả về; xem danh sách có ghi audit log (tối đa 1 lần / 30 phút / người).
/// </summary>
internal static class DashboardEndpoints
{
    private const int UnansweredLimit = 30;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", GetAsync)
            .WithTags("Dashboard")
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        app.MapGet("/tenant/usage", UsageAsync).WithTags("Dashboard").RequireTenantRole(TenantRole.Owner);
        return app;
    }

    /// <summary>Hạn mức hội thoại tháng này (băng cảnh báo cho chủ DN).</summary>
    private static async Task<Ok<UsageResponse>> UsageAsync(
        ITenantContext tenant,
        TenantRepository tenants,
        ConversationQuotaService quota,
        IOptions<BillingOptions> billing,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var row = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        var usage = await quota.UsageAsync(tenantId, row, cancellationToken);
        var plan = billing.Value.Plans.TryGetValue(row.Plan, out var p) ? p.Name : row.Plan;
        return TypedResults.Ok(new UsageResponse(usage.Used, usage.Quota, plan, usage.Warning, usage.Exceeded));
    }

    /// <param name="days">7 (mặc định) hoặc 30.</param>
    private static async Task<Ok<DashboardResponse>> GetAsync(
        int? days,
        ITenantContext tenant,
        DashboardRepository dashboard,
        AuditLogRepository audit,
        IDistributedStore store,
        IFieldEncryptor encryptor,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var range = days ?? 7;
        if (range is not (7 or 30))
        {
            throw new InvalidInputException("Chỉ xem được 7 hoặc 30 ngày.");
        }

        var tenantId = tenant.RequireTenantId();
        var window = DashboardStats.Window(time.GetUtcNow(), range);
        var activity = await dashboard.ListCustomerActivityAsync(tenantId, window.From, window.To, cancellationToken);
        var neededHuman = await dashboard.ListNeededHumanAsync(tenantId, window.From, window.To, cancellationToken);
        var (newContacts, newLeads) = await dashboard.CountNewContactsAsync(tenantId, window.From, cancellationToken);
        var cost = await dashboard.SumAiCostAsync(tenantId, window.MonthStart, cancellationToken);
        var unanswered = await dashboard.ListUnansweredAsync(tenantId, window.From, UnansweredLimit, cancellationToken);

        if (unanswered.Count > 0
            && await store.SetIfNotExistsAsync($"audit:dashboard-view:{tenant.UserId:N}", "1", TimeSpan.FromMinutes(30), cancellationToken))
        {
            audit.Add(tenantId, tenant.UserId, "dashboard.unanswered_viewed", $"{unanswered.Count}");
            await audit.SaveChangesAsync(cancellationToken);
        }

        var conversations = activity.Select(a => a.ConversationId).Distinct().ToList();
        return TypedResults.Ok(new DashboardResponse(
            range,
            window.FirstDay,
            window.LastDay,
            DashboardStats.Daily(window, activity).Select(d => new DashboardDay(d.Day, d.Conversations)).ToList(),
            conversations.Count,
            DashboardStats.BotHandled(conversations, neededHuman),
            newContacts,
            newLeads,
            cost,
            unanswered.Select(u => new UnansweredQuestion(
                u.ConversationId,
                u.CustomerTextEnc is null ? null : PiiMasker.Mask(encryptor.Decrypt(u.CustomerTextEnc), new PiiVault()),
                u.Reason,
                u.At)).ToList(),
            activity.Count >= DashboardRepository.MaxActivityRows));
    }
}
