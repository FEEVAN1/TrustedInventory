using System.Net;
using Microsoft.AspNetCore.Hosting;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>
/// Swagger UI over docs/openapi.yaml exists in Development only (DEC-103): every other environment keeps the health probes as the
/// only unauthenticated API routes (API-001). In Development it serves the contract as written, with the identity provider's
/// discovery address in place of the placeholder, and a page policy that still forbids framing.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ContractDocsTests(ApiFixture fx)
{
    [Theory]
    [Trait("Requirement", "API-001")]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi.yaml")]
    public async Task Outside_development_the_docs_are_not_served(string path)
    {
        var response = await fx.Anonymous().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-001")]
    public async Task In_development_swagger_ui_shows_the_contract_as_written()
    {
        using var factory = fx.Factory.WithWebHostBuilder(web => web.UseEnvironment("Development"));
        var client = factory.CreateClient();

        var page = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var policy = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);

        var contract = await client.GetStringAsync("/openapi.yaml");
        var written = await File.ReadAllTextAsync(ContractFile());
        Assert.DoesNotContain("identity.example.invalid", contract, StringComparison.Ordinal);
        Assert.Contains($"{TestTokens.Issuer.TrimEnd('/')}/.well-known/openid-configuration", contract, StringComparison.Ordinal);
        Assert.Equal(
            written.Replace("https://identity.example.invalid", TestTokens.Issuer.TrimEnd('/'), StringComparison.Ordinal),
            contract);

        // The UI is a page, but the API itself keeps its no-page policy.
        var health = await client.GetAsync("/health/live");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", string.Join(";", health.Headers.GetValues("Content-Security-Policy")));
    }

    private static string ContractFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "openapi.yaml")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "openapi.yaml");
    }
}
