using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <param name="ServiceIds">Dịch vụ gói bao gồm (từ package_contents; rỗng = gói "tự do" theo mục kiến thức).</param>
public sealed record ActivePackage(CustomerPackage Package, IReadOnlyList<Guid> ServiceIds);

/// <summary>Gói liệu trình của khách (CRM C3): cấp khi mua, trừ buổi khi dùng, hoàn khi hủy hóa đơn, bảo lưu/chuyển nhượng, hết hạn.</summary>
public sealed class PackageService(AppDbContext db, ITenantContext tenantContext, CrmSettingsRepository settingsRepository, Staffing.CommissionService commissions, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<CustomerPackage>> ListForContactAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CustomerPackages.AsNoTracking().Where(p => p.TenantId == tenantId && p.ContactId == contactId)
            .OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivePackage>> ActiveForContactAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var now = time.GetUtcNow();
        var active = await Db.CustomerPackages.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ContactId == contactId && p.Status == CustomerPackageStatus.Active && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderBy(p => p.ExpiresAt ?? DateTimeOffset.MaxValue).ThenBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var packageIds = active.Select(p => p.CatalogItemId).Distinct().ToList();
        var contents = await Db.PackageContents.AsNoTracking().Where(c => c.TenantId == tenantId && packageIds.Contains(c.PackageId)).ToListAsync(cancellationToken);
        return active.Where(p => p.Remaining > 0)
            .Select(p => new ActivePackage(p, contents.Where(c => c.PackageId == p.CatalogItemId).Select(c => c.ServiceId).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<PackageRedemption>> RedemptionsAsync(Guid tenantId, Guid packageId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.PackageRedemptions.AsNoTracking().Where(r => r.TenantId == tenantId && r.CustomerPackageId == packageId).OrderByDescending(r => r.At).ToListAsync(cancellationToken);
    }

    /// <summary>Cấp gói khi hóa đơn thu đủ (gọi trong transaction của hóa đơn).</summary>
    internal async Task<CustomerPackage> GrantAsync(Guid tenantId, Guid contactId, CatalogItem item, Guid invoiceId, long pricePaid, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var sessions = item.SessionCount ?? await Db.PackageContents.Where(c => c.TenantId == tenantId && c.PackageId == item.Id).SumAsync(c => c.SessionCount, cancellationToken);
        if (sessions <= 0)
        {
            throw new InvalidInputException($"Gói {item.Name} chưa có số buổi — cập nhật ở Danh mục.");
        }

        var package = new CustomerPackage
        {
            TenantId = tenantId,
            ContactId = contactId,
            CatalogItemId = item.Id,
            Name = item.Name,
            InvoiceId = invoiceId,
            TotalSessions = sessions,
            PricePaid = pricePaid,
            ExpiresAt = item.ValidityDays is { } days ? time.GetUtcNow().AddDays(days) : null,
        };
        Db.CustomerPackages.Add(package);
        return package;
    }

    /// <summary>Trừ một buổi (trong transaction của hóa đơn hoặc khi hoàn thành lịch): ghi lượt dùng + doanh thu gói.</summary>
    internal async Task<PackageRedemption> RedeemAsync(Guid tenantId, Guid packageId, Guid? appointmentId, Guid? invoiceLineId, Guid? staffMemberId, Guid branchId, string? source, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var package = await Db.CustomerPackages.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == packageId, cancellationToken) ?? throw new NotFoundException();
        if (package.Status != CustomerPackageStatus.Active || package.Remaining <= 0)
        {
            throw new ConflictException("Gói hết buổi hoặc không còn hiệu lực.", "package_exhausted");
        }

        var now = time.GetUtcNow();
        package.UsedSessions++;
        if (package.Remaining == 0)
        {
            package.Status = CustomerPackageStatus.Exhausted;
        }

        var redemption = new PackageRedemption
        {
            TenantId = tenantId,
            CustomerPackageId = package.Id,
            AppointmentId = appointmentId,
            InvoiceLineId = invoiceLineId,
            StaffMemberId = staffMemberId,
            ByUserId = _caller.UserId,
            At = now,
        };
        Db.PackageRedemptions.Add(redemption);

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var perSession = package.TotalSessions > 0 ? package.PricePaid / package.TotalSessions : 0;
        if (staffMemberId is { } performer)
        {
            await commissions.AccrueAsync(tenantId, performer, CatalogKind.Package, package.CatalogItemId, perSession, package.InvoiceId, invoiceLineId, redemption.Id, cancellationToken);
        }

        Db.RevenueEntries.Add(new RevenueEntry
        {
            TenantId = tenantId,
            BranchId = branchId,
            Date = CrmClock.LocalDate(now, CrmClock.Zone(settings.TimeZone)),
            Kind = RevenueKind.PackageRecognized,
            Amount = perSession,
            InvoiceId = package.InvoiceId,
            RedemptionId = redemption.Id,
            CatalogItemId = package.CatalogItemId,
            StaffMemberId = staffMemberId,
            ContactId = package.ContactId,
            Source = source,
            At = now,
        });
        return redemption;
    }

    /// <summary>Hủy hóa đơn: hoàn các buổi đã trừ bởi hóa đơn đó; gói do hóa đơn đó cấp mà chưa dùng → hủy.</summary>
    internal async Task RevertForInvoiceAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var now = time.GetUtcNow();
        var lineIds = await Db.InvoiceLines.Where(l => l.TenantId == tenantId && l.InvoiceId == invoiceId).Select(l => l.Id).ToListAsync(cancellationToken);
        var redemptions = await Db.PackageRedemptions
            .Where(r => r.TenantId == tenantId && r.InvoiceLineId != null && lineIds.Contains(r.InvoiceLineId.Value) && r.RevertedAt == null)
            .ToListAsync(cancellationToken);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var date = CrmClock.LocalDate(now, CrmClock.Zone(settings.TimeZone));
        foreach (var r in redemptions)
        {
            r.RevertedAt = now;
            var package = await Db.CustomerPackages.FirstAsync(p => p.Id == r.CustomerPackageId, cancellationToken);
            package.UsedSessions = Math.Max(0, package.UsedSessions - 1);
            // Doanh thu gói đã ghi cho buổi này → ghi âm lại (giữ dòng cũ để đối soát).
            var recognized = await Db.RevenueEntries.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.RedemptionId == r.Id && e.Kind == RevenueKind.PackageRecognized, cancellationToken);
            if (recognized is not null)
            {
                Db.RevenueEntries.Add(new RevenueEntry
                {
                    TenantId = tenantId,
                    BranchId = recognized.BranchId,
                    Date = date,
                    Kind = RevenueKind.Refund,
                    Amount = -recognized.Amount,
                    InvoiceId = recognized.InvoiceId,
                    RedemptionId = r.Id,
                    CatalogItemId = recognized.CatalogItemId,
                    StaffMemberId = recognized.StaffMemberId,
                    ContactId = recognized.ContactId,
                    Source = recognized.Source,
                    At = now,
                });
            }
            if (package.Status == CustomerPackageStatus.Exhausted)
            {
                package.Status = CustomerPackageStatus.Active;
            }
        }

        var granted = await Db.CustomerPackages.Where(p => p.TenantId == tenantId && p.InvoiceId == invoiceId).ToListAsync(cancellationToken);
        foreach (var package in granted)
        {
            if (package.UsedSessions > 0)
            {
                throw new ConflictException($"Gói {package.Name} đã dùng {package.UsedSessions} buổi — không hủy hóa đơn được, hãy hoàn tiền một phần.", "package_used");
            }

            package.Status = CustomerPackageStatus.Cancelled;
        }
    }

    /// <summary>Hoàn thành lịch mà không có hóa đơn: tự trừ buổi gói cho các dịch vụ gói còn bao, trả về số buổi đã trừ.</summary>
    public async Task<int> AutoRedeemForAppointmentAsync(Guid tenantId, Appointment appointment, string? source, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(appointment);
        if (await Db.PackageRedemptions.AnyAsync(r => r.TenantId == tenantId && r.AppointmentId == appointment.Id && r.RevertedAt == null, cancellationToken))
        {
            return 0;
        }

        var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppointmentId == appointment.Id).ToListAsync(cancellationToken);
        var active = await ActiveForContactAsync(tenantId, appointment.ContactId, cancellationToken);
        var used = new Dictionary<Guid, int>();
        var count = 0;
        foreach (var item in items)
        {
            var covering = active.FirstOrDefault(p => p.ServiceIds.Contains(item.CatalogItemId) && p.Package.Remaining - used.GetValueOrDefault(p.Package.Id) > 0);
            if (covering is null)
            {
                continue;
            }

            await RedeemAsync(tenantId, covering.Package.Id, appointment.Id, null, appointment.StaffMemberId, appointment.BranchId, source, cancellationToken);
            used[covering.Package.Id] = used.GetValueOrDefault(covering.Package.Id) + 1;
            count++;
        }

        return count;
    }

    public async Task<CustomerPackage> SetStatusAsync(Guid tenantId, Guid packageId, CustomerPackageStatus status, CancellationToken cancellationToken)
    {
        var package = await RequireAsync(tenantId, packageId, cancellationToken);
        if (status is not (CustomerPackageStatus.Active or CustomerPackageStatus.Paused or CustomerPackageStatus.Cancelled))
        {
            throw new InvalidInputException("Chỉ bảo lưu, kích hoạt lại hoặc hủy gói.");
        }

        if (package.Status is CustomerPackageStatus.Transferred or CustomerPackageStatus.Exhausted)
        {
            throw new ConflictException("Gói đã chuyển nhượng hoặc đã dùng hết.", "package_state");
        }

        package.Status = status;
        Audit(tenantId, "package.status_changed", $"{package.Id}:{status.ToString().ToLowerInvariant()}");
        await Db.SaveChangesAsync(cancellationToken);
        return package;
    }

    /// <summary>Gia hạn / sửa số buổi (chủ hoặc quyền quản lý danh mục).</summary>
    public async Task<CustomerPackage> AdjustAsync(Guid tenantId, Guid packageId, int? totalSessions, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var package = await RequireAsync(tenantId, packageId, cancellationToken);
        if (totalSessions is { } total)
        {
            if (total < package.UsedSessions)
            {
                throw new InvalidInputException("Tổng buổi không được nhỏ hơn số đã dùng.");
            }

            package.TotalSessions = total;
            if (package.Status == CustomerPackageStatus.Exhausted && package.Remaining > 0)
            {
                package.Status = CustomerPackageStatus.Active;
            }
        }

        package.ExpiresAt = expiresAt?.ToUniversalTime();
        Audit(tenantId, "package.adjusted", $"{package.Id}:{package.TotalSessions}:{package.ExpiresAt:O}");
        await Db.SaveChangesAsync(cancellationToken);
        return package;
    }

    /// <summary>Chuyển phần còn lại cho khách khác (người thân): gói cũ thành Transferred, gói mới có số buổi còn lại.</summary>
    public async Task<CustomerPackage> TransferAsync(Guid tenantId, Guid packageId, Guid toContactId, CancellationToken cancellationToken)
    {
        var package = await RequireAsync(tenantId, packageId, cancellationToken);
        if (package.Status != CustomerPackageStatus.Active || package.Remaining == 0)
        {
            throw new ConflictException("Chỉ chuyển gói đang hiệu lực còn buổi.", "package_state");
        }

        if (!await Db.Contacts.AnyAsync(c => c.TenantId == tenantId && c.Id == toContactId, cancellationToken) || toContactId == package.ContactId)
        {
            throw new NotFoundException();
        }

        var remaining = package.Remaining;
        var moved = new CustomerPackage
        {
            TenantId = tenantId,
            ContactId = toContactId,
            CatalogItemId = package.CatalogItemId,
            Name = package.Name,
            InvoiceId = package.InvoiceId,
            TotalSessions = remaining,
            PricePaid = package.TotalSessions > 0 ? package.PricePaid * remaining / package.TotalSessions : 0,
            ExpiresAt = package.ExpiresAt,
            TransferredFromPackageId = package.Id,
        };
        package.Status = CustomerPackageStatus.Transferred;
        package.TransferredToContactId = toContactId;
        Db.CustomerPackages.Add(moved);
        Audit(tenantId, "package.transferred", $"{package.Id}->{moved.Id}");
        await Db.SaveChangesAsync(cancellationToken);
        return moved;
    }

    /// <summary>Job: gói quá hạn → Expired (giữ số buổi để đối chiếu).</summary>
    public async Task<int> ExpireAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var now = time.GetUtcNow();
        return await Db.CustomerPackages
            .Where(p => p.TenantId == tenantId && p.Status == CustomerPackageStatus.Active && p.ExpiresAt != null && p.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, CustomerPackageStatus.Expired).SetProperty(p => p.UpdatedAt, now), cancellationToken);
    }

    private async Task<CustomerPackage> RequireAsync(Guid tenantId, Guid packageId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CustomerPackages.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == packageId, cancellationToken) ?? throw new NotFoundException();
    }

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
