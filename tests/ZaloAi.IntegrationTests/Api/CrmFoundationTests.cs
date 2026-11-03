using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Api.Tenants;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C1: chi nhánh mặc định + quyền chi tiết, danh mục ↔ kho kiến thức, nhân viên + ca làm, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmFoundationTests(PostgresFixture db)
{
    private const string Knowledge = """
        {
          "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000, "group": "Massage" } ],
          "packages": [ { "code": "GOI-MASSAGE-10", "name": "Gói massage 10 buổi", "sessions": 10, "minutesPerSession": 60, "price": 4000000 } ],
          "products": [ { "code": "SP-TINH-DAU", "name": "Tinh dầu tràm 50ml", "price": 120000, "unit": "chai", "sku": "TD-050" } ]
        }
        """;

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task ImportAsync(HttpClient client, string json)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(json)), "file", "data.json" } };
        using var upload = await client.PostAsync(Url("/knowledge/imports"), form);
        upload.EnsureSuccessStatusCode();
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        using var apply = await client.PostAsJsonAsync(Url($"/knowledge/imports/{preview.Id}/apply"), new { selections = preview.Items.Select(i => new { key = i.Key }) });
        apply.EnsureSuccessStatusCode();
    }

    private static SaveStaffRequest Staff(Guid branchId, string name) => new(branchId, name, "Kỹ thuật viên", "0912345678", "#ff8800", true, null);

    [Fact]
    public async Task Default_branch_is_created_lazily_and_management_needs_permission()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Chi nhánh");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staff = await db.Api.CreateLoggedInClientAsync(staffEmail);

        var branches = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!;
        var main = branches.ShouldHaveSingleItem();
        main.IsDefault.ShouldBeTrue();
        main.Code.ShouldBe(BranchService.DefaultCode);
        (await owner.GetFromJsonAsync<BranchDetailResponse>(Url($"/branches/{main.Id}")))!.Hours.Count.ShouldBe(7);

        // Nhân viên xem được, không sửa được; chủ bật quyền → sửa được ở request kế tiếp.
        (await staff.GetAsync(Url("/branches"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var create = new SaveBranchRequest("q12", "Chi nhánh Quận 12", "1 Lê Văn Khương", null);
        (await staff.PostAsJsonAsync(Url("/branches"), create)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var staffId = (await owner.GetFromJsonAsync<List<TeamMemberResponse>>(Url("/tenant/team")))!.Single(m => m.Email == staffEmail).UserId;
        (await owner.PutAsJsonAsync(Url($"/tenant/team/{staffId}/permissions"), new SetTeamPermissionsRequest([TenantPermissions.ManageBranches], null)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var created = await staff.PostAsJsonAsync(Url("/branches"), create);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var q12 = (await created.Content.ReadFromJsonAsync<BranchResponse>())!;
        q12.Code.ShouldBe("Q12");
        q12.IsDefault.ShouldBeFalse();
        await (await staff.PostAsJsonAsync(Url("/branches"), create)).ShouldBeProblemAsync(409, "duplicate_code");

        var me = (await staff.GetFromJsonAsync<MeResponse>(Url("/auth/me")))!;
        me.CurrentTenant!.Permissions.ShouldBe([TenantPermissions.ManageBranches]);
        me.CurrentTenant.BranchId.ShouldBeNull();

        // Giới hạn nhân viên vào Q12 (bỏ quyền quản lý): chỉ thấy Q12, chi nhánh mặc định thành 404.
        (await owner.PutAsJsonAsync(Url($"/tenant/team/{staffId}/permissions"), new SetTeamPermissionsRequest([], q12.Id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Select(b => b.Id).ShouldBe([q12.Id]);
        (await staff.GetAsync(Url($"/branches/{main.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await staff.GetFromJsonAsync<MeResponse>(Url("/auth/me")))!.CurrentTenant!.BranchId.ShouldBe(q12.Id);

        // Giờ mở cửa + ngày nghỉ.
        var hours = new SetBranchHoursRequest([new BranchHourItem(1, "08:00", "12:00"), new BranchHourItem(1, "13:30", "20:00")]);
        (await owner.PutAsJsonAsync(Url($"/branches/{q12.Id}/hours"), hours)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await owner.PutAsJsonAsync(Url($"/branches/{q12.Id}/hours"), new SetBranchHoursRequest([new BranchHourItem(1, "08:00", "12:00"), new BranchHourItem(1, "11:00", "20:00")])))
            .ShouldBeProblemAsync(400, "invalid_input");
        using var closure = await owner.PostAsJsonAsync(Url($"/branches/{q12.Id}/closures"), new AddBranchClosureRequest("2026-12-25", "Nghỉ lễ"));
        closure.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await owner.GetFromJsonAsync<BranchDetailResponse>(Url($"/branches/{q12.Id}")))!;
        detail.Hours.Count.ShouldBe(2);
        detail.Closures.ShouldHaveSingleItem().Date.ShouldBe("2026-12-25");

        // Không ngừng chi nhánh mặc định; đổi mặc định rồi ngừng được.
        await (await owner.PatchAsJsonAsync(Url($"/branches/{main.Id}/status"), new SetBranchStatusRequest("inactive"))).ShouldBeProblemAsync(409, "default_branch");
        (await owner.PostAsync(Url($"/branches/{q12.Id}/default"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PatchAsJsonAsync(Url($"/branches/{main.Id}/status"), new SetBranchStatusRequest("inactive"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.ShouldHaveSingleItem().Id.ShouldBe(q12.Id);
        (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches?includeInactive=true")))!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Knowledge_import_fills_catalog_and_catalog_edits_flow_back_to_knowledge()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Danh mục");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ImportAsync(owner, Knowledge);

        var items = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!;
        items.Count.ShouldBe(3);
        var service = items.Single(i => i.Code == "DV-MASSAGE-60");
        (service.Kind, service.Price, service.DurationMinutes, service.GroupName, service.Unit).ShouldBe(("service", 450000L, 60, "Massage", "lượt"));
        var package = items.Single(i => i.Code == "GOI-MASSAGE-10");
        (package.Kind, package.SessionCount, package.DurationMinutes, package.Price).ShouldBe(("package", 10, 60, 4000000L));
        var product = items.Single(i => i.Code == "SP-TINH-DAU");
        (product.Kind, product.Unit, product.Sku, product.InKnowledge).ShouldBe(("product", "chai", "TD-050", true));
        (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog?kind=product")))!.ShouldHaveSingleItem().Code.ShouldBe("SP-TINH-DAU");

        // Sửa giá ở danh mục → kho kiến thức đổi theo, cờ duyệt chuyên môn giữ nguyên, đoạn được tạo lại để đánh chỉ mục.
        await using (var context = db.CreateDbContext(tenantId))
        {
            await context.KnowledgeItems.Where(k => k.Code == "DV-MASSAGE-60").ExecuteUpdateAsync(s => s.SetProperty(k => k.MedicallyReviewed, true));
        }

        var update = new SaveCatalogItemRequest("service", "DV-MASSAGE-60", "Massage Đông y 60 phút", "Massage", 500000, null, 75, 15, null, null, null, "lượt", null, false);
        (await owner.PutAsJsonAsync(Url($"/catalog/{service.Id}"), update)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var context = db.CreateDbContext(tenantId))
        {
            var knowledge = await context.KnowledgeItems.SingleAsync(k => k.Code == "DV-MASSAGE-60");
            var data = System.Text.Json.JsonDocument.Parse(knowledge.DataJson).RootElement;
            data.GetProperty("price").GetInt64().ShouldBe(500000);
            data.GetProperty("durationMinutes").GetInt64().ShouldBe(75);
            knowledge.MedicallyReviewed.ShouldBeTrue();
            (await context.Chunks.CountAsync(c => c.KnowledgeItemId == knowledge.Id)).ShouldBe(1);
        }

        // Tạo sản phẩm từ danh mục → có mục kiến thức loại Sản phẩm cho bot.
        var newProduct = new SaveCatalogItemRequest("product", "sp-kem-duong", "Kem dưỡng", "Mỹ phẩm", 350000, null, null, 0, null, null, "KD-01", "hộp", 200000, true);
        using var created = await owner.PostAsJsonAsync(Url("/catalog"), newProduct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var createdItem = (await created.Content.ReadFromJsonAsync<CatalogItemResponse>())!;
        (createdItem.Code, createdItem.TrackInventory, createdItem.CostPrice, createdItem.InKnowledge).ShouldBe(("SP-KEM-DUONG", true, 200000L, true));
        await (await owner.PostAsJsonAsync(Url("/catalog"), newProduct)).ShouldBeProblemAsync(409, "duplicate_code");
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.KnowledgeItems.SingleAsync(k => k.Code == "SP-KEM-DUONG")).Kind.ShouldBe(KnowledgeKind.Product);
        }

        // Nội dung gói: 10 buổi massage; gán dịch vụ của loại khác → lỗi.
        (await owner.PutAsJsonAsync(Url($"/catalog/{package.Id}/contents"), new SetPackageContentsRequest([new PackageContentItem(service.Id, 10)]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync(Url($"/catalog/{package.Id}/contents"), new SetPackageContentsRequest([new PackageContentItem(product.Id, 1)]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetFromJsonAsync<CatalogItemDetailResponse>(Url($"/catalog/{package.Id}")))!.Contents.ShouldHaveSingleItem().SessionCount.ShouldBe(10);

        // Nhập lại file không còn sản phẩm tinh dầu → mục danh mục ngừng bán (giữ lịch sử).
        await ImportAsync(owner, """{ "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 75, "price": 500000, "group": "Massage" } ], "packages": [ { "code": "GOI-MASSAGE-10", "name": "Gói massage 10 buổi", "sessions": 10, "minutesPerSession": 60, "price": 4000000 } ], "products": [ { "code": "SP-KEM-DUONG", "name": "Kem dưỡng", "price": 350000 } ] }""");
        var after = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog?includeInactive=true")))!;
        after.Single(i => i.Code == "SP-TINH-DAU").IsActive.ShouldBeFalse();
        after.Single(i => i.Code == "SP-KEM-DUONG").IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Staff_members_skills_shifts_and_work_windows()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Nhân viên");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ImportAsync(owner, Knowledge);
        var branch = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();
        var service = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog?kind=service")))!.Single();

        using var created = await owner.PostAsJsonAsync(Url("/staff"), Staff(branch.Id, "Lan"));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lan = (await created.Content.ReadFromJsonAsync<StaffResponse>())!;
        (await owner.PostAsJsonAsync(Url("/staff"), Staff(Guid.NewGuid(), "Sai chi nhánh"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await owner.PutAsJsonAsync(Url($"/staff/{lan.Id}/services"), new SetStaffServicesRequest([service.Id]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync(Url($"/staff/{lan.Id}/services"), new SetStaffServicesRequest([Guid.NewGuid()]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Ca cố định T2–T6 9:00–18:00; thứ 4 (2026-10-21) ghi đè ca tay 13:00–21:00; thứ 5 nghỉ.
        var templates = Enumerable.Range(1, 5).Select(d => new ShiftTemplateItem(d, "09:00", "18:00")).ToList();
        (await owner.PutAsJsonAsync(Url($"/staff/{lan.Id}/shift-templates"), new SetShiftTemplatesRequest(templates))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync(Url($"/staff/{lan.Id}/shifts"), new SetDayShiftsRequest("2026-10-21", [new DayShiftItem(null, "13:00", "21:00")]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync(Url($"/staff/{lan.Id}/shifts"), new SetDayShiftsRequest("2026-10-22", []))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = (await owner.GetFromJsonAsync<StaffDetailResponse>(Url($"/staff/{lan.Id}")))!;
        detail.Phone.ShouldBe("0912345678");
        detail.ServiceIds.ShouldBe([service.Id]);
        detail.ShiftTemplates.Count.ShouldBe(5);
        detail.TimeOff.ShouldHaveSingleItem();
        (await owner.GetFromJsonAsync<List<StaffShiftResponse>>(Url("/staff/shifts?from=2026-10-19&to=2026-10-25")))!.ShouldHaveSingleItem().StartTime.ShouldBe("13:00");
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.StaffMembers.SingleAsync()).PhoneEnc!.ShouldNotContain("0912345678");
        }

        using var scope = db.Api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, null, null, isSuperAdmin: false);
        var staff = scope.ServiceProvider.GetRequiredService<StaffService>();
        (await staff.WorkWindowsAsync(tenantId, branch.Id, new DateOnly(2026, 10, 20), CancellationToken.None)).ShouldHaveSingleItem().Start.ShouldBe(new TimeOnly(9, 0));
        (await staff.WorkWindowsAsync(tenantId, branch.Id, new DateOnly(2026, 10, 21), CancellationToken.None)).ShouldHaveSingleItem().Start.ShouldBe(new TimeOnly(13, 0));
        (await staff.WorkWindowsAsync(tenantId, branch.Id, new DateOnly(2026, 10, 22), CancellationToken.None)).ShouldBeEmpty();
        (await staff.WorkWindowsAsync(tenantId, branch.Id, new DateOnly(2026, 10, 25), CancellationToken.None)).ShouldBeEmpty(); // chủ nhật không có ca
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_touch_tenant_A_crm_data()
    {
        var (tenantA, _) = await db.CreateTenantAsync("CRM A");
        var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        await ImportAsync(ownerA, Knowledge);
        var branchA = (await ownerA.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();
        var itemA = (await ownerA.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog?kind=service")))!.Single();
        var staffA = (await (await ownerA.PostAsJsonAsync(Url("/staff"), Staff(branchA.Id, "A"))).Content.ReadFromJsonAsync<StaffResponse>())!;

        var (tenantB, _) = await db.CreateTenantAsync("CRM B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        (await ownerB.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.ShouldAllBe(b => b.Id != branchA.Id);
        (await ownerB.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!.ShouldBeEmpty();
        (await ownerB.GetFromJsonAsync<List<StaffResponse>>(Url("/staff")))!.ShouldBeEmpty();
        (await ownerB.GetAsync(Url($"/branches/{branchA.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetAsync(Url($"/catalog/{itemA.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetAsync(Url($"/staff/{staffA.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PutAsJsonAsync(Url($"/branches/{branchA.Id}"), new SaveBranchRequest("X", "Chiếm", null, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PatchAsJsonAsync(Url($"/catalog/{itemA.Id}/active"), new SetCatalogActiveRequest(false))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url("/staff"), Staff(branchA.Id, "Lạ"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Cùng mã ở hai doanh nghiệp là bình thường, không đụng nhau.
        await ImportAsync(ownerB, Knowledge);
        (await ownerA.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog")))!.Single(i => i.Code == "DV-MASSAGE-60").IsActive.ShouldBeTrue();
        await using var verifyA = db.CreateDbContext(tenantA);
        (await verifyA.Branches.CountAsync()).ShouldBe(1);
        (await verifyA.CatalogItems.CountAsync()).ShouldBe(3);
    }
}
