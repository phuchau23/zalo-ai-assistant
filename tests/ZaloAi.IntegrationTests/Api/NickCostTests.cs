using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Connections;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>
/// Chi phí AI theo nick quy ra VND (chủ dự án chốt 2026-11-15): USD theo ngày × giá bán USD Vietcombank đúng ngày; bản ghi cũ lấy nick
/// theo hội thoại; phần không thuộc nick tách riêng; chỉ chủ DN + super admin; cô lập tenant. Tỷ giá được nạp sẵn → không gọi mạng.
/// </summary>
[Collection(PostgresGroup.Name)]
public sealed class NickCostTests(PostgresFixture db)
{
    private static readonly DateOnly Day1 = new(2026, 10, 5);
    private static readonly DateOnly Day2 = new(2026, 10, 6);
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static DateTimeOffset At(DateOnly day, int hour) => new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, 0)), Vietnam).ToUniversalTime();

    private async Task SeedRatesAsync()
    {
        await using var context = db.CreateDbContext((Guid?)null);
        foreach (var (day, sell) in new[] { (Day1, 25000m), (Day2, 26000m) })
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO exchange_rates (currency, date, sell, transfer, source, fetched_at) VALUES ('USD', {day}, {sell}, {sell - 400}, 'test', now()) ON CONFLICT (currency, date) DO UPDATE SET sell = EXCLUDED.sell");
        }
    }

    /// <summary>Nick A: 1 lần gọi có connection_id (ngày 1) + 1 lần gọi cũ chỉ có hội thoại (ngày 2, 23:30 giờ VN). Phần khác: kho kiến thức.</summary>
    private async Task<(Guid NickA, Guid NickB)> SeedAsync(Guid tenantId, decimal scale = 1m)
    {
        await using var context = db.CreateDbContext(tenantId);
        ChannelConnection Nick(string name) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Channel = ChannelKind.ZaloPersonal,
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
            AccessTokenEnc = "x",
            RefreshTokenEnc = "x",
        };
        var a = Nick("Nick Lan");
        var b = Nick("Nick Hà");
        var contact = new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, Channel = ChannelKind.ZaloPersonal, ExternalUserId = Guid.NewGuid().ToString("N") };
        var conversation = new Conversation { Id = Guid.CreateVersion7(), TenantId = tenantId, ContactId = contact.Id, Channel = ChannelKind.ZaloPersonal, ConnectionId = a.Id };
        context.ChannelConnections.AddRange(a, b);
        context.Contacts.Add(contact);
        context.Conversations.Add(conversation);
        context.Messages.Add(new Message
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = "x",
            CreatedAt = At(Day1, 9),
        });
        UsageRecord Usage(decimal usd, DateTimeOffset at, Guid? conversationId, Guid? connectionId, UsageKind kind = UsageKind.Chat) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Kind = kind,
            Provider = "gemini",
            Model = "m",
            InputTokens = 1000,
            OutputTokens = 100,
            CostUsd = usd * scale,
            ConversationId = conversationId,
            ConnectionId = connectionId,
            CreatedAt = at,
        };
        context.UsageRecords.AddRange(
            Usage(0.0016m, At(Day1, 10), conversation.Id, a.Id),
            Usage(0.00001m, At(Day1, 10), conversation.Id, a.Id, UsageKind.Embed),
            Usage(0.002m, At(Day2, 23).AddMinutes(30), conversation.Id, connectionId: null), // bản ghi cũ: nick lấy theo hội thoại
            Usage(0.5m, At(Day1, 12), conversationId: null, connectionId: null, UsageKind.Embed)); // nạp kho kiến thức: không thuộc nick
        await context.SaveChangesAsync();
        return (a.Id, b.Id);
    }

    [Fact]
    public async Task Owner_sees_vnd_per_nick_at_each_days_vietcombank_rate()
    {
        await SeedRatesAsync();
        var (tenantId, _) = await db.CreateTenantAsync("Chi phí nick");
        var (nickA, nickB) = await SeedAsync(tenantId);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var report = (await owner.GetFromJsonAsync<NickCostReportResponse>(Url($"/channels/costs?from={Day1:yyyy-MM-dd}&to={Day2:yyyy-MM-dd}")))!;

        var a = report.Nicks.Single(n => n.ConnectionId == nickA);
        a.CostUsd.ShouldBe(0.00361m);
        // Ngày 1: 0,00161 USD × 25.000 = 40,25đ; ngày 2 (23:30 giờ VN vẫn là ngày 2): 0,002 × 26.000 = 52đ.
        a.CostVnd.ShouldBe(92.25m);
        a.Days.Select(d => d.UsdVnd).ShouldBe([25000m, 26000m]);
        a.ChatCalls.ShouldBe(2);
        a.EmbedCalls.ShouldBe(1);
        a.ContactsReached.ShouldBe(1);
        a.ContactsReplied.ShouldBe(1);
        report.Nicks.Single(n => n.ConnectionId == nickB).CostVnd.ShouldBe(0m); // nick chưa tốn đồng nào vẫn hiện
        report.Other.CostVnd.ShouldBe(12500m); // 0,5 USD × 25.000
        report.TotalVnd.ShouldBe(92.25m + 12500m);
        report.ApproximatedDays.ShouldBeEmpty();
        report.RateSource.ShouldBe("vietcombank");
    }

    [Fact]
    public async Task Only_owner_and_super_admin_can_see_costs_and_tenants_stay_isolated()
    {
        await SeedRatesAsync();
        var (tenantA, _) = await db.CreateTenantAsync("Chi phí A");
        var (tenantB, _) = await db.CreateTenantAsync("Chi phí B");
        var (nickA, _) = await SeedAsync(tenantA);
        var (nickOfB, _) = await SeedAsync(tenantB, scale: 100m);
        var range = $"from={Day1:yyyy-MM-dd}&to={Day2:yyyy-MM-dd}";

        var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        var reportA = (await ownerA.GetFromJsonAsync<NickCostReportResponse>(Url($"/channels/costs?{range}")))!;
        reportA.Nicks.ShouldNotContain(n => n.ConnectionId == nickOfB);
        reportA.TotalVnd.ShouldBe(92.25m + 12500m); // không lẫn chi phí DN B (gấp 100 lần)

        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, TenantRole.Staff));
        (await staff.GetAsync(Url($"/channels/costs?{range}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ownerA.GetAsync(Url($"/admin/tenants/{tenantA}/costs?{range}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = await db.Api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());
        var viaAdmin = (await admin.GetFromJsonAsync<NickCostReportResponse>(Url($"/admin/tenants/{tenantA}/costs?{range}")))!;
        viaAdmin.Nicks.Single(n => n.ConnectionId == nickA).CostVnd.ShouldBe(92.25m);
        (await admin.GetAsync(Url($"/admin/tenants/{Guid.NewGuid()}/costs?{range}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Range_is_validated()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Chi phí khoảng ngày");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        (await owner.GetAsync(Url("/channels/costs?from=2026-10-06&to=2026-10-05"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.GetAsync(Url("/channels/costs?from=2026-01-01&to=2026-10-05"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
