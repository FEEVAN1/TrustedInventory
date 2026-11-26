using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Organization;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Organization;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Platform signals and alerts, notice recipients and the public service status (SOP-003, SOP-006, SUI-002, DEC-074).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class MonitoringTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "SOP-003")]
    [Trait("Requirement", "SOP-006")]
    public async Task An_old_webhook_raises_a_critical_alert_that_operators_acknowledge_and_the_monitor_closes()
    {
        var platform = fx.Client(SeedData.PlatformAdmin, tenantId: null);
        var stranger = fx.Client($"user-{Guid.NewGuid():N}", tenantId: null);
        await (await stranger.GetAsync("/platform/v1/health")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        // platform.health.read: platform billing reads signals too, but only operations acknowledge alerts.
        var billing = fx.Client(SeedData.PlatformBilling, tenantId: null);
        await (await billing.GetAsync("/platform/v1/health")).ExpectAsync(HttpStatusCode.OK);
        await (await billing.PostJsonAsync($"/platform/v1/alerts/{Guid.NewGuid()}/acknowledge", new { note = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var eventId = $"evt_mon_{Guid.NewGuid():N}";
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO cp.webhook_inbox (provider, event_id, event_type, received_at, raw_body_digest, payload_encrypted, processing_state)
            VALUES ('simulator', @e, 'invoice.paid', @at, decode(repeat('ab', 32), 'hex'), '\x00'::bytea, 'Received')
            """, ("e", eventId), ("at", receivedAt));
        try
        {
            await RunMonitorAsync(DateTimeOffset.UtcNow);
            var health = await (await platform.GetAsync("/platform/v1/health")).ExpectAsync(HttpStatusCode.OK);
            Assert.True((long)health["webhookBacklog"]! >= 1);
            Assert.True((double)health["oldestWebhookAgeSeconds"]! >= 19 * 60);
            // Aggregates only: no tenant id, name or slug appears in the signals (SOP-006).
            Assert.DoesNotContain(fx.Seed.A.TenantId.ToString(), health.ToJsonString(), StringComparison.Ordinal);

            var alerts = await (await platform.GetAsync("/platform/v1/alerts?status=Open")).ExpectAsync(HttpStatusCode.OK);
            var backlog = alerts["items"]!.AsArray().Single(a => (string?)a!["kind"] == "WebhookBacklog")!;
            Assert.Equal("Critical", (string?)backlog["severity"]);
            Assert.True((long)backlog["detail"]!["oldestAgeSeconds"]! >= 19 * 60);

            // A second run refreshes the same alert instead of opening another.
            await RunMonitorAsync(DateTimeOffset.UtcNow.AddMinutes(1));
            var again = await (await platform.GetAsync("/platform/v1/alerts")).ExpectAsync(HttpStatusCode.OK);
            Assert.Single(again["items"]!.AsArray(), a => (string?)a!["kind"] == "WebhookBacklog" && (string?)a["status"] != "Closed");

            var id = (string)backlog["id"]!;
            var acknowledged = await (await platform.PostJsonAsync($"/platform/v1/alerts/{id}/acknowledge",
                new { note = "Provider status page shows an incident; watching" }, $"\"{(int)backlog["version"]!}\"")).ExpectAsync(HttpStatusCode.OK);
            Assert.Equal("Acknowledged", (string?)acknowledged["status"]);
            Assert.Equal(SeedData.PlatformAdmin, (string?)acknowledged["acknowledgedBy"]);
            await (await platform.PostJsonAsync($"/platform/v1/alerts/{id}/acknowledge", new { note = "again" }, $"\"{(int)backlog["version"]!}\""))
                .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        }
        finally
        {
            await TenantProvisioning.ExecAsSuperuserAsync(fx, "UPDATE cp.webhook_inbox SET processing_state = 'Ignored', processed_at = now() WHERE event_id = @e", ("e", eventId));
        }

        // When the condition clears, the monitor closes the alert itself.
        var store = fx.Factory.Services.GetRequiredService<IPlatformMonitorStore>();
        await store.ApplySignalsAsync([new AlertSignal(PlatformAlertKind.WebhookBacklog, "Critical", false, new Dictionary<string, long>())],
            DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None);
        var closed = await (await platform.GetAsync("/platform/v1/alerts?status=Closed")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(closed["items"]!.AsArray(), a => (string?)a!["kind"] == "WebhookBacklog" && a["closedAt"] is not null);
    }

    [Fact]
    [Trait("Requirement", "SUI-002")]
    public async Task A_billing_notice_reaches_the_owner_and_the_billing_contact_once_each()
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, "notice-recipients");
        var noticeId = Guid.NewGuid();
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO cp.billing_notice (id, tenant_id, kind, dedupe_key, detail, created_at)
            VALUES (@id, @t, 'PaymentFailed', @key, '{"invoice":"synthetic"}', now())
            """, ("id", noticeId), ("t", tenant.TenantId), ("key", $"test:{noticeId}"));

        await RelayAsync();
        // Owner (by subject) and the billing contact address: one message each.
        var rows = await CountAsync("SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND payload->>'noticeId' = @n",
            ("t", tenant.TenantId), ("n", noticeId.ToString()));
        Assert.Equal(2L, rows);
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND payload->>'noticeId' = @n
              AND payload->'recipient'->>'type' = 'member' AND payload->'recipient'->>'subject' = @s
            """, ("t", tenant.TenantId), ("n", noticeId.ToString()), ("s", tenant.Admin)));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND payload->>'noticeId' = @n AND payload->'recipient'->>'type' = 'billingContact'
            """, ("t", tenant.TenantId), ("n", noticeId.ToString())));

        // The event id is derived from the notice and the recipient, so a repeated relay (for example after a crash before the
        // notice was marked) collides on the outbox's unique event id instead of sending twice.
        var ownerEvent = TrustedInventory.Infrastructure.Jobs.BillingNoticeRelayJob.EventId(noticeId, $"subject:{tenant.Admin}");
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.outbox_event WHERE event_id = @e", ("e", ownerEvent)));
        var repeat = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => TenantProvisioning.ExecAsSuperuserAsync(fx,
            "INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload) VALUES (@t, @e, 'BillingPaymentFailed', '{}')",
            ("t", tenant.TenantId), ("e", ownerEvent)));
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, repeat.SqlState);
        await RelayAsync();
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND payload->>'noticeId' = @n",
            ("t", tenant.TenantId), ("n", noticeId.ToString())));
    }

    [Fact]
    [Trait("Requirement", "SOP-006")]
    public async Task The_service_status_is_public_and_names_where_to_get_support()
    {
        var status = await (await fx.Anonymous().GetAsync("/api/v1/service-status")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Operational", (string?)status["status"]);
        Assert.Contains(status["components"]!.AsArray(), c => (string?)c!["name"] == "database" && (string?)c["status"] == "Operational");
        Assert.NotNull(status["support"]!["contact"]);
    }

    private async Task RunMonitorAsync(DateTimeOffset at) =>
        await new PlatformMonitorJob(fx.Factory.Services.GetRequiredService<IPlatformMonitorStore>(), new FakeTimeProvider(at),
            NullLogger<PlatformMonitorJob>.Instance).RunOnceAsync(CancellationToken.None);

    private async Task RelayAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new BillingNoticeRelayJob(runner, NullLogger<BillingNoticeRelayJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] args) =>
        Convert.ToInt64(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args), System.Globalization.CultureInfo.InvariantCulture);
}
