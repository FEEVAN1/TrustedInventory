using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>The API fails closed on missing security settings instead of starting with defaults (NFR-CONFIG, SEC-TRANSPORT).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ConfigurationTests(ApiFixture fx)
{
    [Theory]
    [Trait("Requirement", "NFR-CONFIG")]
    [InlineData("ConnectionStrings:App")]
    [InlineData("ConnectionStrings:Platform")]
    [InlineData("ConnectionStrings:Control")]
    [InlineData("Paging:CursorKey")]
    [InlineData("Auth:Authority")]
    [InlineData("Auth:Audience")]
    public void Startup_refuses_to_run_without_a_required_setting(string key)
    {
        using var factory = fx.Factory.WithWebHostBuilder(web => web.UseSetting(key, ""));
        var refused = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var messages = string.Join(" | ", Chain(refused).Select(e => e.Message));
        Assert.Contains($"Configuration value '{key}' is required.", messages, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEC-105: the API may read discovery and signing keys from an in-network address, but the issuer it accepts stays exactly
    /// Auth:Authority; a token naming the in-network address as issuer is refused.
    /// </summary>
    [Fact]
    [Trait("Requirement", "NFR-CONFIG")]
    [Trait("Requirement", "IAM-002")]
    public async Task A_separate_metadata_address_changes_where_keys_are_read_not_which_issuer_is_accepted()
    {
        const string inNetwork = "https://idp-internal.test.trustedinventory.invalid";
        using var factory = fx.Factory.WithWebHostBuilder(web => web.UseSetting("Auth:MetadataAddress", $"{inNetwork}/.well-known/openid-configuration"));
        var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal($"{inNetwork}/.well-known/openid-configuration", options.MetadataAddress);
        Assert.Equal(TestTokens.Issuer, options.TokenValidationParameters.ValidIssuer);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(SeedData.PlatformAdmin, issuer: inNetwork));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(SeedData.PlatformAdmin));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            yield return x;
        if (e is AggregateException a)
            foreach (var inner in a.InnerExceptions.SelectMany(Chain))
                yield return inner;
    }
}
