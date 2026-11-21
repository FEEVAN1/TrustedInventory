using Microsoft.AspNetCore.Hosting;
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

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            yield return x;
        if (e is AggregateException a)
            foreach (var inner in a.InnerExceptions.SelectMany(Chain))
                yield return inner;
    }
}
