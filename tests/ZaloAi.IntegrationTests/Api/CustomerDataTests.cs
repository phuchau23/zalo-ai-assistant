using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Inbox;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Xuất / xóa dữ liệu của một khách cuối (M6 đợt B2): chỉ owner, xóa sạch, giữ chi phí AI, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CustomerDataTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>Khách chat thử nhắn 1 tin có SĐT (bot trả lời, ghi chi phí AI) + nhân viên ghi 1 ghi chú.</summary>
    private async Task<(Guid TenantId, HttpClient Owner, Guid ContactId, Guid ConversationId)> CustomerAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var create = await owner.PostAsync(Url("/chat-test/conversations"), null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversation.Id}/messages"), new { text = "giá triệt lông? sđt 0912345678" });

        await using (var context = db.CreateDbContext(tenantId))
        {
            var messageId = await context.Messages.Where(m => m.ConversationId == conversation.Id).Select(m => m.Id).SingleAsync();
            using var scope = db.Api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProcessIncomingMessageJob>().RunAsync(tenantId, messageId, CancellationToken.None);
        }

        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Url($"/inbox/conversations/{conversation.Id}")))!;
        var contactId = detail.Conversation.ContactId;
        (await owner.PostAsJsonAsync(Url($"/contacts/{contactId}/notes"), new { kind = "service", text = "Đã làm gói triệt lông nách", happenedOn = "2026-10-05" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return (tenantId, owner, contactId, conversation.Id);
    }

    [Fact]
    public async Task Owner_exports_all_data_of_one_customer()
    {
        var (tenantId, owner, contactId, _) = await CustomerAsync("Xuất dữ liệu khách");

        using var response = await owner.GetAsync(Url($"/contacts/{contactId}/export"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        workbook.Worksheets.Select(w => w.Name).ShouldBe(["Thông tin", "Tin nhắn", "Nhật ký chăm sóc", "Gợi ý chăm sóc"]);
        var messages = workbook.Worksheet("Tin nhắn");
        messages.Cell(2, 2).GetString().ShouldBe("Khách");
        messages.Cell(2, 3).GetString().ShouldContain("0912345678"); // bản xuất cho chính khách: dữ liệu gốc, đã giải mã
        workbook.Worksheet("Nhật ký chăm sóc").Cell(2, 3).GetString().ShouldBe("Đã làm gói triệt lông nách");

        await using var context = db.CreateDbContext(tenantId);
        (await context.AuditLogs.CountAsync(a => a.Action == "contact.data_exported" && a.Target == contactId.ToString())).ShouldBe(1);
    }

    [Fact]
    public async Task Owner_deletes_customer_and_everything_but_ai_cost_records()
    {
        var (tenantId, owner, contactId, conversationId) = await CustomerAsync("Xóa dữ liệu khách");
        await using (var before = db.CreateDbContext(tenantId))
        {
            (await before.UsageRecords.CountAsync(u => u.ConversationId == conversationId)).ShouldBeGreaterThan(0);
        }

        (await owner.DeleteAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await owner.GetAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var context = db.CreateDbContext(tenantId);
        (await context.Contacts.CountAsync(c => c.Id == contactId)).ShouldBe(0);
        (await context.Conversations.CountAsync(c => c.Id == conversationId)).ShouldBe(0);
        (await context.Messages.CountAsync(m => m.ConversationId == conversationId)).ShouldBe(0);
        (await context.ContactNotes.CountAsync(n => n.ContactId == contactId)).ShouldBe(0);
        (await context.CareSuggestions.CountAsync(s => s.ContactId == contactId)).ShouldBe(0);
        (await context.UsageRecords.CountAsync(u => u.ConversationId == null)).ShouldBeGreaterThan(0); // chi phí vẫn còn để thống kê
        (await context.AuditLogs.CountAsync(a => a.Action == "contact.deleted")).ShouldBe(1);
    }

    [Fact]
    public async Task Staff_and_other_tenants_cannot_export_or_delete()
    {
        var (tenantA, _, contactId, _) = await CustomerAsync("Dữ liệu khách A");
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, TenantRole.Staff));
        (await staff.GetAsync(Url($"/contacts/{contactId}/export"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.DeleteAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (tenantB, _) = await db.CreateTenantAsync("Dữ liệu khách B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetAsync(Url($"/contacts/{contactId}/export"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var context = db.CreateDbContext(tenantA);
        (await context.Contacts.CountAsync(c => c.Id == contactId)).ShouldBe(1);
    }
}
