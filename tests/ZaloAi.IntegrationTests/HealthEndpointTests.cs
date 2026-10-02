using System.Net;
using Shouldly;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests;

[Collection(PostgresGroup.Name)]
public sealed class HealthEndpointTests(PostgresFixture db)
{
    [Fact]
    public async Task Health_returns_200_when_database_is_reachable()
    {
        using var client = db.Api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_returns_503_when_database_is_down()
    {
        await using var api = new ApiFactory("Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task OpenApi_document_lists_endpoints()
    {
        using var client = db.Api.CreateClient();

        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        json.ShouldContain("/auth/login");
        json.ShouldContain("/tenant/settings");
    }
}
