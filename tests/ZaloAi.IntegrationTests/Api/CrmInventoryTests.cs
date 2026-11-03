using System.Net;
using System.Net.Http.Json;
using System.Text;
using Shouldly;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C4: nhập hàng + giá vốn bình quân, bán trừ kho, hủy trả kho, định mức vật tư theo buổi, chuyển kho, kiểm kê, cảnh báo, quyền, cô lập.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmInventoryTests(PostgresFixture db)
{
    private const string Knowledge = """
        {
          "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000 } ],
          "products": [ { "code": "SP-TINH-DAU", "name": "Tinh dầu tràm", "price": 120000, "unit": "chai" }, { "code": "SP-DAU-MASSAGE", "name": "Dầu massage 1L", "price": 300000, "unit": "chai" } ]
        }
        """;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<(Guid TenantId, HttpClient Owner, Guid BranchId, Guid ServiceId, Guid OilId, Guid BulkOilId)> SetupAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Knowledge)), "file", "data.json" } };
        using var upload = await owner.PostAsync(Url("/knowledge/imports"), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/knowledge/imports/{preview.Id}/apply"), new { selections = preview.Items.Select(i => new { key = i.Key }) })).EnsureSuccessStatusCode();
        var items = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!;
        var oil = items.Single(i => i.Code == "SP-TINH-DAU");
        var bulk = items.Single(i => i.Code == "SP-DAU-MASSAGE");
        foreach (var p in new[] { oil, bulk })
        {
            (await owner.PutAsJsonAsync(Url($"/catalog/{p.Id}"), new SaveCatalogItemRequest("product", p.Code, p.Name, null, p.Price, null, null, 0, null, null, null, p.Unit, p.Price / 2, true))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var branch = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();
        return (tenantId, owner, branch.Id, items.Single(i => i.Kind == "service").Id, oil.Id, bulk.Id);
    }

    [Fact]
    public async Task Purchase_sale_void_consumables_transfer_count_and_low_stock()
    {
        var (_, owner, branchId, serviceId, oilId, bulkId) = await SetupAsync("Kho");

        // Nhập 10 chai @100.000 rồi 10 chai @120.000 → tồn 20, giá vốn bình quân 110.000.
        (await owner.PostAsJsonAsync(Url("/inventory/purchase-orders"), new CreatePurchaseOrderRequest(branchId, null, [new PurchaseLineRequest(oilId, 10, 100000)], "lô 1"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await owner.PostAsJsonAsync(Url("/inventory/purchase-orders"), new CreatePurchaseOrderRequest(branchId, null, [new PurchaseLineRequest(oilId, 10, 120000), new PurchaseLineRequest(bulkId, 2, 250000)], null));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = (await second.Content.ReadFromJsonAsync<PurchaseOrderResponse>())!;
        (order.Number, order.Status, order.Total).ShouldBe((2, "received", 1700000L));
        var levels = (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={branchId}")))!;
        var oilLevel = levels.Single(l => l.CatalogItemId == oilId);
        (oilLevel.Quantity, oilLevel.AvgCost).ShouldBe((20m, 110000L));

        // Bán 3 chai (hóa đơn thu đủ) → tồn 17; hủy hóa đơn → trả về 20.
        var contact = (await (await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, null, new NewContactRequest("Khách Kho", "0905777888", null), null, null, new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(1).Date, TimeSpan.Zero).AddHours(5), [serviceId], null, 1, "staff", true))).Content.ReadFromJsonAsync<AppointmentResponse>())!;
        using var sale = await owner.PostAsJsonAsync(Url("/invoices"), new CreateInvoiceRequest(null, contact.ContactId, null, [new InvoiceLineRequest(oilId, null, null, 3)], null));
        var invoice = (await sale.Content.ReadFromJsonAsync<InvoiceResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/payments"), new AddPaymentRequest("cash", 360000, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={branchId}")))!.Single(l => l.CatalogItemId == oilId).Quantity.ShouldBe(17m);
        (await owner.PostAsJsonAsync(Url($"/invoices/{invoice.Id}/void"), new VoidInvoiceRequest("trả hàng"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={branchId}")))!.Single(l => l.CatalogItemId == oilId).Quantity.ShouldBe(20m);

        // Định mức: mỗi buổi massage dùng 0.05 chai dầu 1L → hoàn thành lịch trừ 0.05; lặp lại không trừ thêm.
        (await owner.PutAsJsonAsync(Url($"/inventory/consumables/{serviceId}"), new SetConsumablesRequest([new ConsumableResponse(bulkId, 0.05m)]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PatchAsJsonAsync(Url($"/appointments/{contact.Id}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={branchId}")))!.Single(l => l.CatalogItemId == bulkId).Quantity.ShouldBe(1.95m);
        var movements = (await owner.GetFromJsonAsync<List<StockMovementResponse>>(Url($"/inventory/movements?catalogItemId={bulkId}")))!;
        movements.Count(m => m.Kind == "consume").ShouldBe(1);

        // Chuyển kho sang chi nhánh 2; kiểm kê chi nhánh 2 đếm thiếu; cảnh báo dưới mức tối thiểu.
        var q12 = (await (await owner.PostAsJsonAsync(Url("/branches"), new SaveBranchRequest("Q12", "Quận 12", null, null))).Content.ReadFromJsonAsync<BranchResponse>())!;
        (await owner.PostAsJsonAsync(Url("/inventory/transfers"), new CreateTransferRequest(branchId, q12.Id, [new TransferLineRequest(oilId, 5)], null))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={q12.Id}")))!.Single(l => l.CatalogItemId == oilId).Quantity.ShouldBe(5m);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={branchId}")))!.Single(l => l.CatalogItemId == oilId).Quantity.ShouldBe(15m);
        using var count = await owner.PostAsJsonAsync(Url("/inventory/counts"), new CreateStockCountRequest(q12.Id, [new CountLineRequest(oilId, 4)], "kiểm kê tối"));
        count.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await count.Content.ReadFromJsonAsync<StockCountResponse>())!.Status.ShouldBe("applied");
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url($"/inventory/levels?branchId={q12.Id}")))!.Single(l => l.CatalogItemId == oilId).Quantity.ShouldBe(4m);
        (await owner.PutAsJsonAsync(Url("/inventory/minimum"), new SetStockMinimumRequest(q12.Id, oilId, 5))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url("/inventory/levels?lowOnly=true")))!.ShouldHaveSingleItem().BranchId.ShouldBe(q12.Id);
        (await owner.PostAsJsonAsync(Url("/inventory/adjust"), new AdjustStockRequest(q12.Id, oilId, 2, "tìm thấy thêm"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<List<StockLevelResponse>>(Url("/inventory/levels?lowOnly=true")))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Staff_needs_permission_and_tenant_B_sees_nothing()
    {
        var (tenantId, owner, branchId, _, oilId, _) = await SetupAsync("Kho quyền");
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, TenantRole.Staff));
        (await staff.GetAsync(Url("/inventory/levels"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.PostAsJsonAsync(Url("/inventory/adjust"), new AdjustStockRequest(branchId, oilId, 1, "x"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync(Url("/inventory/adjust"), new AdjustStockRequest(branchId, oilId, 1, "x"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (tenantB, _) = await db.CreateTenantAsync("Kho B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<List<StockLevelResponse>>(Url("/inventory/levels")))!.ShouldBeEmpty();
        (await ownerB.GetFromJsonAsync<List<StockMovementResponse>>(Url("/inventory/movements")))!.ShouldBeEmpty();
        (await ownerB.PostAsJsonAsync(Url("/inventory/adjust"), new AdjustStockRequest(branchId, oilId, 1, "x"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
