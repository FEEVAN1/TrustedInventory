using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Jobs;

/// <summary>Background jobs against real PostgreSQL with a controllable clock (section 19).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WorkerJobTests : IDisposable
{
    private static readonly byte[] CheckpointKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    private readonly ApiFixture fx;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow.AddSeconds(1));
    private readonly string _retentionDir = Path.Combine(Path.GetTempPath(), "ti-retention-" + Guid.NewGuid().ToString("N"));
    private readonly AppDataSource _app;
    private readonly PlatformDataSource _platform;
    private readonly TenantJobRunner _runner;
    private readonly WorkerOptions _options = new() { WorkerId = "test-worker" };

    public WorkerJobTests(ApiFixture fixture)
    {
        fx = fixture;
        _app = new AppDataSource(DependencyInjection.BuildDataSource(fx.AppConnection));
        _platform = new PlatformDataSource(DependencyInjection.BuildDataSource(fx.PlatformConnection));
        _runner = new TenantJobRunner(_platform, _app, _clock);
    }

    public void Dispose()
    {
        _app.Source.Dispose();
        _platform.Source.Dispose();
        if (Directory.Exists(_retentionDir))
            Directory.Delete(_retentionDir, recursive: true);
    }

    [Fact]
    [Trait("Requirement", "NFR-JOBS")]
    [Trait("Requirement", "ARC-003")]
    public async Task Outbox_event_from_a_command_is_delivered_once_and_never_resent()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var created = await (await admin.PostJsonAsync("/api/v1/warehouses",
            new { code = "OB-" + Guid.NewGuid().ToString("N")[..8], name = "Outbox", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);
        var warehouseId = (string)created["id"]!;
        var eventId = await ScalarAsync<Guid>(
            "SELECT event_id FROM ti.outbox_event WHERE payload->>'warehouseId' = @w", ("w", warehouseId));
        // The row is due from the database clock's now(); move the job clock just past it.
        _clock.SetUtcNow(await ScalarAsync<DateTime>(
            "SELECT next_attempt_at FROM ti.outbox_event WHERE event_id = @e", ("e", eventId)) + TimeSpan.FromSeconds(1));

        var sink = new RecordingSink();
        var dispatcher = new OutboxDispatcher(_runner, sink, _options, NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.RunOnceAsync(default);
        await dispatcher.RunOnceAsync(default);

        Assert.Equal(1, sink.Delivered.Count(m => m.EventId == eventId));
        var delivered = sink.Delivered.Single(m => m.EventId == eventId);
        Assert.Equal(fx.Seed.A.TenantId, delivered.TenantId);
        Assert.Equal("WarehouseCreated", delivered.EventType);
        Assert.True(await ScalarAsync<bool>("SELECT sent_at IS NOT NULL AND lease_owner IS NULL FROM ti.outbox_event WHERE event_id = @e", ("e", eventId)));
    }

    [Fact]
    [Trait("Requirement", "NFR-JOBS")]
    public async Task Failing_delivery_backs_off_then_dead_letters_after_ten_attempts_with_one_alert()
    {
        var eventId = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload, next_attempt_at)
            VALUES (@t, @e, 'TestAlwaysFails', '{}', @now)
            """, ("t", fx.Seed.B.TenantId), ("e", eventId), ("now", _clock.GetUtcNow()));

        var sink = new RecordingSink(failFor: eventId);
        var dispatcher = new OutboxDispatcher(_runner, sink, _options, NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.RunOnceAsync(default);
        await dispatcher.RunOnceAsync(default); // backoff: not due again yet
        Assert.Equal(1, sink.Attempts(eventId));

        for (var i = 0; i < 12; i++)
        {
            _clock.Advance(TimeSpan.FromHours(2)); // beyond the 1 h maximum backoff
            await dispatcher.RunOnceAsync(default);
        }

        Assert.Equal(10, sink.Attempts(eventId));
        Assert.True(await ScalarAsync<bool>(
            "SELECT attempts = 10 AND dead_lettered_at IS NOT NULL AND sent_at IS NULL FROM ti.outbox_event WHERE event_id = @e", ("e", eventId)));
        Assert.Equal(1L, await ScalarAsync<long>("""
            SELECT count(*) FROM ti.alert
            WHERE tenant_id = @t AND type = 'OutboxDeadLetter' AND severity = 'High' AND dedupe_key = @k
            """, ("t", fx.Seed.B.TenantId), ("k", $"outbox-dead-letter:{eventId}")));
    }

    [Fact]
    [Trait("Requirement", "TEN-004")]
    public async Task A_job_in_one_tenant_context_cannot_see_or_change_another_tenants_rows()
    {
        var foreignEvent = Guid.NewGuid();
        await ExecAsync("INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload, next_attempt_at) VALUES (@t, @e, 'TestForeign', '{}', @n)",
            ("t", fx.Seed.B.TenantId), ("e", foreignEvent), ("n", DateTimeOffset.UtcNow.AddDays(30)));

        var uow = _runner.UnitOfWorkFor(fx.Seed.A.TenantId, TenantStatus.Active);
        var (visibleTenants, updated) = await uow.WriteAsync(async (s, ct) =>
        {
            var (conn, tx) = RawOf(s);
            await using var tenants = new NpgsqlCommand("SELECT count(DISTINCT tenant_id) FROM ti.outbox_event WHERE tenant_id <> @a", conn, tx);
            tenants.Parameters.AddWithValue("a", fx.Seed.A.TenantId);
            var foreign = (long)(await tenants.ExecuteScalarAsync(ct))!;
            await using var update = new NpgsqlCommand("UPDATE ti.outbox_event SET sent_at = now() WHERE event_id = @e", conn, tx);
            update.Parameters.AddWithValue("e", foreignEvent);
            return (foreign, await update.ExecuteNonQueryAsync(ct));
        });

        Assert.Equal(0L, visibleTenants);
        Assert.Equal(0, updated);
        Assert.True(await ScalarAsync<bool>("SELECT sent_at IS NULL FROM ti.outbox_event WHERE event_id = @e", ("e", foreignEvent)));

        // The platform pool used to enumerate tenants has no access to tenant data at all.
        await using var platform = _platform.Source.CreateCommand("SELECT count(*) FROM ti.outbox_event");
        var denied = await Assert.ThrowsAsync<PostgresException>(() => platform.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    public async Task Idempotency_cleanup_removes_only_expired_records()
    {
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        foreach (var (key, created) in new[] { (expired, now.AddDays(-8)), (live, now.AddDays(-1)) })
            await ExecAsync("""
                INSERT INTO ti.idempotency_record (tenant_id, principal, route, key, payload_digest, response_status, response_body, created_at, expires_at)
                VALUES (@t, 'user-test', 'POST /test', @k, decode(repeat('00', 32), 'hex'), 200, '{}', @c, @c + interval '7 days')
                """, ("t", fx.Seed.A.TenantId), ("k", key), ("c", created));

        await new IdempotencyCleanupJob(_runner, NullLogger<IdempotencyCleanupJob>.Instance).RunOnceAsync(default);

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM ti.idempotency_record WHERE key = @k", ("k", expired)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.idempotency_record WHERE key = @k", ("k", live)));
    }

    [Fact]
    [Trait("Requirement", "AUD-003")]
    public async Task Daily_checkpoint_is_signed_stored_externally_and_created_once_per_day()
    {
        var store = new SimulatedFileRetentionStore(_retentionDir);
        var job = new AuditCheckpointJob(_runner, store, new CheckpointSigningOptions(CheckpointKey, "test-key"),
            NullLogger<AuditCheckpointJob>.Instance);
        // Far-future day so this test owns the checkpoint rows it inspects.
        _clock.SetUtcNow(new DateTimeOffset(2099, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var day = new DateOnly(2099, 1, 1);

        await job.RunOnceAsync(default);
        await job.RunOnceAsync(default);

        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT c.sequence, c.event_hash, c.signature, c.signing_key_id, c.external_reference, s.last_sequence, s.last_hash
            FROM ti.audit_checkpoint c JOIN ti.audit_stream s ON s.tenant_id = c.tenant_id
            WHERE c.tenant_id = @t AND c.checkpoint_date = @d
            """, conn);
        cmd.Parameters.AddWithValue("t", fx.Seed.A.TenantId);
        cmd.Parameters.AddWithValue("d", day);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            var sequence = reader.GetInt64(0);
            var hash = (byte[])reader[1];
            var signature = (byte[])reader[2];
            Assert.Equal("test-key", reader.GetString(3));
            Assert.StartsWith("simulated-file://", reader.GetString(4));
            Assert.Equal(reader.GetInt64(5), sequence);
            Assert.Equal((byte[])reader[6], hash);
            Assert.True(AuditCheckpointSigner.Verify(CheckpointKey, fx.Seed.A.TenantId, day, sequence, hash, signature));
            Assert.False(AuditCheckpointSigner.Verify(CheckpointKey, fx.Seed.A.TenantId, day, sequence + 1, hash, signature));
            Assert.False(await reader.ReadAsync()); // one checkpoint per tenant per day
        }

        Assert.True(File.Exists(Path.Combine(_retentionDir, fx.Seed.A.TenantId.ToString(), "2099-01-01.json")));

        _clock.Advance(TimeSpan.FromDays(1));
        await job.RunOnceAsync(default);
        Assert.Equal(2L, await ScalarAsync<long>(
            "SELECT count(*) FROM ti.audit_checkpoint WHERE tenant_id = @t AND checkpoint_date >= @d", ("t", fx.Seed.A.TenantId), ("d", day)));
    }

    [Fact]
    [Trait("Requirement", "AT-AUD-01")]
    [Trait("Requirement", "AUD-003")]
    public async Task External_checkpoints_catch_a_rewritten_chain_with_a_matching_database_checkpoint_and_a_forged_signature()
    {
        // DEC-093: an attacker with database access rewrites the event at the checkpoint and the database's own checkpoint row to agree;
        // only the signed copy in the separate retention store shows it. A forged external file is caught by its signature.
        var tenant = await Organization.TenantProvisioning.SignUpAsync(fx, "ckpt-verify");
        var store = new SimulatedFileRetentionStore(_retentionDir);
        var signing = new CheckpointSigningOptions(CheckpointKey, "test-key");
        var support = fx.Factory.Services.GetRequiredService<Application.Audit.IAuditSupport>();
        _clock.SetUtcNow(new DateTimeOffset(2098, 3, 1, 12, 0, 0, TimeSpan.Zero));
        await new AuditCheckpointJob(_runner, store, signing, NullLogger<AuditCheckpointJob>.Instance).RunOnceAsync(default);
        var verifier = new AuditCheckpointVerificationJob(_runner, store, signing, support, NullLogger<AuditCheckpointVerificationJob>.Instance);
        async Task<long> OpenAlerts() => await ScalarAsync<long>(
            "SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'AuditChainFailure' AND severity = 'High' AND status = 'Open'",
            ("t", tenant.TenantId));

        Assert.Equal(0, await verifier.VerifyTenantAsync(tenant.TenantId, default));
        Assert.Equal(0L, await OpenAlerts());

        var sequence = await ScalarAsync<long>("SELECT sequence FROM ti.audit_checkpoint WHERE tenant_id = @t", ("t", tenant.TenantId));
        var forged = System.Security.Cryptography.SHA256.HashData("forged"u8);
        foreach (var table in new[] { "audit_event", "audit_checkpoint" })
        {
            await ExecAsync($"ALTER TABLE ti.{table} DISABLE TRIGGER {table}_append_only");
            try
            {
                await ExecAsync($"UPDATE ti.{table} SET event_hash = @h WHERE tenant_id = @t AND sequence = @s",
                    ("h", forged), ("t", tenant.TenantId), ("s", sequence));
            }
            finally
            {
                await ExecAsync($"ALTER TABLE ti.{table} ENABLE TRIGGER {table}_append_only");
            }
        }
        Assert.Equal(2, await verifier.VerifyTenantAsync(tenant.TenantId, default)); // EventHashMismatch and DatabaseCheckpointMismatch
        Assert.Equal(1L, await OpenAlerts());
        var detail = await ScalarAsync<string>(
            "SELECT details::text FROM ti.alert WHERE tenant_id = @t AND type = 'AuditChainFailure' AND status = 'Open'", ("t", tenant.TenantId));
        Assert.Contains("EventHashMismatch", detail, StringComparison.Ordinal);
        Assert.Contains("DatabaseCheckpointMismatch", detail, StringComparison.Ordinal);

        // A forged external file: valid shape, wrong signature, is a third problem; the one open alert stays open (deduplicated).
        var file = Path.Combine(_retentionDir, tenant.TenantId.ToString(), "2098-03-01.json");
        var text = await File.ReadAllTextAsync(file);
        var signature = System.Text.Json.Nodes.JsonNode.Parse(text)!["SignatureHex"]!.GetValue<string>();
        await File.WriteAllTextAsync(file, text.Replace(signature, new string('0', signature.Length), StringComparison.Ordinal));
        Assert.Equal(3, await verifier.VerifyTenantAsync(tenant.TenantId, default));
        Assert.Equal(1L, await OpenAlerts());
    }

    // The session type is internal to Infrastructure; its connection is read reflectively for this test only.
    private static (NpgsqlConnection, NpgsqlTransaction) RawOf(Application.Persistence.ITenantSession s) =>
        ((NpgsqlConnection)s.GetType().GetProperty("Connection")!.GetValue(s)!,
         (NpgsqlTransaction)s.GetType().GetProperty("Transaction")!.GetValue(s)!);

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class RecordingSink(Guid? failFor = null) : IOutboxSink
    {
        private readonly ConcurrentDictionary<Guid, int> _attempts = new();

        public ConcurrentBag<OutboxMessage> Delivered { get; } = [];

        public int Attempts(Guid eventId) => _attempts.GetValueOrDefault(eventId);

        public Task DeliverAsync(OutboxMessage message, CancellationToken ct)
        {
            _attempts.AddOrUpdate(message.EventId, 1, (_, n) => n + 1);
            if (message.EventId == failFor)
                throw new InvalidOperationException("Simulated delivery failure.");
            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }
}
