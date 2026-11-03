using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using ZaloAi.Api.Connections;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Kết nối Zalo OA: OAuth v4 + PKCE + state, với Zalo giả.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ZaloConnectTests(PostgresFixture db) : IAsyncLifetime, IDisposable
{
    private readonly FakeZaloServer _zalo = new();
    private ZaloApiFactory _api = null!;

    public Task InitializeAsync()
    {
        _api = new ZaloApiFactory(db.ConnectionString, db.RedisConnectionString, _zalo);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    public void Dispose() => _zalo.Dispose();

    private static string NewOaId() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static async Task<string> StartAsync(HttpClient owner)
    {
        using var response = await owner.PostAsync(new Uri("/channels/zalo/connect", UriKind.Relative), null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConnectUrlResponse>())!.Url;
    }

    private async Task<HttpResponseMessage> CallbackAsync(string state, string oaId, string code = "auth-code")
    {
        var anonymous = _api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await anonymous.GetAsync(new Uri($"/connect/zalo/callback?code={code}&oa_id={oaId}&state={state}", UriKind.Relative));
    }

    [Fact]
    public async Task Owner_connects_OA_with_pkce_and_tokens_stay_secret()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Kết nối Zalo");
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var oaId = NewOaId();

        var url = await StartAsync(owner);
        url.ShouldStartWith("https://oauth.zaloapp.com/v4/oa/permission?");
        var query = Query(url);
        query["app_id"].ShouldBe(ZaloApiFactory.AppId);
        query["redirect_uri"].ShouldBe("https://dev-api.test.vn/connect/zalo/callback");

        using var callback = await CallbackAsync(query["state"], oaId);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().ShouldBe("http://localhost:3000/channels?connected=zalo");

        // Verifier gửi cho Zalo khớp challenge đã đặt trên link; secret ở header.
        var exchange = _zalo.Calls.Last(c => c.Body.Contains("grant_type=authorization_code", StringComparison.Ordinal));
        exchange.Headers["secret_key"].ShouldBe("test-app-secret");
        var verifier = Uri.UnescapeDataString(exchange.Body.Split('&').Single(p => p.StartsWith("code_verifier=", StringComparison.Ordinal))[14..]);
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').ShouldBe(query["code_challenge"]);

        var list = (await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!;
        list.Configured.ShouldBeTrue();
        var connection = list.Connections.ShouldHaveSingleItem();
        connection.ExternalId.ShouldBe(oaId);
        connection.Status.ShouldBe("active");
        var raw = await owner.GetStringAsync(new Uri("/channels", UriKind.Relative));
        raw.ShouldNotContain("AT-");
        raw.ShouldNotContain("RT-");
        raw.ShouldNotContain("Token\":\"", Case.Insensitive);
    }

    [Fact]
    public async Task State_is_single_use()
    {
        var (tenantId, _) = await db.CreateTenantAsync("State một lần");
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var state = Query(await StartAsync(owner))["state"];
        var oaId = NewOaId();

        using (var first = await CallbackAsync(state, oaId))
        {
            first.Headers.Location!.Query.ShouldContain("connected=zalo");
        }

        using var replay = await CallbackAsync(state, oaId);
        replay.Headers.Location!.Query.ShouldContain("error=expired");
        using var forged = await CallbackAsync("khong-phai-state-that", oaId);
        forged.Headers.Location!.Query.ShouldContain("error=expired");
    }

    [Fact]
    public async Task OA_of_tenant_A_cannot_be_taken_by_tenant_B_and_B_cannot_touch_A()
    {
        var (tenantA, _) = await db.CreateTenantAsync("OA của A");
        var (tenantB, _) = await db.CreateTenantAsync("Muốn lấy OA");
        var ownerA = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        var ownerB = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        var oaId = NewOaId();

        using (await CallbackAsync(Query(await StartAsync(ownerA))["state"], oaId))
        {
        }

        using var stolen = await CallbackAsync(Query(await StartAsync(ownerB))["state"], oaId);
        stolen.Headers.Location!.Query.ShouldContain("error=oa_in_use");

        (await ownerB.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.ShouldBeEmpty();
        var idA = (await ownerA.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.Single().Id;
        using var delete = await ownerB.DeleteAsync(new Uri($"/channels/{idA}", UriKind.Relative));
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerA.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.ShouldHaveSingleItem();

        using var disconnect = await ownerA.DeleteAsync(new Uri($"/channels/{idA}", UriKind.Relative));
        disconnect.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ownerA.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task Staff_cannot_connect()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Nhân viên kết nối");
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staff = await _api.CreateLoggedInClientAsync(staffEmail);

        using var response = await staff.PostAsync(new Uri("/channels/zalo/connect", UriKind.Relative), null);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
