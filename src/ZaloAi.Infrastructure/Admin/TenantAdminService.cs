using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Core.Storage;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Security;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Admin;

/// <param name="TemporaryPassword">Mật khẩu tạm của tài khoản chủ MỚI (chỉ trả một lần, không lưu dạng đọc được); null khi email đã có tài khoản.</param>
public sealed record CreatedTenant(Guid TenantId, string OwnerEmail, string? TemporaryPassword);

public sealed record NewTenant(string Name, string IndustrySlug, string? BotName, string OwnerEmail, string OwnerName, string Plan, DateTimeOffset? ExpiresAt, int? ConversationQuota);

/// <summary>
/// Thao tác của super admin lên MỘT doanh nghiệp (CLAUDE.md M6 đợt B3). Mỗi thao tác chạy trong một DI scope RIÊNG có tenant context
/// đặt đúng DN đó (<see cref="InTenantAsync"/>; TenantContext không cho đổi tenant giữa chừng một scope) — nên mọi truy vấn vẫn đi
/// qua global query filter và kiểm tra tenant khi SaveChanges như code thường, không dùng IgnoreQueryFilters (trừ một chỗ ghi rõ ở
/// <see cref="PurgeAsync"/>). Mọi thao tác ghi audit log.
/// </summary>
public sealed partial class TenantAdminService(
    IServiceScopeFactory scopes,
    ITenantContext caller,
    IPasswordHasher<User> hasher,
    IFieldEncryptor encryptor,
    IFileStorage storage,
    IOptions<BillingOptions> billing,
    TimeProvider time,
    ILogger<TenantAdminService> logger)
{
    /// <summary>Tạo DN + tài khoản chủ (email đã có tài khoản → thêm làm chủ, không đổi mật khẩu). Mã ngành đã được API kiểm.</summary>
    public async Task<CreatedTenant> CreateAsync(NewTenant request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var adminId = RequireSuperAdmin();
        ValidatePlan(request.Plan);
        var tenantId = Guid.CreateVersion7();
        return await InTenantAsync(tenantId, adminId, async db =>
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = request.Name.Trim(),
                IndustrySlug = request.IndustrySlug,
                BotName = string.IsNullOrWhiteSpace(request.BotName) ? "Trợ lý" : request.BotName.Trim(),
                BotPronoun = "em",
                Plan = request.Plan,
                ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
                ConversationQuota = request.ConversationQuota,
            });

            var email = User.NormalizeEmail(request.OwnerEmail);
            var owner = await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            string? temporaryPassword = null;
            if (owner is null)
            {
                temporaryPassword = Passwords.NewTemporary();
                owner = new User { Id = Guid.CreateVersion7(), Email = email, Name = request.OwnerName.Trim(), PasswordHash = "" };
                owner.PasswordHash = hasher.HashPassword(owner, temporaryPassword);
                db.Users.Add(owner);
            }

            db.Memberships.Add(new Membership { TenantId = tenantId, UserId = owner.Id, Role = TenantRole.Owner });
            Audit(db, tenantId, adminId, "tenant.created", $"owner:{owner.Id}");
            await db.SaveChangesAsync(cancellationToken);
            return new CreatedTenant(tenantId, email, temporaryPassword);
        });
    }

    /// <summary>Đổi gói, hạn thuê, hạn mức riêng. DN đang tự khóa vì quá hạn mà được gia hạn qua hôm nay → tự mở lại.</summary>
    public async Task UpdateSubscriptionAsync(Guid tenantId, string plan, DateTimeOffset? expiresAt, int? conversationQuota, CancellationToken cancellationToken)
    {
        var adminId = RequireSuperAdmin();
        ValidatePlan(plan);
        await InTenantAsync(tenantId, adminId, async db =>
        {
            var tenant = await LoadAsync(db, tenantId, cancellationToken);
            tenant.Plan = plan;
            tenant.ExpiresAt = expiresAt?.ToUniversalTime();
            tenant.ConversationQuota = conversationQuota;
            if (tenant.Status == TenantStatus.Suspended && tenant.SuspendedReason == Tenant.SuspendExpired
                && (tenant.ExpiresAt is null || tenant.ExpiresAt > time.GetUtcNow()))
            {
                tenant.Status = TenantStatus.Active;
                tenant.SuspendedReason = null;
            }

            var expiry = tenant.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none";
            Audit(db, tenantId, adminId, "tenant.subscription_changed", $"{plan}:{expiry}:{conversationQuota}");
            return await db.SaveChangesAsync(cancellationToken);
        });
    }

    /// <summary>Khóa: người của DN không vào được web, bot ngừng trả lời, ngừng chăm sóc chủ động (dữ liệu vẫn giữ).</summary>
    public Task SuspendAsync(Guid tenantId, CancellationToken cancellationToken) =>
        SetStatusAsync(tenantId, TenantStatus.Suspended, Tenant.SuspendManual, "tenant.suspended", cancellationToken);

    public Task ResumeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        SetStatusAsync(tenantId, TenantStatus.Active, null, "tenant.resumed", cancellationToken);

    /// <summary>Job hệ thống: DN quá hạn thuê hơn <see cref="BillingOptions.SuspendAfterExpiryDays"/> ngày → tự khóa. Trả số DN đã khóa.</summary>
    public async Task<int> SuspendExpiredAsync(IReadOnlyList<Guid> tenantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        RequireSuperAdmin();
        var cutoff = time.GetUtcNow().AddDays(-billing.Value.SuspendAfterExpiryDays);
        var count = 0;
        foreach (var tenantId in tenantIds)
        {
            count += await InTenantAsync(tenantId, null, async db =>
            {
                var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
                if (tenant is not { Status: TenantStatus.Active, ExpiresAt: { } expires } || expires >= cutoff)
                {
                    return 0;
                }

                tenant.Status = TenantStatus.Suspended;
                tenant.SuspendedReason = Tenant.SuspendExpired;
                Audit(db, tenantId, null, "tenant.suspended", Tenant.SuspendExpired);
                await db.SaveChangesAsync(cancellationToken);
                return 1;
            });
        }

        return count;
    }

    /// <summary>
    /// File zip toàn bộ dữ liệu DN (gửi cho DN khi ngừng thuê): khach-hang.xlsx (khách, tin nhắn, nhật ký chăm sóc),
    /// kho-kien-thuc.xlsx (đúng mẫu để nhập lại), tai-lieu/ (file gốc đã tải lên). Ghi thẳng ra <paramref name="output"/>.
    /// </summary>
    public async Task ExportAsync(Guid tenantId, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        var adminId = RequireSuperAdmin();
        await InTenantAsync(tenantId, adminId, async db =>
        {
            await LoadAsync(db, tenantId, cancellationToken);
            Audit(db, tenantId, adminId, "tenant.exported");
            await db.SaveChangesAsync(cancellationToken);

            // Zip ghi đồng bộ khi đóng → dựng trong file tạm (tự xóa khi đóng), rồi chép bất đồng bộ ra response.
            await using var temp = new FileStream(
                Path.Combine(Path.GetTempPath(), $"zaloai-export-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await BuildZipAsync(db, tenantId, temp, cancellationToken);
            temp.Position = 0;
            await temp.CopyToAsync(output, cancellationToken);
            return 0;
        });
    }

    private async Task BuildZipAsync(AppDbContext db, Guid tenantId, Stream target, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        await WriteEntryAsync(zip, "khach-hang.xlsx", s => WriteCustomersAsync(db, tenantId, s, cancellationToken), cancellationToken);
        var items = await db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId).ToListAsync(cancellationToken);
        await WriteEntryAsync(zip, "kho-kien-thuc.xlsx", s =>
        {
            KnowledgeExcel.Write(items.Select(KnowledgeItemMapper.FromItem).ToList(), s);
            return Task.CompletedTask;
        }, cancellationToken);

        var documents = await db.Documents.AsNoTracking().Where(d => d.TenantId == tenantId).ToListAsync(cancellationToken);
        foreach (var document in documents)
        {
            try
            {
                await using var file = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
                await WriteEntryAsync(zip, $"tai-lieu/{document.Id:N}-{SafeFileName(document.FileName)}", s => file.CopyToAsync(s, cancellationToken), cancellationToken);
            }
            catch (FileNotFoundException)
            {
                LogMissingFile(logger, document.Id);
            }
        }
    }

    /// <summary>
    /// Xóa VĨNH VIỄN một DN (ngừng thuê): phải đang khóa và gõ đúng tên DN. Xóa dòng tenants → khóa ngoại ON DELETE CASCADE xóa mọi
    /// bảng có tenant_id (khách, tin nhắn, kho kiến thức, kết nối kênh, chi phí, audit log của DN...); xóa file đã tải lên; xóa tài
    /// khoản chỉ thuộc DN này. Ghi một dòng audit hệ thống (tenant_id rỗng) làm bằng chứng đã xóa.
    /// </summary>
    public async Task PurgeAsync(Guid tenantId, string confirmName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmName);
        var adminId = RequireSuperAdmin();
        var (name, keys) = await InTenantAsync(tenantId, adminId, async db =>
        {
            var tenant = await LoadAsync(db, tenantId, cancellationToken);
            if (tenant.Status == TenantStatus.Active)
            {
                throw new ConflictException("Khóa doanh nghiệp trước khi xóa dữ liệu.", "tenant_active");
            }

            if (!string.Equals(confirmName.Trim(), tenant.Name.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidInputException("Tên xác nhận không khớp tên doanh nghiệp.");
            }

            var storageKeys = await db.Documents.Where(d => d.TenantId == tenantId).Select(d => d.StorageKey).ToListAsync(cancellationToken);
            var memberIds = await db.Memberships.Where(m => m.TenantId == tenantId).Select(m => m.UserId).ToListAsync(cancellationToken);
            await db.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync(cancellationToken);

            // Tài khoản không còn thuộc DN nào (và không phải super admin) → xóa. Đếm membership XUYÊN tenant có chủ đích:
            // chỉ để biết user còn thuộc DN khác hay không, không đọc dữ liệu nghiệp vụ.
            await db.Users
                .Where(u => memberIds.Contains(u.Id) && !u.IsSuperAdmin && !db.Memberships.IgnoreQueryFilters().Any(m => m.UserId == u.Id))
                .ExecuteDeleteAsync(cancellationToken);
            return (tenant.Name, storageKeys);
        });

        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(key, cancellationToken);
            }
            catch (IOException ex)
            {
                LogFileDeleteFailed(logger, ex.GetType().Name);
            }
        }

        await InTenantAsync(null, adminId, db =>
        {
            db.AuditLogs.Add(new AuditLog { TenantId = null, UserId = adminId, Action = "tenant.deleted", Target = $"{tenantId}:{name}" });
            return db.SaveChangesAsync(cancellationToken);
        });
    }

    private async Task SetStatusAsync(Guid tenantId, TenantStatus status, string? reason, string action, CancellationToken cancellationToken)
    {
        var adminId = RequireSuperAdmin();
        await InTenantAsync(tenantId, adminId, async db =>
        {
            var tenant = await LoadAsync(db, tenantId, cancellationToken);
            tenant.Status = status;
            tenant.SuspendedReason = reason;
            Audit(db, tenantId, adminId, action, reason);
            return await db.SaveChangesAsync(cancellationToken);
        });
    }

    private async Task WriteCustomersAsync(AppDbContext db, Guid tenantId, Stream output, CancellationToken cancellationToken)
    {
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
        var names = contacts.ToDictionary(c => c.Id, c => ContactView.Name(c, ContactView.LeadFields(c, encryptor)));
        var messages = await (
            from m in db.Messages.AsNoTracking()
            join c in db.Conversations on m.ConversationId equals c.Id
            where m.TenantId == tenantId && c.TenantId == tenantId
            orderby m.CreatedAt, m.Id
            select new { c.ContactId, c.IsTest, m.Sender, m.ContentEnc, m.CreatedAt }).ToListAsync(cancellationToken);
        var notes = await db.ContactNotes.AsNoTracking().Where(n => n.TenantId == tenantId).OrderBy(n => n.HappenedOn).ToListAsync(cancellationToken);

        ContactDataExcel.Write(
            [
                new ExcelSheet(
                    "Khách hàng",
                    ["Tên", "Kênh", "Mã người dùng trên kênh", "Mức độ tiềm năng", "Nhãn", "Thông tin khách cung cấp", "Ngày bắt đầu"],
                    contacts.Select(c => (IReadOnlyList<string>)
                    [
                        names[c.Id],
                        c.Channel.ToString(),
                        c.ExternalUserId,
                        ContactView.StatusLabel(c.LeadStatus),
                        string.Join(", ", c.Tags),
                        JsonSerializer.Serialize(ContactView.LeadFields(c, encryptor)),
                        ContactDataExcel.Format(c.CreatedAt),
                    ]).ToList()),
                new ExcelSheet(
                    "Tin nhắn",
                    ["Thời gian", "Khách", "Chat thử", "Người gửi", "Nội dung"],
                    messages.Select(m => (IReadOnlyList<string>)
                    [
                        ContactDataExcel.Format(m.CreatedAt),
                        names.GetValueOrDefault(m.ContactId) ?? "",
                        m.IsTest ? "Có" : "",
                        m.Sender.ToString(),
                        encryptor.Decrypt(m.ContentEnc),
                    ]).ToList()),
                new ExcelSheet(
                    "Nhật ký chăm sóc",
                    ["Khách", "Ngày", "Loại", "Nội dung", "Chăm sóc lại lúc"],
                    notes.Select(n => (IReadOnlyList<string>)
                    [
                        names.GetValueOrDefault(n.ContactId) ?? "",
                        n.HappenedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                        n.Kind.ToString(),
                        encryptor.Decrypt(n.ContentEnc),
                        ContactDataExcel.Format(n.FollowUpAt),
                    ]).ToList()),
            ],
            output);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();

        // ClosedXML cần stream đọc/ghi được → ghi ra bộ nhớ trước rồi chép vào zip.
        using var buffer = new MemoryStream();
        await write(buffer);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
    }

    private static async Task<Tenant> LoadAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw new NotFoundException();

    /// <summary>Chạy <paramref name="work"/> trong scope riêng có tenant context = DN đó (giữ quyền super admin); null = không thuộc DN nào.</summary>
    private async Task<T> InTenantAsync<T>(Guid? tenantId, Guid? adminId, Func<AppDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Set(tenantId, adminId, tenantId is null ? null : TenantRole.Owner, isSuperAdmin: true);
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Guid? RequireSuperAdmin()
    {
        if (!caller.IsSuperAdmin)
        {
            throw new ForbiddenException();
        }

        return caller.UserId;
    }

    private static void Audit(AppDbContext db, Guid tenantId, Guid? userId, string action, string? target = null) =>
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = userId, Action = action, Target = target });

    private void ValidatePlan(string plan)
    {
        if (!billing.Value.Plans.ContainsKey(plan))
        {
            throw new InvalidInputException("Gói không có trong danh sách.");
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(Path.GetFileName(name).Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Xuất dữ liệu DN: không thấy file của tài liệu {DocumentId}")]
    private static partial void LogMissingFile(ILogger logger, Guid documentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Xóa DN: không xóa được một file đã tải lên ({ErrorType})")]
    private static partial void LogFileDeleteFailed(ILogger logger, string errorType);
}
