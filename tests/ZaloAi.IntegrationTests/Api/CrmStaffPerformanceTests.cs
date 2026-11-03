using System.Net;
using System.Net.Http.Json;
using System.Text;
using Shouldly;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Api.Tenants;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C5: hoa hồng theo quy tắc + thu hồi khi hủy, chấm công tự bấm, hiệu suất, nhân viên chỉ thấy số của mình.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmStaffPerformanceTests(PostgresFixture db)
{
    private const string Knowledge = """
        { "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 500000 } ],
          "packages": [ { "code": "GOI-MASSAGE-5", "name": "Gói massage 5 buổi", "sessions": 5, "minutesPerSession": 60, "price": 2000000 } ] }
        """;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task Commission_accrues_on_paid_invoice_and_package_session_reverts_on_void_and_staff_sees_only_self()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Hoa hồng");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Knowledge)), "file", "data.json" } };
        using var upload = await owner.PostAsync(Url("/knowledge/imports"), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/knowledge/imports/{preview.Id}/apply"), new { selections = preview.Items.Select(i => new { key = i.Key }) })).EnsureSuccessStatusCode();
        var items = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!;
        var service = items.Single(i => i.Kind == "service");
        var package = items.Single(i => i.Kind == "package");
        (await owner.PutAsJsonAsync(Url($"/catalog/{package.Id}/contents"), new SetPackageContentsRequest([new PackageContentItem(service.Id, 5)]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var branch = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();

        // Nhân viên Lan gắn tài khoản nhân viên; Mai không gắn.
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staffUserId = (await owner.GetFromJsonAsync<List<TeamMemberResponse>>(Url("/tenant/team")))!.Single(m => m.Email == staffEmail).UserId;
        var lan = (await (await owner.PostAsJsonAsync(Url("/staff"), new SaveStaffRequest(branch.Id, "Lan", null, null, "#2563eb", true, staffUserId))).Content.ReadFromJsonAsync<StaffResponse>())!;
        var mai = (await (await owner.PostAsJsonAsync(Url("/staff"), new SaveStaffRequest(branch.Id, "Mai", null, null, "#16a34a", true, null))).Content.ReadFromJsonAsync<StaffResponse>())!;
        var staff = await db.Api.CreateLoggedInClientAsync(staffEmail);

        // Quy tắc: chung 10% dịch vụ; Lan riêng 20% dịch vụ; gói 50.000 cố định mỗi buổi.
        (await owner.PostAsJsonAsync(Url("/commissions/rules"), new SaveCommissionRuleRequest(null, "service", null, "percent", 10))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url("/commissions/rules"), new SaveCommissionRuleRequest(lan.Id, "service", null, "percent", 20))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url("/commissions/rules"), new SaveCommissionRuleRequest(null, "package", null, "fixed", 50000))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync(Url("/commissions/rules"), new SaveCommissionRuleRequest(null, "all", null, "percent", 1))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Khách + hóa đơn: Lan làm dịch vụ 500.000 (giảm 10% hóa đơn → cơ sở 450.000 → 90.000); Mai làm dịch vụ → 50.000.
        var contact = (await (await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, null, new NewContactRequest("Khách HH", "0905999000", "chị"), null, null, new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero).AddHours(6), [service.Id], null, 1, "staff", true))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        var invoice = (await (await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contact.ContactId, null,
            [new InvoiceLineRequest(service.Id, null, null, 1, null, 0, lan.Id), new InvoiceLineRequest(service.Id, null, null, 1, null, 0, mai.Id)], null))).Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/discount"), new ApplyDiscountRequest(100000, "khuyến mãi", null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("cash", 900000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var period = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var statements = (await owner.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}")))!;
        statements.Single(s => s.StaffMemberId == lan.Id).Total.ShouldBe(90000);
        statements.Single(s => s.StaffMemberId == mai.Id).Total.ShouldBe(45000);

        // Nhân viên (Lan) chỉ thấy mình; xin xem Mai → 403.
        (await staff.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}")))!.ShouldHaveSingleItem().StaffMemberId.ShouldBe(lan.Id);
        (await staff.GetAsync(Url($"/commissions?period={period}&staffMemberId={mai.Id}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Gói: mua rồi dùng 1 buổi do Lan làm → 50.000 cố định.
        var sale = (await (await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contact.ContactId, null, [new InvoiceLineRequest(package.Id, null, null)], null))).Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/invoices/{sale.Id}/payments"), new AddPaymentRequest("transfer", 2000000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var owned = (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contact.ContactId}")))!.Single();
        var use = (await (await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contact.ContactId, null, [new InvoiceLineRequest(service.Id, owned.Id, null, 1, null, 0, lan.Id)], null))).Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsync(Url($"/invoices/{use.Id}/close-zero"), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}&staffMemberId={lan.Id}")))!.Single().Total.ShouldBe(140000);

        // Hủy hóa đơn dùng buổi → thu hồi 50.000; đánh dấu đã trả.
        (await owner.PostAsJsonAsync(Url($"/invoices/{use.Id}/void"), new VoidInvoiceRequest("nhầm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}&staffMemberId={lan.Id}")))!.Single().Total.ShouldBe(90000);
        (await owner.PostAsJsonAsync(Url("/commissions/mark-paid"), new MarkCommissionPaidRequest(lan.Id, period))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}&staffMemberId={lan.Id}")))!.Single().Paid.ShouldBe(90000);

        // Chấm công: Lan tự vào ca, vào lần 2 → 409, ra ca; quản lý chấm cho Mai; hiệu suất có giờ làm và hoa hồng.
        (await staff.PostAsJsonAsync(Url("/attendance/clock-in"), new ClockRequest(null, null, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await staff.PostAsJsonAsync(Url("/attendance/clock-in"), new ClockRequest(null, null, null))).ShouldBeProblemAsync(409, "attendance_open");
        (await staff.GetAsync(Url("/attendance/me"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync(Url("/attendance/clock-in"), new ClockRequest(mai.Id, null, null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync(Url("/attendance/clock-in"), new ClockRequest(mai.Id, null, "ghi hộ"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync(Url("/attendance/clock-out"), new ClockRequest(null, null, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.GetAsync(Url("/attendance/me"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PatchAsJsonAsync(Url($"/appointments/{contact.Id}/assign"), new AssignAppointmentRequest(lan.Id, null))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PatchAsJsonAsync(Url($"/appointments/{contact.Id}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var performance = (await owner.GetFromJsonAsync<List<StaffPerformanceResponse>>(Url("/staff/performance")))!;
        var lanRow = performance.Single(p => p.StaffMemberId == lan.Id);
        // Hoàn thành lịch (gói còn bao) → tự trừ 1 buổi do Lan làm → thêm 50.000 hoa hồng gói.
        (lanRow.AppointmentsDone, lanRow.Commission, lanRow.Customers).ShouldBe((1, 140000, 1));
        lanRow.Revenue.ShouldBe(850000); // 450.000 dịch vụ + 400.000 buổi gói tự trừ khi hoàn thành (buổi của hóa đơn đã hủy được ghi âm)
        (await staff.GetFromJsonAsync<List<StaffPerformanceResponse>>(Url("/staff/performance")))!.ShouldHaveSingleItem().StaffMemberId.ShouldBe(lan.Id);

        // Tenant khác không thấy gì.
        var (tenantB, _) = await db.CreateTenantAsync("HH B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<List<CommissionStatementResponse>>(Url($"/commissions?period={period}")))!.ShouldBeEmpty();
        (await ownerB.GetFromJsonAsync<List<StaffPerformanceResponse>>(Url("/staff/performance")))!.ShouldBeEmpty();
    }
}
