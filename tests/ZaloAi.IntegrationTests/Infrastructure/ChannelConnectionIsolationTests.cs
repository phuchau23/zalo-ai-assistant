using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.IntegrationTests.Infrastructure;

/// <summary>Cô lập tenant cho kênh đã kết nối (token OA) + tra cứu hệ thống của webhook.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ChannelConnectionIsolationTests(PostgresFixture db)
{
    private sealed record Scope(TenantContext Context, AppDbContext Db) : IAsyncDisposable
    {
        public ChannelConnectionRepository Connections => new(Db, Context);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Scope As(Guid tenantId)
    {
        var context = PostgresFixture.CreateTenantContext(tenantId);
        return new Scope(context, db.CreateDbContext(context));
    }

    private static string NewOaId() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<ChannelConnection> ConnectAsync(Guid tenantId, string oaId)
    {
        await using var s = As(tenantId);
        var connection = s.Connections.Add(tenantId, new ChannelConnection
        {
            Channel = ChannelKind.Zalo,
            ExternalId = oaId,
            AccessTokenEnc = "enc-access",
            RefreshTokenEnc = "enc-refresh",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(25),
            RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddMonths(3),
        });
        await s.Db.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_load_tenant_A_connection()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Kênh A");
        var (tenantB, _) = await db.CreateTenantAsync("Kênh B");
        var oaId = NewOaId();
        var connection = await ConnectAsync(tenantA, oaId);

        await using var asB = As(tenantB);
        (await asB.Connections.ListAsync(tenantB, CancellationToken.None)).ShouldBeEmpty();
        (await asB.Connections.GetAsync(tenantB, connection.Id, CancellationToken.None)).ShouldBeNull();
        (await asB.Connections.GetByExternalIdAsync(tenantB, ChannelKind.Zalo, oaId, CancellationToken.None)).ShouldBeNull();
        (await asB.Db.ChannelConnections.AnyAsync()).ShouldBeFalse();
        await Should.ThrowAsync<TenantIsolationException>(() => asB.Connections.ListAsync(tenantA, CancellationToken.None));
    }

    [Fact]
    public async Task Same_OA_cannot_be_connected_to_two_tenants()
    {
        var (tenantA, _) = await db.CreateTenantAsync("OA trùng A");
        var (tenantB, _) = await db.CreateTenantAsync("OA trùng B");
        var oaId = NewOaId();
        await ConnectAsync(tenantA, oaId);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => ConnectAsync(tenantB, oaId));
        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

        await using var system = db.CreateDbContext(tenantId: null);
        var lookup = new ChannelConnectionLookup(system);
        (await lookup.BelongsToOtherTenantAsync(ChannelKind.Zalo, oaId, tenantB, CancellationToken.None)).ShouldBeTrue();
        (await lookup.BelongsToOtherTenantAsync(ChannelKind.Zalo, oaId, tenantA, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Lookup_routes_OA_to_its_tenant_only()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Route A");
        var (tenantB, _) = await db.CreateTenantAsync("Route B");
        var oaA = NewOaId();
        var oaB = NewOaId();
        var connectionA = await ConnectAsync(tenantA, oaA);
        await ConnectAsync(tenantB, oaB);

        await using var system = db.CreateDbContext(tenantId: null);
        var lookup = new ChannelConnectionLookup(system);
        var route = (await lookup.FindAsync(ChannelKind.Zalo, oaA, CancellationToken.None)).ShouldNotBeNull();
        route.TenantId.ShouldBe(tenantA);
        route.ConnectionId.ShouldBe(connectionA.Id);
        route.TenantActive.ShouldBeTrue();
        (await lookup.FindAsync(ChannelKind.Zalo, "khong-ton-tai", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Disconnect_keeps_conversations_but_clears_link()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Ngắt kết nối");
        var connection = await ConnectAsync(tenantId, NewOaId());

        Guid conversationId;
        await using (var s = As(tenantId))
        {
            var repo = new ConversationRepository(s.Db, s.Context);
            var contact = await repo.GetOrAddContactAsync(tenantId, ChannelKind.Zalo, "user-1", null, CancellationToken.None);
            conversationId = repo.AddConversation(tenantId, new Conversation { ContactId = contact.Id, Channel = ChannelKind.Zalo, ConnectionId = connection.Id }).Id;
            await s.Db.SaveChangesAsync();
        }

        await using (var s = As(tenantId))
        {
            var tracked = (await s.Connections.GetAsync(tenantId, connection.Id, CancellationToken.None)).ShouldNotBeNull();
            s.Connections.Remove(tenantId, tracked);
            await s.Db.SaveChangesAsync();
        }

        await using var check = As(tenantId);
        var conversation = (await check.Db.Conversations.FirstAsync(c => c.Id == conversationId));
        conversation.ConnectionId.ShouldBeNull();
    }
}
