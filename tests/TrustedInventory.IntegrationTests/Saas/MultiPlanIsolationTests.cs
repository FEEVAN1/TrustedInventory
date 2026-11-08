using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// Worker half of AT-SAS-01 and the background-work table of docs/saas-state-matrix.md (DEC-043), through the real
/// job runner on real PostgreSQL. The HTTP half is SignupTests.Tenants_on_different_plans_sharing_one_pooled_connection_get_their_own_decisions.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MultiPlanIsolationTests(ApiFixture fx)
{
    private const string ProbeEvent = "WorkerIsolationProbe";

    [Fact]
    [Trait("Requirement", "AT-SAS-01")]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "TEN-003")]
    public async Task Worker_on_one_pooled_connection_keeps_each_tenants_events_in_that_tenant_whatever_the_plan()
    {
        var full = await TenantProvisioning.ProvisionAsync(fx, "wk-full");
        var narrow = await TenantProvisioning.ProvisionAsync(fx, "wk-narrow");
        await MoveToPlanWithoutInventoryAsync(narrow.TenantId);

        // Interleaved so a context leaking from one tenant's transaction into the next would lease the wrong rows.
        var expected = new Dictionary<Guid, Guid>();
        for (var i = 0; i < 3; i++)
            foreach (var tenant in new[] { full.TenantId, narrow.TenantId })
                expected[await InsertProbeAsync(tenant)] = tenant;

        // One physical connection per pool: every tenant transaction of the pass reuses the same session.
        await using var app = SingleConnection(fx.AppConnection);
        await using var platform = SingleConnection(fx.PlatformConnection);
        var sink = new RecordingSink();
        await DispatcherOn(app, platform, sink).RunOnceAsync(CancellationToken.None);

        // Each event once, labelled with the tenant that owns it. A narrow plan still gets delivery: background
        // work is never entitlement-gated (DEC-043).
        var delivered = sink.Delivered.Where(m => expected.ContainsKey(m.EventId)).ToList();
        Assert.Equal(expected.Count, delivered.Count);
        Assert.All(delivered, m => Assert.Equal(expected[m.EventId], m.TenantId));
        Assert.Equal(expected.Keys.Order(), delivered.Select(m => m.EventId).Order());

        // The tenant context was transaction-local (DEC-013): nothing is left on the shared session.
        await using var conn = await app.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT current_setting('app.tenant_id', true)", conn);
        Assert.True(string.IsNullOrEmpty(await cmd.ExecuteScalarAsync() as string));
    }

    [Fact]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "LFC-001")]
    public async Task Outbox_delivery_runs_in_every_mode_except_deleted()
    {
        var tenants = new Dictionary<string, Guid>();
        foreach (var mode in new[] { "Normal", "Provisioning", "BillingRestricted", "SecuritySuspended", "Closed", "Deleted" })
        {
            var tenant = await TenantProvisioning.ProvisionAsync(fx, "wk-" + mode.ToLowerInvariant());
            await ArrangeAsync(tenant.TenantId, mode);
            tenants[mode] = tenant.TenantId;
        }
        var probes = new Dictionary<string, Guid>();
        foreach (var (mode, tenantId) in tenants)
            probes[mode] = await InsertProbeAsync(tenantId);

        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var sink = new RecordingSink();
        await DispatcherOn(app, platform, sink).RunOnceAsync(CancellationToken.None);

        var delivered = sink.Delivered.Select(m => m.EventId).ToHashSet();
        foreach (var (mode, eventId) in probes)
            Assert.True(delivered.Contains(eventId) == (mode != "Deleted"), $"{mode}: delivered={delivered.Contains(eventId)}");
        // The deleted tenant's event is left untouched, not leased or failed.
        Assert.Equal(true, await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT sent_at IS NULL AND lease_owner IS NULL AND attempts = 0 FROM ti.outbox_event WHERE event_id = @e", ("e", probes["Deleted"])));
    }

    private static OutboxDispatcher DispatcherOn(NpgsqlDataSource app, NpgsqlDataSource platform, RecordingSink sink)
    {
        // Events are due from the database's now(); the job clock runs a minute ahead.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), clock);
        return new OutboxDispatcher(runner, sink, new WorkerOptions { WorkerId = "isolation-test" }, NullLogger<OutboxDispatcher>.Instance);
    }

    private static NpgsqlDataSource SingleConnection(string connectionString) =>
        DependencyInjection.BuildDataSource(new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 1 }.ConnectionString);

    private async Task<Guid> InsertProbeAsync(Guid tenantId)
    {
        var eventId = Guid.NewGuid();
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload, next_attempt_at)
            VALUES (@t, @e, @type, '{}', now())
            """, ("t", tenantId), ("e", eventId), ("type", ProbeEvent));
        return eventId;
    }

    private async Task MoveToPlanWithoutInventoryAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var plan = await SaasSeed.PlanAsync(conn, $"no-inv-{Guid.NewGuid():N}"[..20], ["service_api"], 10);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.subscription SET plan_version_id = @p WHERE tenant_id = @t",
            ("p", plan), ("t", tenantId));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE cp.entitlement_snapshot e SET plan_version_id = p.id, features = p.features, limits = p.quotas
            FROM cp.plan_version p WHERE p.id = @p AND e.tenant_id = @t AND e.is_current
            """, ("p", plan), ("t", tenantId));
    }

    private async Task ArrangeAsync(Guid tenantId, string mode)
    {
        var sql = mode switch
        {
            "Normal" => null,
            "Provisioning" => "UPDATE ti.tenant SET lifecycle = 'Provisioning' WHERE id = @t",
            "BillingRestricted" => """
                UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                       trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
                WHERE tenant_id = @t
                """,
            "SecuritySuspended" => """
                UPDATE ti.tenant SET security_suspended_at = now(), security_suspension_reason = 'worker test',
                       security_suspended_by = 'user-platform-admin' WHERE id = @t
                """,
            "Closed" => "UPDATE ti.tenant SET lifecycle = 'Archived', archived_at = now() WHERE id = @t",
            "Deleted" => "UPDATE ti.tenant SET lifecycle = 'Deleted', deletion_requested_at = now() - interval '1 day', deleted_at = now() WHERE id = @t",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (sql is not null)
            Assert.Equal(1, await TenantProvisioning.ExecAsSuperuserAsync(fx, sql, ("t", tenantId)));
    }

    private sealed class RecordingSink : IOutboxSink
    {
        public ConcurrentBag<OutboxMessage> Delivered { get; } = [];

        public Task DeliverAsync(OutboxMessage message, CancellationToken ct)
        {
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }
}
