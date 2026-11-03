using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Reports;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Sáng thứ 2 (08:00 giờ VN) gửi tóm tắt tuần qua cho sếp qua Telegram (DN đã kết nối) — CRM C6. Job HỆ THỐNG: IgnoreQueryFilters chỉ để
/// lấy tenant có Telegram; số liệu tính trong scope từng tenant. Không gửi nội dung khách, chỉ con số.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class WeeklyReportJob(AppDbContext db, IServiceScopeFactory scopes, TelegramClient telegram, IOptions<AppOptions> app, TimeProvider time)
{
    public const string RecurringId = "weekly-report";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var tenants = await db.HandoffSettings.IgnoreQueryFilters()
            .Where(h => h.TelegramChatId != null && h.TelegramChatId != "")
            .Select(h => new { h.TenantId, h.TelegramChatId })
            .ToListAsync(cancellationToken);
        foreach (var t in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(t.TenantId, null, null, isSuperAdmin: false);
            var tenant = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t.TenantId, cancellationToken);
            if (tenant is not { Status: TenantStatus.Active })
            {
                continue;
            }

            var text = await BuildAsync(scope.ServiceProvider.GetRequiredService<ReportService>(), t.TenantId, tenant.Name, cancellationToken);
            await telegram.SendAsync(t.TelegramChatId!, text, cancellationToken);
        }
    }

    public async Task<string> BuildAsync(ReportService reports, Guid tenantId, string tenantName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var zone = await reports.ZoneAsync(tenantId, cancellationToken);
        var today = CrmClock.LocalDate(time.GetUtcNow(), zone);
        var to = today.AddDays(-1);
        var from = to.AddDays(-6);
        var revenue = await reports.RevenueAsync(tenantId, from, to, null, "day", cancellationToken);
        var customers = await reports.CustomersAsync(tenantId, from, to, cancellationToken);
        var ai = await reports.AiEffectAsync(tenantId, from, to, cancellationToken);
        var vi = CultureInfo.GetCultureInfo("vi-VN");
        var delta = revenue.PreviousTotal == 0 ? "" : $" ({(revenue.Total >= revenue.PreviousTotal ? "+" : "")}{Math.Round(100.0 * (revenue.Total - revenue.PreviousTotal) / revenue.PreviousTotal)}% so với tuần trước)";
        var topItem = revenue.ByItem.Count > 0 ? revenue.ByItem[0] : null;
        var topStaff = revenue.ByStaff.Count > 0 ? revenue.ByStaff[0] : null;
        return $"""
            📊 Tuần {from:dd/MM}–{to:dd/MM} — {tenantName}
            Doanh thu: {revenue.Total.ToString("#,##0", vi)} đ{delta} · {revenue.Invoices} hóa đơn
            Khách: {customers.ActiveCustomers} ({customers.NewCustomers} mới, {customers.ReturningCustomers} quay lại)
            {(topItem is null ? "" : $"Bán chạy: {topItem.Name} ({topItem.Amount.ToString("#,##0", vi)} đ)\n")}{(topStaff is null ? "" : $"Nhân viên dẫn đầu: {topStaff.Name} ({topStaff.Amount.ToString("#,##0", vi)} đ)\n")}Bot: {ai.BotBookedAppointments} lịch tự chốt, {ai.RevenueFromBot.ToString("#,##0", vi)} đ từ khách bot chốt, xử lý {ai.ConversationsHandledByBot}/{ai.ConversationsTotal} hội thoại
            Xem chi tiết: {app.Value.AdminUrl.TrimEnd('/')}/reports
            """;
    }
}
