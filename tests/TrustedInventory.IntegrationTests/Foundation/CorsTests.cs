using System.Net;
using Microsoft.AspNetCore.Hosting;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Browsers may call the API only from the configured web origins (SEC-INPUT, section 06).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CorsTests(ApiFixture fx)
{
    private const string Allowed = "https://app.synthetic.example";

    [Fact]
    [Trait("Requirement", "SEC-INPUT")]
    public async Task Only_configured_origins_get_cross_origin_access_with_the_contract_headers()
    {
        await using var factory = fx.Factory.WithWebHostBuilder(web => web.UseSetting("Cors:AllowedOrigins:0", Allowed));
        var client = factory.CreateClient();

        async Task<HttpResponseMessage> Preflight(string origin, string method = "POST")
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/warehouses");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key,if-match,x-tenant-id");
            return await client.SendAsync(request);
        }

        var allowed = await Preflight(Allowed);
        Assert.True(allowed.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"preflight answered {(int)allowed.StatusCode}");
        Assert.Equal(Allowed, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        var headers = string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        foreach (var h in new[] { "authorization", "content-type", "idempotency-key", "if-match", "x-tenant-id" })
            Assert.Contains(h, headers, StringComparison.Ordinal);

        // Another origin, or a method the API never accepts from browsers, gets no permission.
        Assert.False((await Preflight("https://evil.synthetic.example")).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await Preflight(Allowed, "DELETE")).Headers.Contains("Access-Control-Allow-Methods")
            && (await Preflight(Allowed, "DELETE")).Headers.GetValues("Access-Control-Allow-Methods").Any(m => m.Contains("DELETE", StringComparison.Ordinal)));

        // A simple request from the allowed origin exposes the headers the web app reads (ETag, Retry-After, Content-Disposition).
        var get = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        get.Headers.Add("Origin", Allowed);
        var answer = await client.SendAsync(get);
        Assert.Equal(Allowed, Assert.Single(answer.Headers.GetValues("Access-Control-Allow-Origin")));
        var exposed = string.Join(",", answer.Headers.GetValues("Access-Control-Expose-Headers"));
        foreach (var h in new[] { "ETag", "Location", "Retry-After", "Content-Disposition" })
            Assert.Contains(h, exposed, StringComparison.OrdinalIgnoreCase);
    }
}
