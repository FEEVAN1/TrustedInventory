using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Operations;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Tenant-aware rate limits, export concurrency and fair worker scheduling (SOP-001, SOP-002, AT-SOP-01, DEC-073).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class FairnessTests(ApiFixture fx)
{
    private static readonly byte[] CheckpointKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 5)).ToArray();

    [Fact]
    [Trait("Requirement", "SOP-001")]
    [Trait("Requirement", "SOP-002")]
    [Trait("Requirement", "AT-SOP-01")]
    public async Task A_noisy_tenant_spends_only_its_own_budget_while_nine_other_tenants_are_served()
    {
        // A host with the proposed per-person budget lowered so the test is quick; every other setting is the fixture's.
        await using var host = fx.Factory.WithWebHostBuilder(b => b.UseSetting("RateLimit:HumanPerMinute", "30"));
        var tenants = new List<ProvisionedTenant>();
        for (var i = 0; i < 10; i++)
            tenants.Add(await TenantProvisioning.ProvisionAsync(fx, $"fair-{i}"));
        HttpClient Client(ProvisionedTenant t, Guid? header = null)
        {
            var c = host.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(t.Admin));
            c.DefaultRequestHeaders.Add("X-Tenant-Id", (header ?? t.TenantId).ToString());
            return c;
        }

        // The noisy tenant fires 150 requests at once; each quiet tenant sends ten in a row at the same time. Concurrency stays
        // under the test database's connection limit (the noisy tenant's rejected requests open no connection).
        var noisy = Client(tenants[0]);
        var noise = Enumerable.Range(0, 150).Select(_ => noisy.GetAsync("/api/v1/warehouses")).ToList();
        var quiet = tenants.Skip(1).Select(async t =>
        {
            var client = Client(t);
            var results = new List<HttpResponseMessage>();
            for (var i = 0; i < 10; i++)
                results.Add(await client.GetAsync("/api/v1/warehouses"));
            return results;
        }).ToList();
        var noisyResults = await Task.WhenAll(noise);
        var quietResults = (await Task.WhenAll(quiet)).SelectMany(r => r).ToList();
        Assert.Equal(90, quietResults.Count);

        // The other nine tenants are all served; the noisy one gets exactly its budget and then 429 with Retry-After.
        Assert.All(quietResults, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(30, noisyResults.Count(r => r.StatusCode == HttpStatusCode.OK));
        var rejected = noisyResults.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.Equal(120, rejected.Count);
        Assert.All(rejected, r => Assert.NotNull(r.Headers.RetryAfter));
        await rejected[0].ExpectProblemAsync(HttpStatusCode.TooManyRequests, "RATE_LIMITED");

        // Naming another tenant in the header does not reach that tenant's budget: the noisy user is still limited, the
        // victim is still served.
        var forged = await Client(tenants[0], header: tenants[1].TenantId).GetAsync("/api/v1/warehouses");
        Assert.NotEqual(HttpStatusCode.OK, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(tenants[1]).GetAsync("/api/v1/warehouses")).StatusCode);
        Npgsql.NpgsqlConnection.ClearAllPools(); // release this host's connections before the next test
    }

    [Fact]
    [Trait("Requirement", "SOP-001")]
    [Trait("Requirement", "AT-SOP-01")]
    public async Task Each_tenant_has_two_concurrent_exports_and_the_worker_serves_tenants_in_turn()
    {
        await GenerateAsync(int.MaxValue); // drain anything other tests left queued
        var a = await TenantProvisioning.SignUpAsync(fx, "fair-exp-a");
        var b = await TenantProvisioning.SignUpAsync(fx, "fair-exp-b");
        var ca = fx.Client(a.Admin, a.TenantId);
        var cb = fx.Client(b.Admin, b.TenantId);
        static object Audit() => new { type = "Audit", filters = new { } };

        var a1 = (string)(await (await ca.PostJsonAsync("/api/v1/exports", Audit())).ExpectAsync(HttpStatusCode.Accepted))["id"]!;
        var a2 = (string)(await (await ca.PostJsonAsync("/api/v1/exports", Audit())).ExpectAsync(HttpStatusCode.Accepted))["id"]!;
        var third = await ca.PostJsonAsync("/api/v1/exports", Audit());
        await third.ExpectProblemAsync(HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        var b1 = (string)(await (await cb.PostJsonAsync("/api/v1/exports", Audit())).ExpectAsync(HttpStatusCode.Accepted))["id"]!;

        // Budget for two exports: tenant A was queued first and has two, yet B's single export is done in the first round.
        await GenerateAsync(2);
        Assert.Equal("Completed", await StatusAsync(cb, b1));
        Assert.Equal(1, new[] { await StatusAsync(ca, a1), await StatusAsync(ca, a2) }.Count(s => s == "Completed"));

        // A finished export frees a slot.
        await (await ca.PostJsonAsync("/api/v1/exports", Audit())).ExpectAsync(HttpStatusCode.Accepted);
    }

    private static async Task<string?> StatusAsync(HttpClient client, string id) =>
        (string?)(await (await client.GetAsync($"/api/v1/exports/{id}")).ExpectAsync(HttpStatusCode.OK))["status"];

    private async Task GenerateAsync(int maxPerRun)
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new ExportGenerationJob(runner, fx.Factory.Services.GetRequiredService<IObjectStore>(), new CheckpointSigningOptions(CheckpointKey, "test-key"),
            NullLogger<ExportGenerationJob>.Instance, maxPerRun).RunOnceAsync(CancellationToken.None);
    }
}
