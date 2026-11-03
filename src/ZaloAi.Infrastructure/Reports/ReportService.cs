using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Reports;

public sealed record NamedAmount(Guid? Id, string Name, long Amount, int Count);

public sealed record PeriodAmount(string Period, long Revenue, int Invoices);

/// <param name="Previous">Kỳ trước cùng độ dài (so sánh).</param>
public sealed record RevenueReport(
    DateOnly From,
    DateOnly To,
    long Total,
    long Refunds,
    int Invoices,
    long PreviousTotal,
    IReadOnlyList<PeriodAmount> Series,
    IReadOnlyList<NamedAmount> ByKind,
    IReadOnlyList<NamedAmount> ByPaymentMethod,
    IReadOnlyList<NamedAmount> ByItem,
    IReadOnlyList<NamedAmount> ByStaff,
    IReadOnlyList<NamedAmount> ByBranch,
    IReadOnlyList<NamedAmount> BySource);

public sealed record TodayReport(
    DateOnly Date,
    int Appointments,
    int Done,
    int NoShows,
    int Upcoming,
    int BotBooked,
    long Revenue,
    long Cash,
    long Transfer,
    long Card,
    long Wallet,
    int Invoices,
    int NewCustomers,
    int OpenInvoices);

public sealed record AiEffectReport(DateOnly From, DateOnly To, int BotBookedAppointments, int BotBookedDone, long RevenueFromBot, int ConversationsHandledByBot, int ConversationsTotal, decimal AiCostUsd);

public sealed record OccupancyCell(DayOfWeek Day, int Hour, int Appointments);

public sealed record OccupancyDay(DateOnly Date, int BookedMinutes, int AvailableMinutes);

public sealed record OccupancyReport(DateOnly From, DateOnly To, IReadOnlyList<OccupancyCell> Heatmap, IReadOnlyList<OccupancyDay> Days, double UtilizationPercent);

public sealed record CustomerReport(DateOnly From, DateOnly To, int ActiveCustomers, int NewCustomers, int ReturningCustomers, double RetentionPercent, long AverageSpend, IReadOnlyList<NamedAmount> TopCustomers);

public sealed record OutstandingInvoice(Guid InvoiceId, int Number, string? ContactName, long Due, DateTimeOffset IssuedAt);

public sealed record OutstandingReport(long InvoiceDue, IReadOnlyList<OutstandingInvoice> Invoices, int UnusedSessions, long UnusedPackageValue, long WalletBalances);

/// <summary>Báo cáo cho chủ DN (CRM C6). Chỉ đọc; luôn lọc tenant (+ chi nhánh theo quyền); ngày theo múi giờ DN.</summary>
public sealed class ReportService(
    AppDbContext db,
    ITenantContext tenantContext,
    CrmSettingsRepository settingsRepository,
    StaffService staff,
    DashboardRepository dashboard,
    ZaloAi.Core.Security.IFieldEncryptor encryptor,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<TimeZoneInfo> ZoneAsync(Guid tenantId, CancellationToken cancellationToken) =>
        CrmClock.Zone((await settingsRepository.GetAsync(tenantId, cancellationToken)).TimeZone);

    public async Task<TodayReport> TodayAsync(Guid tenantId, Guid? branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        var date = CrmClock.LocalDate(now, zone);
        var (from, to) = Range(date, date, zone);
        var branch = _caller.BranchId ?? branchId;

        var appointments = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.StartAt >= from && a.StartAt < to && (branch == null || a.BranchId == branch) && a.Status != AppointmentStatus.Cancelled)
            .Select(a => new { a.Status, a.Source, a.StartAt }).ToListAsync(cancellationToken);
        var payments = await Db.Payments.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ReceivedAt >= from && p.ReceivedAt < to && p.Amount > 0)
            .Join(Db.Invoices.Where(i => i.TenantId == tenantId && (branch == null || i.BranchId == branch)), p => p.InvoiceId, i => i.Id, (p, i) => new { p.Method, p.Amount })
            .ToListAsync(cancellationToken);
        var revenue = await Db.RevenueEntries.Where(r => r.TenantId == tenantId && r.Date == date && (branch == null || r.BranchId == branch)).SumAsync(r => r.Amount, cancellationToken);
        var invoices = await Db.Invoices.CountAsync(i => i.TenantId == tenantId && i.IssuedAt >= from && i.IssuedAt < to && (branch == null || i.BranchId == branch) && i.Status != InvoiceStatus.Void, cancellationToken);
        var openInvoices = await Db.Invoices.CountAsync(i => i.TenantId == tenantId && (branch == null || i.BranchId == branch) && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.Partial), cancellationToken);
        var newCustomers = await Db.Contacts.CountAsync(c => c.TenantId == tenantId && c.Channel != ChannelKind.Webchat && c.CreatedAt >= from && c.CreatedAt < to, cancellationToken);

        return new TodayReport(
            date,
            appointments.Count,
            appointments.Count(a => a.Status == AppointmentStatus.Done),
            appointments.Count(a => a.Status == AppointmentStatus.NoShow),
            appointments.Count(a => a.StartAt > now && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed)),
            appointments.Count(a => a.Source == AppointmentSource.Bot),
            revenue,
            payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount),
            payments.Where(p => p.Method == PaymentMethod.Transfer).Sum(p => p.Amount),
            payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount),
            payments.Where(p => p.Method == PaymentMethod.Wallet).Sum(p => p.Amount),
            invoices,
            newCustomers,
            openInvoices);
    }

    /// <param name="groupBy">day | week | month</param>
    public async Task<RevenueReport> RevenueAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, Guid? branchId, string groupBy, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidateRange(fromDate, toDate, 366);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var branch = _caller.BranchId ?? branchId;
        var entries = await Db.RevenueEntries.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Date >= fromDate && r.Date <= toDate && (branch == null || r.BranchId == branch))
            .ToListAsync(cancellationToken);
        var length = toDate.DayNumber - fromDate.DayNumber + 1;
        var previousTotal = await Db.RevenueEntries
            .Where(r => r.TenantId == tenantId && r.Date >= fromDate.AddDays(-length) && r.Date < fromDate && (branch == null || r.BranchId == branch))
            .SumAsync(r => r.Amount, cancellationToken);

        var (from, to) = Range(fromDate, toDate, zone);
        var payments = await Db.Payments.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ReceivedAt >= from && p.ReceivedAt < to && p.Amount > 0)
            .Join(Db.Invoices.Where(i => i.TenantId == tenantId && (branch == null || i.BranchId == branch)), p => p.InvoiceId, i => i.Id, (p, i) => new { p.Method, p.Amount })
            .ToListAsync(cancellationToken);
        var invoiceCount = await Db.Invoices.CountAsync(i => i.TenantId == tenantId && i.PaidAt >= from && i.PaidAt < to && (branch == null || i.BranchId == branch) && i.Status != InvoiceStatus.Void, cancellationToken);

        var itemIds = entries.Where(e => e.CatalogItemId != null).Select(e => e.CatalogItemId!.Value).Distinct().ToList();
        var itemNames = await Db.CatalogItems.AsNoTracking().Where(c => itemIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var staffIds = entries.Where(e => e.StaffMemberId != null).Select(e => e.StaffMemberId!.Value).Distinct().ToList();
        var staffNames = await Db.StaffMembers.AsNoTracking().Where(s => staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DisplayName, cancellationToken);
        var branchNames = await Db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        var series = entries.GroupBy(e => PeriodKey(e.Date, groupBy)).OrderBy(g => g.Key)
            .Select(g => new PeriodAmount(g.Key, g.Sum(e => e.Amount), g.Where(e => e.InvoiceId != null).Select(e => e.InvoiceId).Distinct().Count())).ToList();
        series = FillPeriods(series, fromDate, toDate, groupBy);

        return new RevenueReport(
            fromDate,
            toDate,
            entries.Sum(e => e.Amount),
            -entries.Where(e => e.Kind == RevenueKind.Refund).Sum(e => e.Amount),
            invoiceCount,
            previousTotal,
            series,
            Group(entries.Where(e => e.Kind != RevenueKind.Refund), e => (Guid?)null, e => KindLabel(e.Kind), e => e.Amount),
            payments.GroupBy(p => p.Method).Select(g => new NamedAmount(null, MethodLabel(g.Key), g.Sum(p => p.Amount), g.Count())).OrderByDescending(n => n.Amount).ToList(),
            Group(entries.Where(e => e.CatalogItemId != null), e => e.CatalogItemId, e => itemNames.GetValueOrDefault(e.CatalogItemId!.Value, "?"), e => e.Amount).Take(20).ToList(),
            Group(entries.Where(e => e.StaffMemberId != null), e => e.StaffMemberId, e => staffNames.GetValueOrDefault(e.StaffMemberId!.Value, "?"), e => e.Amount),
            Group(entries, e => (Guid?)e.BranchId, e => branchNames.GetValueOrDefault(e.BranchId, "?"), e => e.Amount),
            Group(entries, e => (Guid?)null, e => SourceLabel(e.Source), e => e.Amount));
    }

    public async Task<AiEffectReport> AiEffectAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidateRange(fromDate, toDate, 366);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var (from, to) = Range(fromDate, toDate, zone);
        var botAppointments = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Source == AppointmentSource.Bot && a.CreatedAt >= from && a.CreatedAt < to && a.Status != AppointmentStatus.Cancelled)
            .Select(a => a.Status).ToListAsync(cancellationToken);
        var revenueFromBot = await Db.RevenueEntries.Where(r => r.TenantId == tenantId && r.Date >= fromDate && r.Date <= toDate && r.Source == "bot").SumAsync(r => r.Amount, cancellationToken);
        var activity = await dashboard.ListCustomerActivityAsync(tenantId, from, to, cancellationToken);
        var neededHuman = await dashboard.ListNeededHumanAsync(tenantId, from, to, cancellationToken);
        var conversations = activity.Select(a => a.ConversationId).Distinct().ToList();
        var cost = await Db.UsageRecords.Where(u => u.TenantId == tenantId && u.CreatedAt >= from && u.CreatedAt < to).SumAsync(u => u.CostUsd, cancellationToken);
        return new AiEffectReport(fromDate, toDate, botAppointments.Count, botAppointments.Count(s => s == AppointmentStatus.Done), revenueFromBot,
            Dashboard.DashboardStats.BotHandled(conversations, neededHuman), conversations.Count, cost);
    }

    public async Task<OccupancyReport> OccupancyAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, Guid? branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidateRange(fromDate, toDate, 62);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var branch = _caller.BranchId ?? branchId ?? (await Db.Branches.Where(b => b.TenantId == tenantId && b.IsDefault).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken))
            ?? throw new NotFoundException();
        var (from, to) = Range(fromDate, toDate, zone);
        var appointments = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.BranchId == branch && a.StartAt >= from && a.StartAt < to && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow)
            .Select(a => new { a.StartAt, a.EndAt }).ToListAsync(cancellationToken);

        var heat = appointments.GroupBy(a => (CrmClock.ToLocal(a.StartAt, zone).DayOfWeek, CrmClock.ToLocal(a.StartAt, zone).Hour))
            .Select(g => new OccupancyCell(g.Key.DayOfWeek, g.Key.Hour, g.Count())).OrderBy(c => c.Day).ThenBy(c => c.Hour).ToList();
        var days = new List<OccupancyDay>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            var windows = await staff.WorkWindowsAsync(tenantId, branch, d, cancellationToken);
            var available = windows.Sum(w => SlotCalculator.Minutes(w.End) - SlotCalculator.Minutes(w.Start));
            var booked = appointments.Where(a => CrmClock.LocalDate(a.StartAt, zone) == d).Sum(a => (int)(a.EndAt - a.StartAt).TotalMinutes);
            days.Add(new OccupancyDay(d, booked, available));
        }

        var totalAvailable = days.Sum(x => x.AvailableMinutes);
        return new OccupancyReport(fromDate, toDate, heat, days, totalAvailable == 0 ? 0 : Math.Round(100.0 * days.Sum(x => x.BookedMinutes) / totalAvailable, 1));
    }

    public async Task<CustomerReport> CustomersAsync(Guid tenantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidateRange(fromDate, toDate, 366);
        var zone = await ZoneAsync(tenantId, cancellationToken);
        var (from, to) = Range(fromDate, toDate, zone);
        var length = toDate.DayNumber - fromDate.DayNumber + 1;
        var previousFrom = CrmClock.FromLocal(fromDate.AddDays(-length), TimeOnly.MinValue, zone);

        var visits = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == AppointmentStatus.Done && a.StartAt < to)
            .Select(a => new { a.ContactId, a.StartAt }).ToListAsync(cancellationToken);
        var inPeriod = visits.Where(v => v.StartAt >= from).Select(v => v.ContactId).Distinct().ToHashSet();
        var before = visits.Where(v => v.StartAt < from).Select(v => v.ContactId).Distinct().ToHashSet();
        var previousPeriod = visits.Where(v => v.StartAt >= previousFrom && v.StartAt < from).Select(v => v.ContactId).Distinct().ToHashSet();
        var returning = inPeriod.Count(before.Contains);
        var retained = previousPeriod.Count == 0 ? 0 : Math.Round(100.0 * previousPeriod.Count(inPeriod.Contains) / previousPeriod.Count, 1);

        var spend = await Db.RevenueEntries.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Date >= fromDate && r.Date <= toDate && r.ContactId != null)
            .GroupBy(r => r.ContactId!.Value).Select(g => new { ContactId = g.Key, Amount = g.Sum(r => r.Amount), Count = g.Count() }).ToListAsync(cancellationToken);
        var topIds = spend.OrderByDescending(s => s.Amount).Take(10).Select(s => s.ContactId).ToList();
        var contacts = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && topIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var top = spend.OrderByDescending(s => s.Amount).Take(10)
            .Select(s => new NamedAmount(s.ContactId, contacts.TryGetValue(s.ContactId, out var c) ? Customers.ContactView.Name(c, Customers.ContactView.LeadFields(c, encryptor)) : "?", s.Amount, s.Count)).ToList();
        var paying = spend.Count(s => s.Amount > 0);
        return new CustomerReport(fromDate, toDate, inPeriod.Count, inPeriod.Count - returning, returning, retained, paying == 0 ? 0 : spend.Sum(s => s.Amount) / paying, top);
    }

    public async Task<OutstandingReport> OutstandingAsync(Guid tenantId, Guid? branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var branch = _caller.BranchId ?? branchId;
        var open = await Db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && (branch == null || i.BranchId == branch) && (i.Status == InvoiceStatus.Open || i.Status == InvoiceStatus.Partial) && i.Total - i.PaidAmount + i.RefundedAmount > 0)
            .OrderBy(i => i.IssuedAt).Take(200).ToListAsync(cancellationToken);
        var contactIds = open.Where(i => i.ContactId != null).Select(i => i.ContactId!.Value).Distinct().ToList();
        var contacts = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var packages = await Db.CustomerPackages.AsNoTracking().Where(p => p.TenantId == tenantId && p.Status == CustomerPackageStatus.Active).Select(p => new { p.TotalSessions, p.UsedSessions, p.PricePaid }).ToListAsync(cancellationToken);
        var wallets = await Db.Wallets.Where(w => w.TenantId == tenantId).SumAsync(w => w.Balance, cancellationToken);
        return new OutstandingReport(
            open.Sum(i => i.Due),
            open.Select(i => new OutstandingInvoice(i.Id, i.Number, i.ContactId is { } cid && contacts.TryGetValue(cid, out var c) ? Customers.ContactView.Name(c, Customers.ContactView.LeadFields(c, encryptor)) : null, i.Due, i.IssuedAt)).ToList(),
            packages.Sum(p => Math.Max(0, p.TotalSessions - p.UsedSessions)),
            packages.Sum(p => p.TotalSessions > 0 ? p.PricePaid / p.TotalSessions * Math.Max(0, p.TotalSessions - p.UsedSessions) : 0),
            wallets);
    }

    // ---- helpers ----

    private static (DateTimeOffset From, DateTimeOffset To) Range(DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        (CrmClock.FromLocal(from, TimeOnly.MinValue, zone), CrmClock.FromLocal(to.AddDays(1), TimeOnly.MinValue, zone));

    private static void ValidateRange(DateOnly from, DateOnly to, int maxDays)
    {
        if (to < from || to.DayNumber - from.DayNumber > maxDays)
        {
            throw new InvalidInputException($"Khoảng ngày không hợp lệ (tối đa {maxDays} ngày).");
        }
    }

    public static string PeriodKey(DateOnly date, string groupBy) => groupBy switch
    {
        "month" => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        "week" => WeekStart(date).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // thứ 2

    private static List<PeriodAmount> FillPeriods(List<PeriodAmount> series, DateOnly from, DateOnly to, string groupBy)
    {
        var keys = new List<string>();
        for (var d = groupBy == "week" ? WeekStart(from) : groupBy == "month" ? new DateOnly(from.Year, from.Month, 1) : from; d <= to; d = groupBy == "month" ? d.AddMonths(1) : d.AddDays(groupBy == "week" ? 7 : 1))
        {
            keys.Add(PeriodKey(d, groupBy));
        }

        var map = series.ToDictionary(s => s.Period);
        return keys.Select(k => map.TryGetValue(k, out var s) ? s : new PeriodAmount(k, 0, 0)).ToList();
    }

    private static List<NamedAmount> Group<T>(IEnumerable<T> items, Func<T, Guid?> id, Func<T, string> name, Func<T, long> amount) =>
        items.GroupBy(name).Select(g => new NamedAmount(id(g.First()), g.Key, g.Sum(amount), g.Count())).OrderByDescending(n => n.Amount).ToList();

    public static string KindLabel(RevenueKind kind) => kind switch
    {
        RevenueKind.Service => "Dịch vụ",
        RevenueKind.PackageRecognized => "Gói liệu trình (theo buổi)",
        RevenueKind.Product => "Sản phẩm",
        RevenueKind.Custom => "Khác",
        _ => "Hoàn tiền",
    };

    private static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "Tiền mặt",
        PaymentMethod.Transfer => "Chuyển khoản",
        PaymentMethod.Card => "Thẻ",
        PaymentMethod.Wallet => "Thẻ trả trước",
        _ => "Khác",
    };

    private static string SourceLabel(string? source) => source switch
    {
        "bot" => "Bot Zalo chốt",
        "staff" => "Nhân viên đặt",
        "online" => "Đặt online",
        "walk_in" => "Khách tại quầy",
        _ => "Không rõ",
    };
}
