using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Reports;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C6: hôm nay, doanh thu (kỳ, loại, nguồn, thanh toán), hiệu quả AI, khách, công nợ, Excel, hỏi tiếng Việt, báo cáo tuần, quyền.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmReportTests(PostgresFixture db)
{
    private const string Knowledge = """
        { "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 500000 } ],
          "products": [ { "code": "SP-TINH-DAU", "name": "Tinh dầu tràm", "price": 100000, "unit": "chai" } ] }
        """;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Fact]
    public async Task Reports_reflect_invoices_appointments_and_answer_questions()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Báo cáo");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Knowledge)), "file", "data.json" } };
        using var upload = await owner.PostAsync(Url("/knowledge/imports"), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/knowledge/imports/{preview.Id}/apply"), new { selections = preview.Items.Select(i => new { key = i.Key }) })).EnsureSuccessStatusCode();
        var items = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!;
        var service = items.Single(i => i.Kind == "service");
        var product = items.Single(i => i.Kind == "product");
        var branch = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();
        var lan = (await (await owner.PostAsJsonAsync(Url("/staff"), new SaveStaffRequest(branch.Id, "Lan", null, null, "#2563eb", true, null))).Content.ReadFromJsonAsync<StaffResponse>())!;

        // Lịch hôm nay (xếp chồng cố ý để không phụ thuộc giờ) → hoàn thành; hóa đơn 500.000 dịch vụ + 2 sản phẩm, thu tiền mặt + chuyển khoản.
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddHours(9); // 16:00 VN hôm nay (hoặc ngày mai nếu test chạy sau 17:00 VN — vẫn trong khoảng báo cáo)
        var appointment = (await (await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, null, new NewContactRequest("Khách BC", "0905123123", "chị"), lan.Id, null, today, [service.Id], null, 1, "walk_in", true))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        (await owner.PatchAsJsonAsync(Url($"/appointments/{appointment.Id}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var invoice = (await (await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, appointment.ContactId, appointment.Id,
            [new InvoiceLineRequest(service.Id, null, null, 1, null, 0, lan.Id), new InvoiceLineRequest(product.Id, null, null, 2)], null))).Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("cash", 400000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("transfer", 300000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Hóa đơn còn nợ.
        var open = (await (await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, appointment.ContactId, null, [new InvoiceLineRequest(product.Id, null, null)], null))).Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/invoices/{open.Id}/payments"), new AddPaymentRequest("cash", 40000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var todayReport = (await owner.GetFromJsonAsync<TodayReport>(Url("/reports/today")))!;
        (todayReport.Revenue, todayReport.Cash, todayReport.Transfer, todayReport.Invoices, todayReport.OpenInvoices).ShouldBe((700000L, 440000L, 300000L, 2, 1));

        var revenue = (await owner.GetFromJsonAsync<RevenueReport>(Url("/reports/revenue?groupBy=day")))!;
        revenue.Total.ShouldBe(700000);
        revenue.Invoices.ShouldBe(1);
        revenue.ByKind.Single(k => k.Name == "Dịch vụ").Amount.ShouldBe(500000);
        revenue.ByKind.Single(k => k.Name == "Sản phẩm").Amount.ShouldBe(200000);
        revenue.ByStaff.Single().Name.ShouldBe("Lan");
        revenue.BySource.Single().Name.ShouldBe("Khách tại quầy");
        revenue.ByPaymentMethod.Single(m => m.Name == "Tiền mặt").Amount.ShouldBe(440000);
        revenue.Series.Count.ShouldBe(30);
        revenue.Series.Sum(s => s.Revenue).ShouldBe(700000);
        (await owner.GetFromJsonAsync<RevenueReport>(Url("/reports/revenue?groupBy=month")))!.Series.Sum(s => s.Revenue).ShouldBe(700000);

        var customers = (await owner.GetFromJsonAsync<CustomerReport>(Url("/reports/customers")))!;
        (customers.ActiveCustomers, customers.NewCustomers, customers.ReturningCustomers).ShouldBe((1, 1, 0));
        customers.TopCustomers.Single().Amount.ShouldBe(700000);

        var outstanding = (await owner.GetFromJsonAsync<OutstandingReport>(Url("/reports/outstanding")))!;
        outstanding.InvoiceDue.ShouldBe(60000);
        outstanding.Invoices.Single().Number.ShouldBe(2);
        open.Number.ShouldBe("HD-000002");

        var ai = (await owner.GetFromJsonAsync<AiEffectReport>(Url("/reports/ai")))!;
        (ai.BotBookedAppointments, ai.RevenueFromBot).ShouldBe((0, 0L));

        var occupancy = (await owner.GetFromJsonAsync<OccupancyReport>(Url("/reports/occupancy")))!;
        occupancy.Heatmap.Sum(c => c.Appointments).ShouldBe(1);

        using var excel = await owner.GetAsync(Url("/reports/revenue/export"));
        excel.StatusCode.ShouldBe(HttpStatusCode.OK);
        excel.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await excel.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(1000);

        // Hỏi bằng tiếng Việt (AI giả): doanh thu → báo cáo revenue kèm dữ liệu; thời tiết → không trả lời.
        using var ask = await owner.PostAsJsonAsync(Url("/reports/ask"), new AskReportRequest("Doanh thu tháng này bao nhiêu?"));
        ask.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await ask.Content.ReadFromJsonAsync<AskReportResponse>())!;
        answer.Report.ShouldBe("revenue");
        answer.Data.ShouldNotBeNull();
        answer.Data.Value.GetProperty("total").GetInt64().ShouldBe(700000);
        answer.Answer.ShouldNotBeNullOrWhiteSpace();
        var weather = (await (await owner.PostAsJsonAsync(Url("/reports/ask"), new AskReportRequest("Thời tiết hôm nay thế nào?"))).Content.ReadFromJsonAsync<AskReportResponse>())!;
        weather.Report.ShouldBeNull();

        // Báo cáo tuần: dựng được văn bản (không gửi vì chưa có Telegram).
        using var scope = db.Api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, null, null, isSuperAdmin: false);
        var text = await scope.ServiceProvider.GetRequiredService<WeeklyReportJob>().BuildAsync(scope.ServiceProvider.GetRequiredService<ReportService>(), tenantId, "Báo cáo", CancellationToken.None);
        text.ShouldContain("Doanh thu:");

        // Nhân viên không có quyền view_reports → 403; tenant khác → trống.
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, TenantRole.Staff));
        (await staff.GetAsync(Url("/reports/today"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (tenantB, _) = await db.CreateTenantAsync("Báo cáo B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<RevenueReport>(Url("/reports/revenue")))!.Total.ShouldBe(0);
        (await ownerB.GetFromJsonAsync<OutstandingReport>(Url("/reports/outstanding")))!.Invoices.ShouldBeEmpty();
    }
}
