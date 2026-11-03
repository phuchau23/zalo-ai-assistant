using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Api.Tenants;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C3: hóa đơn, giảm giá có ngưỡng, voucher, thanh toán nhiều cách, gói trừ buổi, ví, hủy/hoàn, ca thu ngân, VietQR, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmBillingTests(PostgresFixture db)
{
    private const string Knowledge = """
        {
          "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000 } ],
          "packages": [ { "code": "GOI-MASSAGE-5", "name": "Gói massage 5 buổi", "sessions": 5, "minutesPerSession": 60, "price": 2000000 } ],
          "products": [ { "code": "SP-TINH-DAU", "name": "Tinh dầu tràm", "price": 120000, "unit": "chai" } ]
        }
        """;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<(Guid TenantId, HttpClient Owner, Guid ServiceId, Guid PackageId, Guid ProductId, Guid ContactId, Guid BranchId)> SetupAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
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

        // Khách: tạo qua lịch hẹn đặt tay (có SĐT).
        var staffId = await AddStaffAsync(owner, branch.Id);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        start = new DateTimeOffset(start.Year, start.Month, start.Day, 3, 0, 0, TimeSpan.Zero); // 10:00 VN
        using var booked = await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, null, new NewContactRequest("Khách Mua", "0905111222", "chị"), staffId, null, start, [service.Id], null));
        booked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appointment = (await booked.Content.ReadFromJsonAsync<AppointmentResponse>())!;
        return (tenantId, owner, service.Id, package.Id, items.Single(i => i.Kind == "product").Id, appointment.ContactId, branch.Id);
    }

    private static async Task<Guid> AddStaffAsync(HttpClient owner, Guid branchId)
    {
        using var created = await owner.PostAsJsonAsync(Url("/staff"), new SaveStaffRequest(branchId, "Lan", null, null, "#2563eb", true, null));
        var staff = (await created.Content.ReadFromJsonAsync<StaffResponse>())!;
        (await owner.PutAsJsonAsync(Url($"/branches/{branchId}/hours"), new SetBranchHoursRequest(Enumerable.Range(0, 7).Select(d => new BranchHourItem(d, "00:00", "23:59")).ToList()))).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync(Url($"/staff/{staff.Id}/shift-templates"), new SetShiftTemplatesRequest(Enumerable.Range(0, 7).Select(d => new ShiftTemplateItem(d, "00:00", "23:59")).ToList()))).EnsureSuccessStatusCode();
        return staff.Id;
    }

    private static async Task<InvoiceResponse> CreateAsync(HttpClient client, Guid contactId, params InvoiceLineRequest[] lines)
    {
        using var created = await client.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contactId, null, lines, "ghi chú nội bộ"));
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<InvoiceResponse>())!;
    }

    [Fact]
    public async Task Invoice_with_discount_voucher_and_split_payments_settles_and_records_revenue()
    {
        var (tenantId, owner, serviceId, _, productId, contactId, _) = await SetupAsync("Hóa đơn");
        (await owner.PostAsJsonAsync(Url("/vouchers"), new SaveVoucherRequest("GIAM10", "percent", 10, 100000, 0, null, null, 5))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var invoice = await CreateAsync(owner, contactId, new InvoiceLineRequest(serviceId, null, null), new InvoiceLineRequest(productId, null, null, 2));
        (invoice.Number, invoice.Status, invoice.Subtotal, invoice.Total, invoice.Lines.Count).ShouldBe(("HD-000001", "open", 690000L, 690000L, 2));
        invoice.Note.ShouldBe("ghi chú nội bộ");

        // Giảm 50.000 + voucher 10% (tối đa 100.000) → tổng 576.000.
        using var discounted = await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/discount"), new ApplyDiscountRequest(50000, "khách quen", "giam10"));
        discounted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await discounted.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (after.DiscountAmount, after.VoucherCode, after.VoucherAmount, after.Total).ShouldBe((50000L, "GIAM10", 64000L, 576000L));
        await (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/discount"), new ApplyDiscountRequest(0, null, "SAI"))).ShouldBeProblemAsync(409, "voucher_invalid");

        // Thu 2 lần: tiền mặt 300.000 → partial; chuyển khoản phần còn lại → paid. Thu quá → 409.
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("cash", 300000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("transfer", 300000, null))).ShouldBeProblemAsync(409, "overpayment");
        using var paid = await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("transfer", 276000, "FT123"));
        var final = (await paid.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (final.Status, final.PaidAmount, final.Due, final.Payments.Count).ShouldBe(("paid", 576000L, 0L, 2));
        await (await owner.PutAsJsonAsync(Url($"/invoices/{invoice.Id}/lines"), new UpdateInvoiceLinesRequest([new InvoiceLineRequest(serviceId, null, null)], null))).ShouldBeProblemAsync(409, "invoice_state");

        await using (var context = db.CreateDbContext(tenantId))
        {
            var revenue = await context.RevenueEntries.Where(r => r.InvoiceId == invoice.Id).ToListAsync();
            revenue.Sum(r => r.Amount).ShouldBe(576000); // doanh thu = tổng thu, giảm giá chia theo tỷ lệ
            revenue.Select(r => r.Kind).OrderBy(k => k).ShouldBe([RevenueKind.Service, RevenueKind.Product]);
            (await context.Vouchers.SingleAsync(v => v.Code == "GIAM10")).UsedCount.ShouldBe(1);
            (await context.Invoices.SingleAsync(i => i.Id == invoice.Id)).NoteEnc!.ShouldNotContain("ghi chú nội bộ");
        }

        // Phiếu thu: chữ đúng; khách chỉ có SĐT → không gửi được qua kênh (false).
        var receipt = (await (await owner.PostAsync(Url($"/invoices/{invoice.Id}/receipt/send"), null)).Content.ReadFromJsonAsync<ReceiptResponse>())!;
        receipt.Sent.ShouldBeFalse();
        receipt.Text.ShouldContain("PHIẾU THU HD-000001");
        receipt.Text.ShouldContain("Tổng: 576.000 đ");

        // Hoàn 100.000 tiền mặt (cần quyền refund: chủ có) → refunded + doanh thu âm.
        using var refunded = await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/refund"), new RefundInvoiceRequest(100000, "cash", "khách không hài lòng"));
        refunded.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refunded.Content.ReadFromJsonAsync<InvoiceResponse>())!.RefundedAmount.ShouldBe(100000);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.RevenueEntries.Where(r => r.InvoiceId == invoice.Id).SumAsync(r => r.Amount)).ShouldBe(476000);
        }

        (await owner.GetFromJsonAsync<List<InvoiceSummaryResponse>>(Url("/invoices")))!.ShouldHaveSingleItem().Number.ShouldBe("HD-000001");
    }

    [Fact]
    public async Task Package_purchase_redemption_auto_redeem_and_void_restore_sessions()
    {
        var (tenantId, owner, serviceId, packageId, _, contactId, _) = await SetupAsync("Gói");

        var sale = await CreateAsync(owner, contactId, new InvoiceLineRequest(packageId, null, null));
        (await owner.PostAsJsonAsync(Url($"/invoices/{sale.Id}/payments"), new AddPaymentRequest("card", 2000000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var packages = (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!;
        var owned = packages.ShouldHaveSingleItem();
        (owned.TotalSessions, owned.UsedSessions, owned.Remaining, owned.Status, owned.PricePaid).ShouldBe((5, 0, 5, "active", 2000000L));

        // Hóa đơn từ lịch hẹn: dịch vụ được gói bao → dòng dùng buổi, tổng 0 → chốt không thu tiền → trừ 1 buổi + doanh thu gói 400.000.
        var appointmentId = (await owner.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(3).ToString("o"))}")))!.Single().Id;
        using var fromAppointment = await owner.PostAsync(Url($"/invoices/from-appointment/{appointmentId}"), null);
        fromAppointment.StatusCode.ShouldBe(HttpStatusCode.OK);
        var redemption = (await fromAppointment.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        redemption.Lines.ShouldHaveSingleItem().Kind.ShouldBe("redemption");
        redemption.Total.ShouldBe(0);
        (await owner.PostAsync(Url($"/invoices/{redemption.Id}/close-zero"), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsync(Url($"/invoices/from-appointment/{appointmentId}"), null)).StatusCode.ShouldBe(HttpStatusCode.OK); // trả lại hóa đơn đã có
        (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.Single().UsedSessions.ShouldBe(1);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.RevenueEntries.Where(r => r.Kind == RevenueKind.PackageRecognized).SumAsync(r => r.Amount)).ShouldBe(400000);
        }

        // Hoàn thành lịch khi đã có hóa đơn → không trừ thêm; hủy hóa đơn dùng buổi → hoàn lại buổi.
        (await owner.PatchAsJsonAsync(Url($"/appointments/{appointmentId}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.Single().UsedSessions.ShouldBe(1);
        (await owner.PostAsJsonAsync(Url($"/invoices/{redemption.Id}/void"), new VoidInvoiceRequest("nhầm khách"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.Single().UsedSessions.ShouldBe(0);

        // Hủy hóa đơn mua gói khi gói chưa dùng → gói bị hủy; bảo lưu / chuyển nhượng.
        (await owner.PostAsJsonAsync(Url($"/invoices/{sale.Id}/void"), new VoidInvoiceRequest("khách đổi ý"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.Single().Status.ShouldBe("cancelled");

        var sale2 = await CreateAsync(owner, contactId, new InvoiceLineRequest(packageId, null, null));
        (await owner.PostAsJsonAsync(Url($"/invoices/{sale2.Id}/payments"), new AddPaymentRequest("cash", 2000000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var active = (await owner.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.Single(p => p.Status == "active");
        (await owner.PatchAsJsonAsync(Url($"/customer-packages/{active.Id}/status"), new SetPackageStatusRequest("paused"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PatchAsJsonAsync(Url($"/customer-packages/{active.Id}/status"), new SetPackageStatusRequest("active"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var friend = (await (await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, null, new NewContactRequest("Bạn Thân", "0905333444", "anh"), null, null, new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(2).Date, TimeSpan.Zero).AddHours(4), [serviceId], null))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        using var transferred = await owner.PostAsJsonAsync(Url($"/customer-packages/{active.Id}/transfer"), new TransferPackageRequest(friend.ContactId));
        transferred.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await transferred.Content.ReadFromJsonAsync<CustomerPackageResponse>())!.ContactId.ShouldBe(friend.ContactId);
    }

    [Fact]
    public async Task Wallet_cash_shift_bank_qr_and_discount_limit_for_staff()
    {
        var (tenantId, owner, serviceId, _, _, contactId, branchId) = await SetupAsync("Ví & ca");
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staff = await db.Api.CreateLoggedInClientAsync(staffEmail);

        // Ví: nạp 1.000.000 + thưởng 100.000; thanh toán hóa đơn bằng ví.
        using var topup = await owner.PostAsJsonAsync(Url($"/contacts/{contactId}/wallet/topup"), new WalletTopupRequest(1000000, 100000, "nạp thẻ"));
        topup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await topup.Content.ReadFromJsonAsync<WalletResponse>())!.Balance.ShouldBe(1100000);

        (await owner.PostAsJsonAsync(Url("/cash-shifts"), new OpenCashShiftRequest(branchId, 500000))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await owner.PostAsJsonAsync(Url("/cash-shifts"), new OpenCashShiftRequest(branchId, 0))).ShouldBeProblemAsync(409, "shift_open");

        var invoice = await CreateAsync(staff, contactId, new InvoiceLineRequest(serviceId, null, null));
        // Nhân viên giảm 30% (> ngưỡng 20%) → cấm; 10% → được.
        (await staff.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/discount"), new ApplyDiscountRequest(135000, "quen", null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/discount"), new ApplyDiscountRequest(45000, "quen", null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("wallet", 405000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<WalletResponse>(Url($"/contacts/{contactId}/wallet")))!.Balance.ShouldBe(695000);
        (await staff.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/void"), new VoidInvoiceRequest("thử"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // nhân viên không có quyền hủy

        // Tiền mặt vào ca + chốt ca.
        var cashInvoice = await CreateAsync(owner, contactId, new InvoiceLineRequest(serviceId, null, null));
        (await owner.PostAsJsonAsync(Url($"/invoices/{cashInvoice.Id}/payments"), new AddPaymentRequest("cash", 450000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var current = (await owner.GetFromJsonAsync<CashShiftResponse>(Url($"/cash-shifts/current?branchId={branchId}")))!;
        (current.CashIn, current.Wallet, current.InvoiceCount).ShouldBe((450000L, 405000L, 2));
        using var closed = await owner.PostAsJsonAsync(Url($"/cash-shifts/{current.Id}/close"), new CloseCashShiftRequest(940000, "thiếu 10k"));
        closed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = (await closed.Content.ReadFromJsonAsync<CashShiftResponse>())!;
        (summary.ExpectedCash, summary.CountedCash).ShouldBe((950000L, 940000L));

        // Tài khoản ngân hàng + VietQR cho hóa đơn còn nợ.
        var open = await CreateAsync(owner, contactId, new InvoiceLineRequest(serviceId, null, null));
        await (await owner.GetAsync(Url($"/invoices/{open.Id}/transfer-qr"))).ShouldBeProblemAsync(404, "no_bank_account");
        (await staff.PostAsJsonAsync(Url("/tenant/bank-accounts"), new SaveBankAccountRequest(null, "970436", "Vietcombank", "0123456789", "Nguyen Van A", true))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var bank = await owner.PostAsJsonAsync(Url("/tenant/bank-accounts"), new SaveBankAccountRequest(null, "970436", "Vietcombank", "0123 456 789", "Nguyen Van A", true));
        bank.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bank.Content.ReadFromJsonAsync<BankAccountResponse>())!.AccountNoMasked.ShouldBe("******6789");
        var qr = (await owner.GetFromJsonAsync<TransferQrResponse>(Url($"/invoices/{open.Id}/transfer-qr")))!;
        (qr.Amount, qr.AccountNo, qr.Purpose).ShouldBe((450000L, "0123456789", "HD000003"));
        qr.Payload.ShouldContain("5406450000");
        qr.PngBase64.Length.ShouldBeGreaterThan(100);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.TenantBankAccounts.SingleAsync()).AccountNoEnc.ShouldNotContain("0123456789");
        }
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_pay_tenant_A_invoices()
    {
        var (_, ownerA, serviceId, _, _, contactId, _) = await SetupAsync("Bán A");
        var invoice = await CreateAsync(ownerA, contactId, new InvoiceLineRequest(serviceId, null, null));

        var (tenantB, _) = await db.CreateTenantAsync("Bán B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<List<InvoiceSummaryResponse>>(Url("/invoices")))!.ShouldBeEmpty();
        (await ownerB.GetAsync(Url($"/invoices/{invoice.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("cash", 1000, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/void"), new VoidInvoiceRequest("x"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contactId, null, [new InvoiceLineRequest(serviceId, null, null)], null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetFromJsonAsync<WalletResponse>(Url($"/contacts/{contactId}/wallet")))!.Balance.ShouldBe(0);
        (await ownerB.GetFromJsonAsync<List<CustomerPackageResponse>>(Url($"/customer-packages?contactId={contactId}")))!.ShouldBeEmpty();

        (await ownerA.GetFromJsonAsync<InvoiceResponse>(Url($"/invoices/{invoice.Id}")))!.Status.ShouldBe("open");
    }
}
