using System.Net;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>File xác thực tên miền Zalo (/zalo_verifier{mã}.html) — không cần database.</summary>
public sealed class ZaloSiteVerificationTests
{
    private const string Token = "EkQzTixOQouhn_5rr_WgOGlBdpRdlKHfE3Cu";

    private sealed class FactoryWithToken(string? token) : ApiFactory
    {
        protected override Dictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                settings["Zalo:SiteVerificationToken"] = token;
                return settings;
            }
        }
    }

    [Fact]
    public async Task Serves_verification_file_with_meta_tag_when_token_is_configured()
    {
        await using var api = new FactoryWithToken(Token);
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri($"/zalo_verifier{Token}.html", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain($"<meta property=\"zalo-platform-site-verification\" content=\"{Token}\" />");
    }

    [Fact]
    public async Task Other_verifier_names_are_not_found()
    {
        await using var api = new FactoryWithToken(Token);
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/zalo_verifierSomeoneElse123.html", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_file_is_served_when_token_is_not_configured()
    {
        await using var api = new FactoryWithToken(null);
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri($"/zalo_verifier{Token}.html", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Api_refuses_to_start_when_token_has_unsafe_characters()
    {
        using var api = new FactoryWithToken("../../etc/passwd");

        var ex = Should.Throw<OptionsValidationException>(() => api.CreateClient());

        ex.Message.ShouldContain("SiteVerificationToken");
    }
}
