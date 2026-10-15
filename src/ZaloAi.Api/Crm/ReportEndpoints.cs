using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Reports;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Crm;

public sealed record AskReportRequest(string Question);

/// <param name="Report">Báo cáo đã chọn (revenue | customers | staff | occupancy | outstanding | today | ai) hoặc null nếu không trả lời được.</param>
/// <param name="Data">Số liệu hệ thống tính (JSON) để giao diện vẽ.</param>
public sealed record AskReportResponse(string Answer, string? Report, string? From, string? To, JsonElement? Data);

internal sealed class AskReportRequestValidator : AbstractValidator<AskReportRequest>
{
    public AskReportRequestValidator() => RuleFor(x => x.Question).NotEmpty().MaximumLength(500).WithName("Câu hỏi");
}

/// <summary>Báo cáo (CRM C6): chủ DN hoặc quyền view_reports. Số liệu tiền chỉ cho người có view_company_revenue (chủ mặc định).</summary>
internal static class ReportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reports").WithTags("Reports").RequirePermission(TenantPermissions.ViewReports);
        group.MapGet("/today", TodayAsync);
        group.MapGet("/revenue", RevenueAsync).ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/revenue/export", ExportRevenueAsync);
        group.MapGet("/ai", AiAsync);
        group.MapGet("/occupancy", OccupancyAsync);
        group.MapGet("/customers", CustomersAsync);
        group.MapGet("/outstanding", OutstandingAsync);
        group.MapPost("/ask", AskAsync).Validate<AskReportRequest>();
        return app;
    }

    private static (DateOnly From, DateOnly To) Dates(string? from, string? to, TimeZoneInfo zone, TimeProvider time, int defaultDays)
    {
        var today = CrmClock.LocalDate(time.GetUtcNow(), zone);
        var t = CrmParsing.IsDate(to) ? CrmParsing.Date(to!) : today;
        var f = CrmParsing.IsDate(from) ? CrmParsing.Date(from!) : t.AddDays(-(defaultDays - 1));
        return (f, t);
    }

    private static async Task<Ok<TodayReport>> TodayAsync(Guid? branchId, ITenantContext tenant, ReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.TodayAsync(tenant.RequireTenantId(), branchId, cancellationToken));

    /// <param name="from">yyyy-MM-dd</param>
    /// <param name="to">yyyy-MM-dd</param>
    /// <param name="groupBy">day | week | month</param>
    private static async Task<Ok<RevenueReport>> RevenueAsync(string? from, string? to, Guid? branchId, string? groupBy, ITenantContext tenant, ReportService reports, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (f, t) = Dates(from, to, await reports.ZoneAsync(tenantId, cancellationToken), time, 30);
        return TypedResults.Ok(await reports.RevenueAsync(tenantId, f, t, branchId, groupBy is "week" or "month" ? groupBy : "day", cancellationToken));
    }

    private static async Task<IResult> ExportRevenueAsync(string? from, string? to, Guid? branchId, string? groupBy, ITenantContext tenant, ReportService reports, AuditLogRepository audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!tenant.Can(TenantPermissions.ExportData))
        {
            throw new ForbiddenException();
        }

        var tenantId = tenant.RequireTenantId();
        var (f, t) = Dates(from, to, await reports.ZoneAsync(tenantId, cancellationToken), time, 30);
        var report = await reports.RevenueAsync(tenantId, f, t, branchId, groupBy is "week" or "month" ? groupBy : "day", cancellationToken);
        audit.Add(tenantId, tenant.UserId, "report.revenue_exported", $"{f:yyyy-MM-dd}..{t:yyyy-MM-dd}");
        await audit.SaveChangesAsync(cancellationToken);
        return Results.File(ReportExcel.Revenue(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"doanh-thu-{f:yyyyMMdd}-{t:yyyyMMdd}.xlsx");
    }

    private static async Task<Ok<AiEffectReport>> AiAsync(string? from, string? to, ITenantContext tenant, ReportService reports, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (f, t) = Dates(from, to, await reports.ZoneAsync(tenantId, cancellationToken), time, 30);
        return TypedResults.Ok(await reports.AiEffectAsync(tenantId, f, t, cancellationToken));
    }

    private static async Task<Ok<OccupancyReport>> OccupancyAsync(string? from, string? to, Guid? branchId, ITenantContext tenant, ReportService reports, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (f, t) = Dates(from, to, await reports.ZoneAsync(tenantId, cancellationToken), time, 14);
        return TypedResults.Ok(await reports.OccupancyAsync(tenantId, f, t, branchId, cancellationToken));
    }

    private static async Task<Ok<CustomerReport>> CustomersAsync(string? from, string? to, ITenantContext tenant, ReportService reports, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (f, t) = Dates(from, to, await reports.ZoneAsync(tenantId, cancellationToken), time, 30);
        return TypedResults.Ok(await reports.CustomersAsync(tenantId, f, t, cancellationToken));
    }

    private static async Task<Ok<OutstandingReport>> OutstandingAsync(Guid? branchId, ITenantContext tenant, ReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reports.OutstandingAsync(tenant.RequireTenantId(), branchId, cancellationToken));

    /// <summary>Hỏi bằng tiếng Việt: AI chọn báo cáo + khoảng thời gian, hệ thống tính số, AI chỉ diễn giải (không bịa số).</summary>
    private static async Task<Ok<AskReportResponse>> AskAsync(
        AskReportRequest request, ITenantContext tenant, ReportService reports, IReportQuestionInterpreter interpreter, ConversationRepository conversations,
        ZaloAi.Infrastructure.Staffing.PerformanceService performance, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var zone = await reports.ZoneAsync(tenantId, cancellationToken);
        var (query, usage) = await interpreter.InterpretAsync(request.Question, CrmClock.ToLocal(time.GetUtcNow(), zone), cancellationToken);
        var allUsage = new List<BotUsage>(usage);
        if (query.Unanswerable)
        {
            Record(conversations, tenantId, allUsage);
            await conversations.SaveChangesAsync(cancellationToken);
            return TypedResults.Ok(new AskReportResponse(query.Explanation ?? "Câu hỏi này không thuộc báo cáo nào có sẵn.", null, null, null, null));
        }

        var (f, t) = Dates(query.From, query.To, zone, time, 30);
        object data = query.Report switch
        {
            "today" => await reports.TodayAsync(tenantId, null, cancellationToken),
            "customers" => await reports.CustomersAsync(tenantId, f, t, cancellationToken),
            "staff" => await performance.ReportAsync(tenantId, CrmClock.FromLocal(f, TimeOnly.MinValue, zone), CrmClock.FromLocal(t.AddDays(1), TimeOnly.MinValue, zone), null, null, cancellationToken),
            "occupancy" => await reports.OccupancyAsync(tenantId, f.DayNumber < t.AddDays(-62).DayNumber ? t.AddDays(-62) : f, t, null, cancellationToken),
            "outstanding" => await reports.OutstandingAsync(tenantId, null, cancellationToken),
            "ai" => await reports.AiEffectAsync(tenantId, f, t, cancellationToken),
            _ => await reports.RevenueAsync(tenantId, f, t, null, query.GroupBy is "week" or "month" ? query.GroupBy : "day", cancellationToken),
        };
        var json = JsonSerializer.Serialize(data, Json);
        var (answer, narrateUsage) = await interpreter.NarrateAsync(request.Question, json.Length > 12000 ? json[..12000] : json, cancellationToken);
        allUsage.AddRange(narrateUsage);
        Record(conversations, tenantId, allUsage);
        await conversations.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new AskReportResponse(answer, query.Report, f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), JsonSerializer.Deserialize<JsonElement>(json)));
    }

    private static void Record(ConversationRepository conversations, Guid tenantId, IEnumerable<BotUsage> usage)
    {
        foreach (var u in usage)
        {
            conversations.AddUsage(tenantId, new UsageRecord { Kind = u.Kind, Provider = u.Provider, Model = u.Model, InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CostUsd = u.CostUsd });
        }
    }
}
