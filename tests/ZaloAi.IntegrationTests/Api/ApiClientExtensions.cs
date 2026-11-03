using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

internal static class ApiClientExtensions
{
    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password = PostgresFixture.TestPassword) =>
        client.PostAsJsonAsync(new Uri("/auth/login", UriKind.Relative), new { email, password });

    /// <summary>Client mới (cookie riêng) đã đăng nhập.</summary>
    public static async Task<HttpClient> CreateLoggedInClientAsync(this ApiFactory api, string email)
    {
        var client = api.CreateClient();
        using var response = await client.LoginAsync(email);
        response.EnsureSuccessStatusCode();
        return client;
    }

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).ShouldBe(status);
        (await response.ProblemCodeAsync()).ShouldBe(code);
    }
}
