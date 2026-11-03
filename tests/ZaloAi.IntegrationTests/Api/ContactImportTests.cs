using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Customers;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Nhập danh sách khách (Excel), lịch hẹn của khách chỉ có SĐT, ghép hồ sơ khi khách chat cho SĐT, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ContactImportTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static MultipartFormDataContent File(params string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Khách hàng");
        string[] headers = ["SĐT", "Tên", "Giới tính", "Cách gọi", "Đã làm gì", "Ngày làm", "Hẹn nhắn lúc", "Ghi chú cho AI", "Đồng ý nhận tin ưu đãi"];
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];
            }
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { content, "file", "khach.xlsx" } };
    }

    private static readonly string[][] Rows =
    [
        ["0912345678", "Nguyễn Thị Lan", "Nữ", "", "Gói bấm huyệt 10 buổi", "10/10/2026", "01/10/2026 09:00", "Hay đau vai gáy", "Có"],
        ["0987654321", "Trần Văn Hùng", "Nam", "", "Massage 60 phút", "12/10/2026", "", "", ""],
        ["123", "Sai số"],
    ];

    [Fact]
    public async Task Staff_previews_and_imports_list_without_duplicates_on_reimport()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Nhập khách");
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, TenantRole.Staff));

        (await staff.GetAsync(Url("/contacts/import/template"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var previewResponse = await staff.PostAsync(Url("/contacts/import/preview"), File(Rows));
        previewResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<ContactImportPreviewResponse>())!;
        (preview.New, preview.Update, preview.Error).ShouldBe((2, 0, 1));

        var result = (await (await staff.PostAsync(Url("/contacts/import"), File(Rows))).Content.ReadFromJsonAsync<ContactImportResultResponse>())!;
        (result.Created, result.Updated, result.Skipped, result.FollowUps).ShouldBe((2, 0, 1, 1));

        var again = (await (await staff.PostAsync(Url("/contacts/import"), File(Rows))).Content.ReadFromJsonAsync<ContactImportResultResponse>())!;
        (again.Created, again.Updated, again.FollowUps).ShouldBe((0, 2, 0));

        await using var context = db.CreateDbContext(tenantId);
        var lan = await context.Contacts.SingleAsync(c => c.DisplayName == "Nguyễn Thị Lan");
        lan.Channel.ShouldBe(ChannelKind.Phone);
        lan.Salutation.ShouldBe("chị");
        lan.ConsentMarketing.ShouldBeTrue();
        lan.ConsentSource.ShouldBe("import");
        lan.PhoneEnc.ShouldNotBeNull();
        lan.PhoneEnc.ShouldNotContain("0912345678"); // mã hóa
        (await context.ContactNotes.CountAsync(n => n.ContactId == lan.Id)).ShouldBe(2); // dịch vụ + ghi chú AI, không nhân đôi
        (await context.AuditLogs.CountAsync(a => a.Action == "contacts.imported")).ShouldBe(2);

        await (await staff.PostAsync(Url("/contacts/import/preview"), new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "x.xlsx" } }))
            .ShouldBeProblemAsync(400, "invalid_input");
    }

    [Fact]
    public async Task Due_follow_ups_of_phone_only_customers_are_listed_until_marked_done()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Tới hẹn SĐT");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        (await owner.PostAsync(Url("/contacts/import"), File(Rows))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var due = (await owner.GetFromJsonAsync<List<PhoneDueItem>>(Url("/care/phone-due")))!;
        var item = due.ShouldHaveSingleItem(); // Hùng không có hẹn
        item.Name.ShouldBe("Nguyễn Thị Lan");
        item.Phone.ShouldBe("0912345678");
        item.Salutation.ShouldBe("chị");
        item.Note.ShouldBe("Gói bấm huyệt 10 buổi");

        var (tenantB, _) = await db.CreateTenantAsync("Tới hẹn SĐT B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<List<PhoneDueItem>>(Url("/care/phone-due")))!.ShouldBeEmpty();
        (await ownerB.PostAsync(Url($"/care/phone-due/{item.NoteId}/done"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await owner.PostAsync(Url($"/care/phone-due/{item.NoteId}/done"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<PhoneDueItem>>(Url("/care/phone-due")))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Customer_who_gives_the_same_phone_in_chat_absorbs_the_imported_profile()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Ghép hồ sơ");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        (await owner.PostAsync(Url("/contacts/import"), File(Rows))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Cùng SĐT ở doanh nghiệp khác: không bao giờ bị ghép chéo.
        var (tenantB, _) = await db.CreateTenantAsync("Ghép hồ sơ B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.PostAsync(Url("/contacts/import"), File(Rows))).StatusCode.ShouldBe(HttpStatusCode.OK);

        Guid chatId;
        using (var scope = db.Api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, null, null, isSuperAdmin: false);
            var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
            var context = scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.Persistence.AppDbContext>();
            var chat = new Contact
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Channel = ChannelKind.Zalo,
                ExternalUserId = "zalo-user-1",
                DisplayName = "Lan Zalo",
                LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(new Dictionary<string, string> { ["phone"] = "+84 912 345 678" })),
            };
            context.Contacts.Add(chat);
            await context.SaveChangesAsync();
            chatId = chat.Id;

            await scope.ServiceProvider.GetRequiredService<ContactPhoneLinker>().LinkAsync(tenantId, chat, "+84 912 345 678", CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await using var verify = db.CreateDbContext(tenantId);
        (await verify.Contacts.CountAsync(c => c.DisplayName == "Nguyễn Thị Lan")).ShouldBe(0); // bản nhập đã gộp
        var merged = await verify.Contacts.SingleAsync(c => c.Id == chatId);
        merged.PhoneHash.ShouldNotBeNull();
        merged.Salutation.ShouldBe("chị");
        merged.ConsentMarketing.ShouldBeTrue();
        (await verify.ContactNotes.CountAsync(n => n.ContactId == chatId)).ShouldBe(2);

        await using var verifyB = db.CreateDbContext(tenantB);
        (await verifyB.Contacts.CountAsync(c => c.DisplayName == "Nguyễn Thị Lan")).ShouldBe(1);
    }
}
