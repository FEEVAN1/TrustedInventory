using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Structured logs with trace and tenant scopes, no credentials in logs, and operations metrics (NFR-MONITOR, DEC-079).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ObservabilityTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "NFR-MONITOR")]
    [Trait("Requirement", "SEC-INPUT")]
    public async Task Request_logs_carry_trace_and_tenant_ids_and_never_a_token_or_link_secret()
    {
        var capture = new CapturingLoggerProvider();
        await using var host = fx.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(capture)));
        var tenant = await TenantProvisioning.SignUpAsync(fx, "obs-logs");
        var token = TestTokens.Issue(tenant.Admin);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.TenantId.ToString());

        var warehouses = await client.GetAsync("/api/v1/warehouses");
        Assert.Equal(HttpStatusCode.OK, warehouses.StatusCode);
        // A download token in a query string, and a reauth token in a body, must not reach the logs.
        const string linkSecret = "SECRET-LINK-TOKEN-0123456789";
        await client.GetAsync($"/api/v1/exports/{Guid.NewGuid()}/download?token={linkSecret}");
        var reauth = TestTokens.Issue(tenant.Admin);
        await client.PostAsync("/api/v1/saas/closure", new StringContent(
            $$"""{"reason":"x","requestedDeletion":false,"confirmation":"no","reauthToken":"{{reauth}}"}""", Encoding.UTF8, "application/json"));

        var lines = capture.Lines.ToList();
        Assert.NotEmpty(lines);
        var completed = lines.Where(l => l.Message.Contains("listWarehouses answered 200", StringComparison.Ordinal)).ToList();
        var entry = Assert.Single(completed);
        Assert.Contains("traceId=", entry.Scopes, StringComparison.Ordinal);
        Assert.Contains($"for tenant {tenant.TenantId}", entry.Message, StringComparison.Ordinal);
        var all = string.Join('\n', lines.Select(l => l.Message + " " + l.Scopes));
        Assert.DoesNotContain(token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(reauth, all, StringComparison.Ordinal);
        Assert.DoesNotContain(linkSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", all, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "NFR-MONITOR")]
    [Trait("Requirement", "SOP-006")]
    public async Task Operations_metrics_are_prometheus_text_for_platform_operators_with_no_tenant_ids()
    {
        await (await fx.Client($"user-{Guid.NewGuid():N}", tenantId: null).GetAsync("/platform/v1/metrics"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var response = await fx.Client(SeedData.PlatformAdmin, tenantId: null).GetAsync("/platform/v1/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var metric in new[] { "ti_allocation_failures_total", "ti_transaction_retries_total", "ti_rate_limited_total", "ti_ledger_mismatch_alerts",
                     "ti_outbox_max_age_seconds", "ti_outbox_dead_lettered", "ti_stale_devices", "ti_hold_buckets", "ti_hold_quantity", "ti_open_excursions" })
            Assert.Contains($"\n{metric} ", "\n" + text, StringComparison.Ordinal);
        Assert.DoesNotContain(fx.Seed.A.TenantId.ToString(), text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^ti_hold_quantity \d+(\.\d+)?$", text);
    }

    private sealed record Captured(string Category, string Message, string Scopes);

    /// <summary>Collects formatted messages with their scopes, the way the JSON console renders them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<Captured> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new StringBuilder();
                owner._scopes.ForEachScope((scope, sb) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (k, v) in pairs)
                            sb.Append(k).Append('=').Append(v).Append(';');
                    else
                        sb.Append(scope).Append(';');
                }, scopes);
                owner.Lines.Enqueue(new Captured(category, formatter(state, exception) + (exception is null ? "" : " " + exception), scopes.ToString()));
            }
        }
    }
}
