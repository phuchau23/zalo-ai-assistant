using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Persistence.Seed;

/// <summary>
/// Dữ liệu ẢO cho một doanh nghiệp demo riêng ("Spa Thảo Mộc An Nhiên") — để trang quản trị, báo cáo, hộp thư có số liệu đẹp
/// khi demo hoặc chụp màn hình. KHÔNG đụng doanh nghiệp khác. Chạy lại = xóa sạch doanh nghiệp demo rồi tạo lại (số liệu như nhau
/// vì sinh ngẫu nhiên có hạt giống cố định, mốc thời gian theo "hôm nay"). Chỉ chạy ở Development.
///
/// Có: 2 chi nhánh, 15 mục danh mục (dịch vụ/gói/sản phẩm), 6 nhân viên + ca làm + chấm công 35 ngày, ~220 khách,
/// lịch hẹn 60 ngày qua + 7 ngày tới, hóa đơn + thanh toán + doanh thu ghi nhận, gói liệu trình + trừ buổi, hoa hồng,
/// hội thoại Zalo với bot (có ca chuyển nhân viên, ca chờ người), chi phí AI, gợi ý chăm sóc, ghi chú khách.
/// Ghi thẳng bằng AppDbContext (cần lùi ngày) nhưng vẫn đặt tenant trước khi ghi — đi qua đúng luật cô lập dữ liệu.
/// </summary>
public static class DemoSeeder
{
    public static readonly Guid TenantId = Guid.Parse("0199a000-0000-7000-8000-0000000000d1");
    public const string TenantName = "Spa Thảo Mộc An Nhiên";
    public const string OwnerEmail = "demo@remi.local";
    public const string StaffEmail = "lan@remi.local";

    private static readonly TimeSpan Vn = TimeSpan.FromHours(7);
    private const int HistoryDays = 60;
    private const int FutureDays = 7;
    private const string Model = "gemini-3.5-flash-lite";

    private static readonly string[] Surnames = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý"];
    private static readonly string[] FemaleMiddle = ["Thị", "Ngọc", "Thu", "Thanh", "Phương", "Kim", "Hồng", "Bảo", "Mỹ"];
    private static readonly string[] MaleMiddle = ["Văn", "Minh", "Hoàng", "Gia", "Quốc", "Đức", "Anh"];
    private static readonly string[] FemaleGiven = ["Anh", "Trang", "Lan", "Linh", "Hương", "Mai", "Ngân", "Thảo", "Vy", "Hà", "Nhung", "Hạnh", "Yến", "Trâm", "Chi", "My", "Nhi", "Uyên", "Tâm", "Diệp"];
    private static readonly string[] MaleGiven = ["Tuấn", "Huy", "Nam", "Khoa", "Phúc", "Duy", "Quân", "Hiếu", "Long", "Đức", "Thắng", "Bảo", "Khang"];

    private sealed record ServiceDef(string Code, string Name, string Group, long Price, int Minutes, int Weight);
    private sealed record PackageDef(string Code, string Name, long Price, int Sessions, string ServiceCode);
    private sealed record ProductDef(string Code, string Name, long Price);
    private sealed record StaffDef(string Name, string Title, string Color, int Branch, int StartHour, DayOfWeek DayOff);

    private static readonly ServiceDef[] Services =
    [
        new("DV01", "Gội đầu dưỡng sinh 45 phút", "Thư giãn", 180_000, 45, 14),
        new("DV02", "Bấm huyệt cổ vai gáy 60 phút", "Trị liệu", 350_000, 60, 22),
        new("DV03", "Massage đá nóng 90 phút", "Thư giãn", 550_000, 90, 15),
        new("DV04", "Xông hơi thảo dược 30 phút", "Thư giãn", 150_000, 30, 8),
        new("DV05", "Chăm sóc da mặt thảo mộc", "Da mặt", 420_000, 60, 12),
        new("DV06", "Châm cứu giảm đau lưng", "Trị liệu", 300_000, 45, 9),
        new("DV07", "Massage chân thảo dược 60 phút", "Thư giãn", 280_000, 60, 12),
        new("DV08", "Tắm trắng thảo mộc", "Da mặt", 650_000, 75, 6),
    ];

    private static readonly PackageDef[] Packages =
    [
        new("G01", "Gói 10 buổi cổ vai gáy", 3_000_000, 10, "DV02"),
        new("G02", "Gói 5 buổi chăm sóc da", 1_900_000, 5, "DV05"),
        new("G03", "Gói 8 buổi massage đá nóng", 4_000_000, 8, "DV03"),
    ];

    private static readonly ProductDef[] Products =
    [
        new("SP01", "Tinh dầu ngải cứu", 220_000),
        new("SP02", "Muối thảo dược ngâm chân", 120_000),
        new("SP03", "Kem dưỡng thảo mộc", 380_000),
        new("SP04", "Trà an thần hoa cúc", 90_000),
    ];

    private static readonly StaffDef[] Staff =
    [
        new("Nguyễn Thị Lan", "Trưởng nhóm kỹ thuật viên", "#16a34a", 0, 9, DayOfWeek.Monday),
        new("Trần Minh Thư", "Kỹ thuật viên", "#2563eb", 0, 10, DayOfWeek.Tuesday),
        new("Lê Hoàng Mai", "Kỹ thuật viên", "#db2777", 0, 11, DayOfWeek.Wednesday),
        new("Phạm Quốc Huy", "Kỹ thuật viên", "#ea580c", 0, 9, DayOfWeek.Thursday),
        new("Võ Ngọc Hân", "Kỹ thuật viên", "#7c3aed", 1, 10, DayOfWeek.Monday),
        new("Đặng Thảo Vy", "Kỹ thuật viên", "#0891b2", 1, 11, DayOfWeek.Friday),
    ];

    /// <summary>Câu khách hỏi + bot trả lời (trả lời được).</summary>
    private static readonly (string Ask, string Answer)[] Chats =
    [
        ("Gói cổ vai gáy 60 phút giá bao nhiêu em?", "Dạ bấm huyệt cổ vai gáy 60 phút bên em là 350.000đ một lượt ạ. Mình muốn em giữ giờ nào ạ?"),
        ("Massage đá nóng làm bao lâu vậy em?", "Dạ massage đá nóng 90 phút ạ, giá 550.000đ. Mình đi một mình hay đi cùng bạn ạ?"),
        ("Chiều nay còn chỗ không em?", "Dạ chiều nay bên em còn trống lúc 15:30 và 17:00 ạ. Mình chọn giờ nào em giữ chỗ nhé?"),
        ("Spa có chỗ đậu xe ô tô không em?", "Dạ có ạ, bên em có bãi xe ngay cạnh tiệm, mình báo lễ tân để được hướng dẫn nhé."),
        ("Gói 10 buổi cổ vai gáy có hạn dùng không em?", "Dạ gói 10 buổi giá 3.000.000đ, dùng trong 6 tháng ạ. Tiết kiệm 500.000đ so với làm lẻ."),
        ("Chăm sóc da mặt có nặn mụn không em?", "Dạ có ạ, liệu trình 60 phút gồm làm sạch, nặn mụn nhẹ và đắp mặt nạ thảo mộc, giá 420.000đ."),
        ("Cảm ơn em nhé, hôm qua làm thích lắm", "Dạ em cảm ơn mình nhiều ạ! Hẹn gặp lại mình buổi tới nha 🌿"),
        ("Bên mình mở cửa đến mấy giờ vậy em?", "Dạ bên em mở cửa từ 8:00 đến 20:00 tất cả các ngày trong tuần ạ."),
    ];

    /// <summary>Câu bot KHÔNG trả lời được (không có trong dữ liệu) → chuyển nhân viên.</summary>
    private static readonly string[] UnknownAsks =
    [
        "Bên mình có gói massage cho mẹ bầu không em?",
        "Có xuất hóa đơn VAT cho công ty không em?",
        "Spa có nhận thẻ thành viên của chuỗi khác không?",
        "Làm triệt lông vĩnh viễn giá sao em?",
    ];

    private static readonly string[] HandoffReasons = ["no_knowledge", "customer_request", "low_confidence", "negative_sentiment"];

    /// <summary>Tạo lại doanh nghiệp demo. Trả về đoạn tóm tắt để in ra màn hình.</summary>
    public static async Task<string> RunAsync(IServiceProvider services, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Lệnh seed-demo chỉ chạy ở môi trường Development.");
        }

        // Migration + super admin + 2 doanh nghiệp mẫu (không đổi gì nếu đã có).
        await DevSeeder.RunAsync(services, environment, cancellationToken);
        await ResetAsync(services, cancellationToken);
        return await SeedAsync(services, cancellationToken);
    }

    /// <summary>
    /// Xóa hẳn dữ liệu demo: doanh nghiệp demo + 2 tài khoản demo (chỉ khi tài khoản không thuộc doanh nghiệp nào khác).
    /// Không đụng doanh nghiệp/tài khoản khác. Trả về câu báo để in ra màn hình.
    /// </summary>
    public static async Task<string> ClearAsync(IServiceProvider services, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Lệnh clear-demo chỉ chạy ở môi trường Development.");
        }

        var tenantRemoved = await ResetAsync(services, cancellationToken);

        // Bảng users không có tenant_id: chỉ xóa đúng 2 email demo, và chỉ khi không còn membership nào (sau khi xóa tenant,
        // membership demo đã xóa dây chuyền). Mọi khóa ngoại trỏ tới users là SET NULL/CASCADE nên xóa an toàn.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string[] emails = [OwnerEmail, StaffEmail];
        var usersRemoved = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM users u WHERE u.email = ANY({emails}) AND NOT EXISTS (SELECT 1 FROM memberships m WHERE m.user_id = u.id)",
            cancellationToken);

        return tenantRemoved == 0 && usersRemoved == 0
            ? "Không có dữ liệu demo nào để xóa."
            : $"Đã xóa doanh nghiệp demo \"{TenantName}\" và {usersRemoved} tài khoản demo ({OwnerEmail}, {StaffEmail}).";
    }

    /// <summary>
    /// Xóa sạch doanh nghiệp demo. SQL thô CÓ điều kiện tenant_id (CLAUDE.md §8): lệnh dev, chỉ xóa đúng doanh nghiệp demo.
    /// Xóa trước các bảng có khóa ngoại RESTRICT (hóa đơn, gói, lịch hẹn, nhân viên), rồi xóa tenant → các bảng khác xóa dây chuyền.
    /// Trả về số doanh nghiệp đã xóa (0 = chưa có demo).
    /// </summary>
    private static async Task<int> ResetAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = TenantId;
        await db.Database.ExecuteSqlAsync($"DELETE FROM invoices WHERE tenant_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM customer_packages WHERE tenant_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM appointments WHERE tenant_id = {id}", cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM staff_members WHERE tenant_id = {id}", cancellationToken);
        return await db.Database.ExecuteSqlAsync($"DELETE FROM tenants WHERE id = {id}", cancellationToken);
    }

#pragma warning disable CA5394 // Random không dùng cho bảo mật: chỉ để sinh dữ liệu ảo lặp lại được.
    private static async Task<string> SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<TenantContext>().Set(TenantId, null, null, isSuperAdmin: false);
        var db = sp.GetRequiredService<AppDbContext>();
        var encryptor = sp.GetRequiredService<IFieldEncryptor>();
        var phoneHasher = sp.GetRequiredService<IPhoneHasher>();
        var hasher = sp.GetRequiredService<IPasswordHasher<User>>();
        var users = sp.GetRequiredService<UserRepository>();
        var memberships = sp.GetRequiredService<MembershipRepository>();

        var rnd = new Random(20261106);
        var now = DateTimeOffset.UtcNow;
        var today = LocalDate(now);
        var nowLocalMinutes = (int)now.ToOffset(Vn).TimeOfDay.TotalMinutes;

        // ---------- Doanh nghiệp, tài khoản ----------
        db.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = TenantName,
            IndustrySlug = "spa",
            BotName = "An Nhiên",
            BotPronoun = "em",
            PrivacyUrl = "https://remi.vn/chinh-sach-bao-mat",
            ConversationQuota = 100_000,
            CreatedAt = now.AddDays(-150),
        });
        var passwordHash = hasher.HashPassword(new User { Email = "", PasswordHash = "", Name = "" }, DevSeeder.DevPassword);
        var owner = await users.FindByEmailAsync(OwnerEmail, cancellationToken) ?? users.Add(OwnerEmail, passwordHash, "Chủ An Nhiên");
        var staffUser = await users.FindByEmailAsync(StaffEmail, cancellationToken) ?? users.Add(StaffEmail, passwordHash, "Nguyễn Thị Lan");
        memberships.Add(TenantId, owner.Id, TenantRole.Owner);
        memberships.Add(TenantId, staffUser.Id, TenantRole.Staff);
        await db.SaveChangesAsync(cancellationToken);

        // ---------- Chi nhánh, giờ mở cửa ----------
        var branches = new[]
        {
            new Branch { TenantId = TenantId, Code = "CN1", Name = "Chi nhánh Quận 1", Address = "12 Lê Thánh Tôn, Q.1, TP.HCM", IsDefault = true, SortOrder = 0 },
            new Branch { TenantId = TenantId, Code = "CN2", Name = "Chi nhánh Thủ Đức", Address = "88 Võ Văn Ngân, TP. Thủ Đức", SortOrder = 1 },
        };
        db.Branches.AddRange(branches);
        foreach (var b in branches)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                db.BranchHours.Add(new BranchHour { TenantId = TenantId, BranchId = b.Id, DayOfWeek = day, OpenTime = new TimeOnly(8, 0), CloseTime = new TimeOnly(20, 0) });
            }
        }

        // ---------- Danh mục ----------
        var serviceItems = Services.Select((s, i) => new CatalogItem
        {
            TenantId = TenantId,
            Kind = CatalogKind.Service,
            Code = s.Code,
            Name = s.Name,
            GroupName = s.Group,
            Price = s.Price,
            DurationMinutes = s.Minutes,
            BufferMinutes = 10,
            SortOrder = i,
        }).ToArray();
        var serviceByCode = serviceItems.ToDictionary(s => s.Code);
        var packageItems = Packages.Select((p, i) => new CatalogItem
        {
            TenantId = TenantId,
            Kind = CatalogKind.Package,
            Code = p.Code,
            Name = p.Name,
            GroupName = "Gói liệu trình",
            Price = p.Price,
            SessionCount = p.Sessions,
            ValidityDays = 180,
            SortOrder = i,
        }).ToArray();
        var productItems = Products.Select((p, i) => new CatalogItem
        {
            TenantId = TenantId,
            Kind = CatalogKind.Product,
            Code = p.Code,
            Name = p.Name,
            GroupName = "Sản phẩm",
            Price = p.Price,
            Unit = "chai",
            CostPrice = p.Price * 45 / 100,
            SortOrder = i,
        }).ToArray();
        db.CatalogItems.AddRange(serviceItems);
        db.CatalogItems.AddRange(packageItems);
        db.CatalogItems.AddRange(productItems);
        for (var i = 0; i < Packages.Length; i++)
        {
            db.PackageContents.Add(new PackageContent { TenantId = TenantId, PackageId = packageItems[i].Id, ServiceId = serviceByCode[Packages[i].ServiceCode].Id, SessionCount = Packages[i].Sessions });
        }

        // ---------- Nhân viên, kỹ năng, ca cố định ----------
        var staff = Staff.Select((s, i) => new StaffMember
        {
            TenantId = TenantId,
            BranchId = branches[s.Branch].Id,
            DisplayName = s.Name,
            Title = s.Title,
            Color = s.Color,
            SortOrder = i,
            UserId = i == 0 ? staffUser.Id : null,
            CreatedAt = now.AddDays(-140),
        }).ToArray();
        db.StaffMembers.AddRange(staff);
        foreach (var member in staff)
        {
            foreach (var service in serviceItems)
            {
                db.StaffSkills.Add(new StaffSkill { TenantId = TenantId, StaffMemberId = member.Id, CatalogItemId = service.Id });
            }
        }

        for (var i = 0; i < staff.Length; i++)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>().Where(d => d != Staff[i].DayOff))
            {
                db.StaffShiftTemplates.Add(new StaffShiftTemplate
                {
                    TenantId = TenantId,
                    StaffMemberId = staff[i].Id,
                    DayOfWeek = day,
                    StartTime = new TimeOnly(Staff[i].StartHour, 0),
                    EndTime = new TimeOnly(Staff[i].StartHour + 9, 0),
                });
            }
        }

        // ---------- Hoa hồng ----------
        var ruleService = new CommissionRule { TenantId = TenantId, Scope = CommissionScope.Service, Kind = CommissionKind.Percent, Value = 10, Note = "Mọi dịch vụ lẻ" };
        var ruleLan = new CommissionRule { TenantId = TenantId, StaffMemberId = staff[0].Id, Scope = CommissionScope.Service, Kind = CommissionKind.Percent, Value = 15, Note = "Trưởng nhóm kỹ thuật viên" };
        var rulePackage = new CommissionRule { TenantId = TenantId, Scope = CommissionScope.Package, Kind = CommissionKind.Fixed, Value = 50_000, Note = "Mỗi buổi dùng gói" };
        var ruleProduct = new CommissionRule { TenantId = TenantId, Scope = CommissionScope.Product, Kind = CommissionKind.Percent, Value = 5 };
        db.CommissionRules.AddRange(ruleService, ruleLan, rulePackage, ruleProduct);
        await db.SaveChangesAsync(cancellationToken);

        // ---------- Khách ----------
        var contacts = new List<Contact>();
        var firstSeen = new Dictionary<Guid, DateOnly>();
        var phones = new HashSet<string>(StringComparer.Ordinal);
        const int contactCount = 220;
        for (var i = 0; i < contactCount; i++)
        {
            var female = rnd.NextDouble() < 0.78;
            var name = $"{Pick(rnd, Surnames)} {Pick(rnd, female ? FemaleMiddle : MaleMiddle)} {Pick(rnd, female ? FemaleGiven : MaleGiven)}";
            string phone;
            do
            {
                phone = string.Create(CultureInfo.InvariantCulture, $"09{rnd.Next(10_000_000, 99_999_999)}");
            }
            while (!phones.Add(phone));

            var normalized = PhoneNumber.Normalize(phone)!;
            // Khách mới hôm nay (3 người cuối), còn lại rải trong 90 ngày.
            var seen = i >= contactCount - 3 ? today : today.AddDays(-rnd.Next(1, 90));
            var zalo = rnd.NextDouble() < 0.78;
            var contact = new Contact
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                Channel = zalo ? ChannelKind.Zalo : ChannelKind.Phone,
                ExternalUserId = zalo ? $"demo-zalo-{i:D4}" : $"demo-phone-{i:D4}",
                DisplayName = name,
                LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name, ["phone"] = normalized })),
                PhoneEnc = encryptor.Encrypt(normalized),
                PhoneHash = phoneHasher.Hash(normalized),
                Salutation = female ? "chị" : "anh",
                SalutationSource = "customer",
                ConsentMarketing = rnd.NextDouble() < 0.62,
                CreatedAt = At(seen, 9 + rnd.Next(0, 10), rnd.Next(0, 60)),
            };
            if (contact.CreatedAt > now)
            {
                contact.CreatedAt = now.AddMinutes(-rnd.Next(5, 90)); // khách mới hôm nay: không ghi giờ tương lai
            }
            if (contact.ConsentMarketing)
            {
                contact.ConsentAt = contact.CreatedAt;
                contact.ConsentSource = "zalo_chat";
            }

            contacts.Add(contact);
            firstSeen[contact.Id] = seen;
        }

        db.Contacts.AddRange(contacts);
        var regulars = contacts.Take(60).ToList();
        var leadsOnly = contacts.Skip(contactCount - 45).Take(42).ToList(); // chỉ nhắn hỏi, chưa đến
        var visitorPool = contacts.Except(leadsOnly).ToList();

        // ---------- Gói liệu trình (bán tại quầy ngày khách bắt đầu) ----------
        var invoices = new List<Invoice>();
        var lines = new List<InvoiceLine>();
        var payments = new List<Payment>();
        var revenue = new List<RevenueEntry>();
        var commissions = new List<CommissionEntry>();
        var customerPackages = new Dictionary<Guid, CustomerPackage>();
        var redemptions = new List<PackageRedemption>();

        foreach (var contact in regulars.Where((_, i) => i % 2 == 0).Take(28))
        {
            var def = Packages[rnd.Next(Packages.Length)];
            var item = packageItems.Single(p => p.Code == def.Code);
            var day = firstSeen[contact.Id] < today.AddDays(-HistoryDays) ? today.AddDays(-HistoryDays + rnd.Next(0, 10)) : firstSeen[contact.Id];
            if (day >= today)
            {
                continue;
            }

            var paidAt = At(day, 10 + rnd.Next(0, 8), rnd.Next(0, 60));
            var branch = branches[rnd.NextDouble() < 0.7 ? 0 : 1];
            var invoice = NewInvoice(branch.Id, contact.Id, null, paidAt);
            var line = new InvoiceLine
            {
                TenantId = TenantId,
                InvoiceId = invoice.Id,
                Kind = InvoiceLineKind.Package,
                CatalogItemId = item.Id,
                Description = item.Name,
                UnitPrice = def.Price,
                LineTotal = def.Price,
                Position = 0,
            };
            Settle(invoice, def.Price, def.Price, paidAt, PickMethod(rnd), payments);
            invoices.Add(invoice);
            lines.Add(line);
            var package = new CustomerPackage
            {
                TenantId = TenantId,
                ContactId = contact.Id,
                CatalogItemId = item.Id,
                Name = item.Name,
                InvoiceId = invoice.Id,
                TotalSessions = def.Sessions,
                PricePaid = def.Price,
                ExpiresAt = paidAt.AddDays(180),
                CreatedAt = paidAt,
            };
            line.CustomerPackageId = package.Id;
            customerPackages[contact.Id] = package;
        }

        // ---------- Lịch hẹn + hóa đơn + doanh thu + hoa hồng ----------
        var appointments = new List<Appointment>();
        var appointmentItems = new List<AppointmentItem>();
        var busy = staff.ToDictionary(s => s.Id, _ => new List<(DateTimeOffset Start, DateTimeOffset End)>());
        var serviceWeights = Services.Select(s => s.Weight).ToArray();

        for (var day = today.AddDays(-HistoryDays); day <= today.AddDays(FutureDays); day = day.AddDays(1))
        {
            var weekendBoost = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.45 : day.DayOfWeek == DayOfWeek.Monday ? 0.8 : 1.0;
            for (var b = 0; b < branches.Length; b++)
            {
                var target = (int)Math.Round((b == 0 ? 15 : 8) * weekendBoost * (0.8 + rnd.NextDouble() * 0.4));
                var working = staff.Where((s, i) => Staff[i].Branch == b && Staff[i].DayOff != day.DayOfWeek).ToArray();
                for (var n = 0; n < target && working.Length > 0; n++)
                {
                    var member = working[rnd.Next(working.Length)];
                    var def = Staff[Array.IndexOf(staff, member)];
                    var contact = PickContact(rnd, regulars, visitorPool, firstSeen, day);
                    if (contact is null)
                    {
                        continue;
                    }

                    customerPackages.TryGetValue(contact.Id, out var package);
                    var usePackage = package is not null && package.UsedSessions < package.TotalSessions && package.CreatedAt <= At(day, 23, 0) && rnd.NextDouble() < 0.75;
                    var service = usePackage
                        ? serviceItems.Single(s => s.Code == Packages.Single(p => p.Name == package!.Name).ServiceCode)
                        : serviceItems[WeightedIndex(rnd, serviceWeights)];

                    var hour = PickHour(rnd, def.StartHour, def.StartHour + 9 - (service.DurationMinutes!.Value + 59) / 60);
                    var start = At(day, hour, rnd.NextDouble() < 0.5 ? 0 : 30);
                    var end = start.AddMinutes(service.DurationMinutes.Value);
                    if (busy[member.Id].Any(x => x.Start < end && start < x.End))
                    {
                        continue;
                    }

                    busy[member.Id].Add((start, end));
                    var source = PickSource(rnd);
                    var status = PickStatus(rnd, day, today, nowLocalMinutes, start, end);
                    var createdAt = source switch
                    {
                        AppointmentSource.WalkIn => start.AddMinutes(-10),
                        AppointmentSource.Bot => start.AddDays(-rnd.Next(1, 5)).AddHours(-rnd.Next(1, 6)),
                        _ => start.AddDays(-rnd.Next(0, 8)).AddHours(-rnd.Next(1, 6)),
                    };
                    if (createdAt > now)
                    {
                        createdAt = now.AddMinutes(-rnd.Next(5, 120));
                    }

                    var appointment = new Appointment
                    {
                        TenantId = TenantId,
                        BranchId = branches[b].Id,
                        ContactId = contact.Id,
                        StaffMemberId = member.Id,
                        StartAt = start,
                        EndAt = end,
                        BufferMinutes = 10,
                        Status = status,
                        Source = source,
                        CreatedAt = createdAt,
                        BookedByUserId = source == AppointmentSource.Staff ? owner.Id : null,
                    };
                    if (status is AppointmentStatus.Done or AppointmentStatus.InProgress or AppointmentStatus.Arrived)
                    {
                        appointment.ArrivedAt = start.AddMinutes(-rnd.Next(2, 12));
                    }

                    if (status == AppointmentStatus.Done)
                    {
                        appointment.DoneAt = end;
                    }

                    if (status == AppointmentStatus.Cancelled)
                    {
                        appointment.CancelledAt = start.AddHours(-rnd.Next(2, 30));
                        appointment.CancelReason = "Khách báo bận";
                        appointment.CancelledBy = "customer";
                    }

                    if (status == AppointmentStatus.Confirmed)
                    {
                        appointment.CustomerConfirmedAt = start.AddHours(-rnd.Next(3, 20));
                    }

                    appointments.Add(appointment);
                    appointmentItems.Add(new AppointmentItem
                    {
                        TenantId = TenantId,
                        AppointmentId = appointment.Id,
                        CatalogItemId = service.Id,
                        Name = service.Name,
                        DurationMinutes = service.DurationMinutes.Value,
                        PriceSnapshot = service.Price,
                        Position = 0,
                    });

                    if (status != AppointmentStatus.Done)
                    {
                        continue;
                    }

                    var paidAt = end.AddMinutes(rnd.Next(3, 15));
                    var sourceKey = SourceKey(source);
                    if (usePackage)
                    {
                        package!.UsedSessions++;
                        if (package.UsedSessions >= package.TotalSessions)
                        {
                            package.Status = CustomerPackageStatus.Exhausted;
                        }

                        var redemption = new PackageRedemption
                        {
                            TenantId = TenantId,
                            CustomerPackageId = package.Id,
                            AppointmentId = appointment.Id,
                            StaffMemberId = member.Id,
                            At = paidAt,
                        };
                        redemptions.Add(redemption);
                        var unit = package.PricePaid / package.TotalSessions;
                        revenue.Add(new RevenueEntry
                        {
                            TenantId = TenantId,
                            BranchId = branches[b].Id,
                            Date = LocalDate(paidAt),
                            Kind = RevenueKind.PackageRecognized,
                            Amount = unit,
                            RedemptionId = redemption.Id,
                            CatalogItemId = package.CatalogItemId,
                            StaffMemberId = member.Id,
                            ContactId = contact.Id,
                            Source = sourceKey,
                            At = paidAt,
                        });
                        commissions.Add(Commission(member.Id, null, null, redemption.Id, rulePackage.Id, unit, rulePackage.Value, paidAt, today));
                        continue;
                    }

                    // Hóa đơn dịch vụ (+ thỉnh thoảng mua thêm sản phẩm). Vài hóa đơn gần đây còn nợ.
                    var invoice = NewInvoice(branches[b].Id, contact.Id, appointment.Id, paidAt);
                    var serviceLine = new InvoiceLine
                    {
                        TenantId = TenantId,
                        InvoiceId = invoice.Id,
                        Kind = InvoiceLineKind.Service,
                        CatalogItemId = service.Id,
                        Description = service.Name,
                        UnitPrice = service.Price!.Value,
                        LineTotal = service.Price.Value,
                        PerformerStaffId = member.Id,
                        Position = 0,
                    };
                    lines.Add(serviceLine);
                    var subtotal = serviceLine.LineTotal;
                    InvoiceLine? productLine = null;
                    if (rnd.NextDouble() < 0.22)
                    {
                        var product = productItems[rnd.Next(productItems.Length)];
                        productLine = new InvoiceLine
                        {
                            TenantId = TenantId,
                            InvoiceId = invoice.Id,
                            Kind = InvoiceLineKind.Product,
                            CatalogItemId = product.Id,
                            Description = product.Name,
                            UnitPrice = product.Price!.Value,
                            LineTotal = product.Price.Value,
                            PerformerStaffId = member.Id,
                            Position = 1,
                        };
                        lines.Add(productLine);
                        subtotal += productLine.LineTotal;
                    }

                    var discount = rnd.NextDouble() < 0.12 ? subtotal / 10 / 1000 * 1000 : 0;
                    var total = subtotal - discount;
                    invoice.DiscountAmount = discount;
                    invoice.DiscountReason = discount > 0 ? "Khách quen" : null;
                    var recent = day >= today.AddDays(-4);
                    var paid = recent && rnd.NextDouble() < 0.08 ? 0 : recent && rnd.NextDouble() < 0.08 ? total / 2 / 1000 * 1000 : total;
                    Settle(invoice, subtotal, total, paidAt, PickMethod(rnd), payments, paid);
                    invoices.Add(invoice);
                    if (paid < total)
                    {
                        continue; // còn nợ: chưa ghi nhận doanh thu (giống app — ghi khi thu đủ)
                    }

                    var serviceAmount = serviceLine.LineTotal - discount;
                    revenue.Add(Revenue(branches[b].Id, RevenueKind.Service, serviceAmount, invoice.Id, serviceLine.Id, service.Id, member.Id, contact.Id, sourceKey, paidAt));
                    var serviceRule = member.Id == staff[0].Id ? ruleLan : ruleService;
                    commissions.Add(Commission(member.Id, invoice.Id, serviceLine.Id, null, serviceRule.Id, serviceAmount, serviceAmount * serviceRule.Value / 100, paidAt, today));
                    if (productLine is not null)
                    {
                        revenue.Add(Revenue(branches[b].Id, RevenueKind.Product, productLine.LineTotal, invoice.Id, productLine.Id, productLine.CatalogItemId, member.Id, contact.Id, sourceKey, paidAt));
                        commissions.Add(Commission(member.Id, invoice.Id, productLine.Id, null, ruleProduct.Id, productLine.LineTotal, productLine.LineTotal * ruleProduct.Value / 100, paidAt, today));
                    }
                }
            }
        }

        // Số hóa đơn tăng dần theo thời gian lập.
        var number = 1;
        foreach (var invoice in invoices.OrderBy(i => i.IssuedAt))
        {
            invoice.Number = number++;
        }

        // Hoa hồng tháng trước đã trả; tháng này Lan đã nhận một nửa.
        var thisPeriod = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        foreach (var entry in commissions)
        {
            if (string.CompareOrdinal(entry.Period, thisPeriod) < 0)
            {
                entry.PaidAt = At(DateOnly.ParseExact(entry.Period + "-05", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1), 17, 0);
            }
            else if (entry.StaffMemberId == staff[0].Id && entry.At < At(today, 0, 0).AddDays(-(today.Day / 2)))
            {
                entry.PaidAt = now.AddDays(-2);
            }
        }

        db.CustomerPackages.AddRange(customerPackages.Values);
        db.Appointments.AddRange(appointments);
        db.AppointmentItems.AddRange(appointmentItems);
        db.Invoices.AddRange(invoices);
        db.InvoiceLines.AddRange(lines);
        db.Payments.AddRange(payments);
        db.PackageRedemptions.AddRange(redemptions);
        db.RevenueEntries.AddRange(revenue);
        db.CommissionEntries.AddRange(commissions);
        await db.SaveChangesAsync(cancellationToken);

        // ---------- Chấm công 35 ngày ----------
        var attendanceCount = 0;
        for (var day = today.AddDays(-35); day <= today; day = day.AddDays(1))
        {
            for (var i = 0; i < staff.Length; i++)
            {
                if (Staff[i].DayOff == day.DayOfWeek)
                {
                    continue;
                }

                var shiftStart = At(day, Staff[i].StartHour, 0);
                var shiftEnd = At(day, Staff[i].StartHour + 9, 0);
                if (day == today && shiftStart > now)
                {
                    continue;
                }

                var clockIn = shiftStart.AddMinutes(rnd.NextDouble() < 0.12 ? rnd.Next(5, 25) : -rnd.Next(0, 12));
                DateTimeOffset? clockOut = day == today && shiftEnd > now ? null : shiftEnd.AddMinutes(rnd.Next(-10, 25));
                var byManager = clockOut is not null && rnd.NextDouble() < 0.04;
                db.Attendance.Add(new Attendance
                {
                    TenantId = TenantId,
                    StaffMemberId = staff[i].Id,
                    BranchId = staff[i].BranchId,
                    ClockInAt = clockIn,
                    ClockOutAt = clockOut,
                    Source = byManager ? "manager" : "self",
                    Note = byManager ? "Quên chấm ra ca" : null,
                    ByUserId = byManager ? owner.Id : null,
                });
                attendanceCount++;
            }
        }

        // ---------- Hội thoại Zalo với bot ----------
        var botBooked = appointments.Where(a => a.Source == AppointmentSource.Bot).GroupBy(a => a.ContactId).ToDictionary(g => g.Key, g => g.ToList());
        var conversations = new List<Conversation>();
        var messages = new List<Message>();
        var usage = new List<UsageRecord>();
        var attentionLeft = 6;
        foreach (var contact in contacts.Where(c => c.Channel == ChannelKind.Zalo))
        {
            var start = contact.CreatedAt;
            if (start < now.AddDays(-HistoryDays))
            {
                start = now.AddDays(-HistoryDays + rnd.Next(0, 20));
            }

            var conversation = new Conversation
            {
                Id = Guid.CreateVersion7(start),
                TenantId = TenantId,
                ContactId = contact.Id,
                Channel = ChannelKind.Zalo,
                CreatedAt = start,
            };
            conversations.Add(conversation);

            // Các "lượt" chat: ngày khách mới nhắn + ngày bot chốt lịch + vài lượt hỏi lẻ.
            var sessions = new List<(DateTimeOffset At, string Kind, Appointment? Booking)> { (start, "ask", null) };
            if (botBooked.TryGetValue(contact.Id, out var booked))
            {
                sessions.AddRange(booked.Where(a => a.CreatedAt < now).Select(a => (a.CreatedAt, "book", (Appointment?)a)));
            }

            var extra = leadsOnly.Contains(contact) ? rnd.Next(1, 3) : rnd.Next(0, 3);
            for (var k = 0; k < extra; k++)
            {
                // Lượt hỏi thêm rơi giữa lúc bắt đầu hội thoại và hiện tại.
                sessions.Add((start + (now - start) * rnd.NextDouble(), "ask", null));
            }

            foreach (var (at, kind, booking) in sessions.Where(s => s.At <= now).OrderBy(s => s.At))
            {
                var handoff = kind == "ask" && rnd.NextDouble() < 0.17;
                string ask;
                string answer;
                if (kind == "book" && booking is not null)
                {
                    var local = booking.StartAt.ToOffset(Vn);
                    ask = string.Create(CultureInfo.InvariantCulture, $"Cho {contact.Salutation} đặt lịch {local:HH:mm} ngày {local:dd/MM} nhé em");
                    answer = string.Create(CultureInfo.InvariantCulture, $"Dạ em đã giữ lịch cho mình lúc {local:HH:mm} ngày {local:dd/MM} ở {branches.Single(x => x.Id == booking.BranchId).Name} ạ. Hẹn gặp mình nha!");
                    booking.ConversationId = conversation.Id;
                }
                else if (handoff)
                {
                    ask = Pick(rnd, UnknownAsks);
                    answer = "Dạ câu này em xin phép chuyển nhân viên tư vấn kỹ hơn cho mình ạ. Nhân viên sẽ phản hồi mình sớm nhất.";
                }
                else
                {
                    (ask, answer) = Chats[rnd.Next(Chats.Length)];
                }

                var askAt = at;
                var customer = NewMessage(conversation.Id, MessageDirection.In, MessageSender.Customer, encryptor.Encrypt(ask), askAt);
                var reasonKey = handoff ? Pick(rnd, HandoffReasons) : null;
                var bot = NewMessage(conversation.Id, MessageDirection.Out, MessageSender.Bot, encryptor.Encrypt(answer), askAt.AddSeconds(rnd.Next(3, 9)));
                bot.ReplyToMessageId = customer.Id;
                bot.DeliveryStatus = DeliveryStatus.Sent;
                bot.AiTraceJson = Trace(rnd, handoff, reasonKey, kind == "book");
                messages.Add(customer);
                messages.Add(bot);
                usage.Add(new UsageRecord
                {
                    Id = Guid.CreateVersion7(bot.CreatedAt),
                    TenantId = TenantId,
                    Kind = UsageKind.Chat,
                    Provider = "gemini",
                    Model = Model,
                    InputTokens = rnd.Next(2600, 4200),
                    OutputTokens = rnd.Next(90, 260),
                    CostUsd = 0.0009m + (decimal)rnd.Next(0, 600) / 1_000_000m,
                    ConversationId = conversation.Id,
                    CreatedAt = bot.CreatedAt,
                });

                conversation.LastCustomerMessageAt = askAt;
                conversation.LastMessageAt = bot.CreatedAt;
                contact.LastCustomerMessageAt = askAt;

                if (!handoff)
                {
                    continue;
                }

                // Chuyển nhân viên: ca mới (2 ngày gần đây) để lại "chờ người" cho Hộp thư; ca cũ nhân viên đã trả lời.
                var waiting = askAt > now.AddDays(-2) && attentionLeft > 0;
                if (waiting)
                {
                    attentionLeft--;
                    conversation.Mode = ConversationMode.Human;
                    conversation.HandoffReason = reasonKey;
                    conversation.NeedsAttentionSince = bot.CreatedAt;
                    continue;
                }

                var staffReply = NewMessage(conversation.Id, MessageDirection.Out, MessageSender.Staff,
                    encryptor.Encrypt($"Dạ {contact.Salutation} ơi, em là Lan bên {TenantName}, em hỗ trợ mình câu này nhé. — Lan, CSKH"), bot.CreatedAt.AddMinutes(rnd.Next(4, 40)));
                staffReply.SenderUserId = owner.Id;
                staffReply.DeliveryStatus = DeliveryStatus.Sent;
                messages.Add(staffReply);
                conversation.LastMessageAt = staffReply.CreatedAt;
            }
        }

        // Mức tiềm năng theo hành vi thật của dữ liệu ảo.
        var visited = appointments.Where(a => a.Status == AppointmentStatus.Done).Select(a => a.ContactId).ToHashSet();
        var spend = revenue.Where(r => r.ContactId is not null).GroupBy(r => r.ContactId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var vip = spend.OrderByDescending(x => x.Value).Take(12).Select(x => x.Key).ToHashSet();
        foreach (var contact in contacts)
        {
            contact.LeadStatus = visited.Contains(contact.Id)
                ? LeadStatus.Won
                : rnd.NextDouble() switch { < 0.3 => LeadStatus.Hot, < 0.7 => LeadStatus.Interested, < 0.9 => LeadStatus.New, _ => LeadStatus.Lost };
            contact.LeadStatusChangedAt = contact.LastCustomerMessageAt ?? contact.CreatedAt;
            if (vip.Contains(contact.Id))
            {
                contact.Tags.Add("VIP");
            }

            if (customerPackages.ContainsKey(contact.Id))
            {
                contact.Tags.Add("Có gói liệu trình");
            }
        }

        db.Conversations.AddRange(conversations);
        db.Messages.AddRange(messages);
        db.UsageRecords.AddRange(usage);
        await db.SaveChangesAsync(cancellationToken);

        // ---------- Gợi ý chăm sóc + ghi chú ----------
        var hotLeads = contacts.Where(c => c.LeadStatus == LeadStatus.Hot && c.Channel == ChannelKind.Zalo && c.LastCustomerMessageAt is not null).Take(5).ToList();
        foreach (var contact in hotLeads)
        {
            var conversation = conversations.Single(c => c.ContactId == contact.Id);
            db.CareSuggestions.Add(new CareSuggestion
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                ContactId = contact.Id,
                ConversationId = conversation.Id,
                Temperature = CareTemperature.Hot,
                Trigger = "thinking",
                Status = CareSuggestionStatus.Open,
                ReasonEnc = encryptor.Encrypt("Khách hỏi giá dịch vụ nhưng chưa đặt lịch."),
                SuggestedActionEnc = encryptor.Encrypt("Nhắn hỏi thăm, gợi ý khung giờ còn trống cuối tuần."),
                DraftEnc = encryptor.Encrypt($"Dạ {contact.Salutation} ơi, cuối tuần này bên em còn trống 10:00 và 15:30, {contact.Salutation} muốn em giữ chỗ không ạ?"),
                MessagingDeadline = contact.LastCustomerMessageAt!.Value.AddDays(7),
                CreatedAt = contact.LastCustomerMessageAt.Value.AddHours(6),
            });
        }

        foreach (var contact in regulars.Where(c => visited.Contains(c.Id)).Take(15))
        {
            db.ContactNotes.Add(new ContactNote
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                ContactId = contact.Id,
                AuthorUserId = owner.Id,
                Kind = ContactNoteKind.Service,
                ContentEnc = encryptor.Encrypt("Khách thích lực vừa, hay mỏi vai gáy do ngồi máy tính. Nhắc khách uống nước ấm sau buổi."),
                HappenedOn = today.AddDays(-rnd.Next(2, 30)),
                CreatedAt = now.AddDays(-rnd.Next(1, 20)),
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var revenue30 = revenue.Where(r => r.Date > today.AddDays(-30)).Sum(r => r.Amount);
        return string.Join(Environment.NewLine,
            $"Đã tạo doanh nghiệp demo \"{TenantName}\".",
            $"  Đăng nhập chủ: {OwnerEmail} / {DevSeeder.DevPassword}",
            $"  Đăng nhập nhân viên (Lan, có nút Vào ca): {StaffEmail} / {DevSeeder.DevPassword}",
            $"  {contacts.Count} khách · {appointments.Count} lịch hẹn · {invoices.Count} hóa đơn · {customerPackages.Count} gói · {attendanceCount} lượt chấm công",
            $"  {conversations.Count} hội thoại · {messages.Count} tin nhắn · doanh thu 30 ngày ≈ {revenue30.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} đ",
            "  Chạy lại lệnh bất cứ lúc nào: số liệu được làm mới theo ngày hôm nay.");
    }

    private static Invoice NewInvoice(Guid branchId, Guid contactId, Guid? appointmentId, DateTimeOffset issuedAt) => new()
    {
        TenantId = TenantId,
        BranchId = branchId,
        ContactId = contactId,
        AppointmentId = appointmentId,
        IssuedAt = issuedAt,
        CreatedAt = issuedAt,
    };

    /// <summary>Chốt tổng tiền và thu (đủ hoặc một phần).</summary>
    private static void Settle(Invoice invoice, long subtotal, long total, DateTimeOffset at, PaymentMethod method, List<Payment> payments, long? paid = null)
    {
        var amount = paid ?? total;
        invoice.Subtotal = subtotal;
        invoice.Total = total;
        invoice.PaidAmount = amount;
        invoice.Status = amount >= total ? InvoiceStatus.Paid : amount > 0 ? InvoiceStatus.Partial : InvoiceStatus.Open;
        invoice.PaidAt = amount >= total ? at : null;
        if (amount > 0)
        {
            payments.Add(new Payment { TenantId = TenantId, InvoiceId = invoice.Id, Method = method, Amount = amount, ReceivedAt = at, CreatedAt = at });
        }
    }

    private static RevenueEntry Revenue(Guid branchId, RevenueKind kind, long amount, Guid invoiceId, Guid lineId, Guid? itemId, Guid staffId, Guid contactId, string source, DateTimeOffset at) => new()
    {
        TenantId = TenantId,
        BranchId = branchId,
        Date = LocalDate(at),
        Kind = kind,
        Amount = amount,
        InvoiceId = invoiceId,
        InvoiceLineId = lineId,
        CatalogItemId = itemId,
        StaffMemberId = staffId,
        ContactId = contactId,
        Source = source,
        At = at,
    };

    private static CommissionEntry Commission(Guid staffId, Guid? invoiceId, Guid? lineId, Guid? redemptionId, Guid ruleId, long basis, long amount, DateTimeOffset at, DateOnly today) => new()
    {
        TenantId = TenantId,
        StaffMemberId = staffId,
        InvoiceId = invoiceId,
        InvoiceLineId = lineId,
        RedemptionId = redemptionId,
        RuleId = ruleId,
        Basis = basis,
        Amount = amount,
        At = at,
        Period = LocalDate(at) > today ? today.ToString("yyyy-MM", CultureInfo.InvariantCulture) : LocalDate(at).ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    private static Message NewMessage(Guid conversationId, MessageDirection direction, MessageSender sender, string contentEnc, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(at),
        TenantId = TenantId,
        ConversationId = conversationId,
        Direction = direction,
        Sender = sender,
        ContentEnc = contentEnc,
        CreatedAt = at,
    };

    /// <summary>Vết trả lời của bot (ai_trace) đúng dạng BotEngine ghi — Hộp thư và Tổng quan đọc nó.</summary>
    private static string Trace(Random rnd, bool handoff, string? reason, bool booking) => new JsonObject
    {
        ["template"] = "spa@1",
        ["model"] = Model,
        ["calls"] = 1,
        ["latencyMs"] = rnd.Next(1400, 3600),
        ["dangerSignal"] = null,
        ["guards"] = booking ? new JsonArray("booking_intent") : new JsonArray(),
        ["forbidden"] = new JsonArray(),
        ["chunks"] = new JsonArray(),
        ["belowThreshold"] = 0,
        ["confidence"] = handoff ? "low" : "high",
        ["needsHuman"] = handoff,
        ["handoffReason"] = reason,
        ["urgency"] = "none",
        ["sentiment"] = reason == "negative_sentiment" ? "negative" : "neutral",
        ["healthTopic"] = false,
        ["leadKeys"] = new JsonArray(),
        ["piiMasked"] = 0,
        ["firstReply"] = false,
        ["parts"] = 1,
    }.ToJsonString();

    private static Contact? PickContact(Random rnd, List<Contact> regulars, List<Contact> pool, Dictionary<Guid, DateOnly> firstSeen, DateOnly day)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var list = rnd.NextDouble() < 0.55 ? regulars : pool;
            var candidate = list[rnd.Next(list.Count)];
            if (firstSeen[candidate.Id] <= day || day > DateOnly.FromDateTime(DateTime.UtcNow))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Giờ bắt đầu: đông 10–11h và 15–18h, cuối tuần đông hơn.</summary>
    private static int PickHour(Random rnd, int from, int to)
    {
        to = Math.Max(from, to);
        var hours = Enumerable.Range(from, to - from + 1).ToArray();
        var weights = hours.Select(h => h is 10 or 11 or 15 or 16 or 17 ? 3 : h is 18 or 14 ? 2 : 1).ToArray();
        return hours[WeightedIndex(rnd, weights)];
    }

    private static AppointmentSource PickSource(Random rnd) => rnd.NextDouble() switch
    {
        < 0.36 => AppointmentSource.Bot,
        < 0.80 => AppointmentSource.Staff,
        < 0.88 => AppointmentSource.Online,
        _ => AppointmentSource.WalkIn,
    };

    private static AppointmentStatus PickStatus(Random rnd, DateOnly day, DateOnly today, int nowMinutes, DateTimeOffset start, DateTimeOffset end)
    {
        if (day < today)
        {
            return rnd.NextDouble() switch { < 0.06 => AppointmentStatus.Cancelled, < 0.11 => AppointmentStatus.NoShow, _ => AppointmentStatus.Done };
        }

        if (day > today)
        {
            return rnd.NextDouble() switch { < 0.05 => AppointmentStatus.Cancelled, < 0.45 => AppointmentStatus.Confirmed, _ => AppointmentStatus.Booked };
        }

        var startMinutes = (int)start.ToOffset(Vn).TimeOfDay.TotalMinutes;
        var endMinutes = (int)end.ToOffset(Vn).TimeOfDay.TotalMinutes;
        if (endMinutes <= nowMinutes)
        {
            return rnd.NextDouble() < 0.07 ? AppointmentStatus.NoShow : AppointmentStatus.Done;
        }

        if (startMinutes <= nowMinutes)
        {
            return AppointmentStatus.InProgress;
        }

        return startMinutes - nowMinutes < 30 ? AppointmentStatus.Arrived : rnd.NextDouble() < 0.5 ? AppointmentStatus.Confirmed : AppointmentStatus.Booked;
    }

    private static PaymentMethod PickMethod(Random rnd) => rnd.NextDouble() switch
    {
        < 0.55 => PaymentMethod.Transfer,
        < 0.86 => PaymentMethod.Cash,
        _ => PaymentMethod.Card,
    };

    private static string SourceKey(AppointmentSource source) => source switch
    {
        AppointmentSource.Bot => "bot",
        AppointmentSource.Online => "online",
        AppointmentSource.WalkIn => "walk_in",
        _ => "staff",
    };

    private static int WeightedIndex(Random rnd, int[] weights)
    {
        var roll = rnd.Next(weights.Sum());
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private static T Pick<T>(Random rnd, T[] items) => items[rnd.Next(items.Length)];
#pragma warning restore CA5394

    /// <summary>Thời điểm UTC của giờ địa phương (giờ Việt Nam) trong ngày.</summary>
    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(Math.Clamp(hour, 0, 23), minute)), Vn).ToUniversalTime();

    private static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Vn).DateTime);
}
