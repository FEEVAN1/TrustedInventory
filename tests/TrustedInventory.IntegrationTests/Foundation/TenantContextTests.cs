using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Tenant context through the real unit of work and Npgsql pool (TEN-003), and idempotency (STK-007).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class TenantContextTests(ApiFixture fx)
{
    private sealed class FixedCaller(CallerContext caller) : ICallerAccessor
    {
        public CallerContext Caller { get; } = caller;
    }

    private static CallerContext Caller(Guid tenantId, string subject = "user-test") => new(subject, ActorType.User, tenantId,
        TenantStatus.Active, CallerContext.GrantsFrom([(RoleName.TenantAdministrator, null)]));

    private static TenantUnitOfWork Uow(AppDataSource source, Guid tenantId, string subject = "user-test") =>
        new(source, new FixedCaller(Caller(tenantId, subject)), new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    [Trait("Requirement", "TEN-003")]
    public async Task Tenant_context_never_leaks_across_pooled_connection_reuse()
    {
        // One physical connection, reused by every transaction below.
        var single = new NpgsqlConnectionStringBuilder(fx.AppConnection) { MaxPoolSize = 1, Pooling = true }.ConnectionString;
        await using var raw = DependencyInjection.BuildDataSource(single);
        var source = new AppDataSource(raw);

        async Task<(int Pid, List<Guid> Tenants)> SeenBy(Guid tenantId) =>
            await Uow(source, tenantId).ReadAsync(async (s, ct) =>
            {
                var pid = await s.Db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
                var tenants = await s.Db.Set<Warehouse>().Select(w => w.TenantId).Distinct().ToListAsync(ct);
                return (pid, tenants);
            });

        var a = await SeenBy(fx.Seed.A.TenantId);
        var b = await SeenBy(fx.Seed.B.TenantId);
        Assert.Equal([fx.Seed.A.TenantId], a.Tenants);
        Assert.Equal([fx.Seed.B.TenantId], b.Tenants);
        Assert.Equal(a.Pid, b.Pid);

        // The same connection with no tenant context set sees nothing (fail closed).
        await using var conn = await raw.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid(), (SELECT count(*) FROM ti.warehouse), current_setting('app.tenant_id', true)", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(a.Pid, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.True(reader.IsDBNull(2) || reader.GetString(2).Length == 0);
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "AT-STK-02")]
    public async Task Idempotent_command_replays_once_and_rejects_a_different_payload()
    {
        var source = new AppDataSource(DependencyInjection.BuildDataSource(fx.AppConnection));
        var uow = Uow(source, fx.Seed.A.TenantId, "user-idempotency");
        var key = Guid.NewGuid();
        var code = "IDEM-" + key.ToString("N")[..8].ToUpperInvariant();
        var payload = new JsonObject { ["code"] = code };
        var executions = 0;

        Task<CommandOutcome> Run(JsonObject body) => uow.WriteIdempotentAsync(new IdempotencyRequest(key, "POST /test/idempotent", body),
            async (s, ct) =>
            {
                executions++;
                var w = Warehouse.Create(s.Caller.TenantId, code, "Idempotent", "UTC", s.Caller.Subject, s.Now);
                s.Db.Add(w);
                await s.Db.SaveChangesAsync(ct);
                return new CommandOutcome(201, new JsonObject { ["id"] = w.Id.ToString() });
            });

        var first = await Run(payload);
        var replay = await Run((JsonObject)payload.DeepClone());
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.Body!.ToJsonString(), replay.Body!.ToJsonString());
        Assert.Equal(1, executions);
        Assert.Equal(1, await CountWarehousesAsync(fx.Seed.A.TenantId, code));

        var mismatch = await Assert.ThrowsAsync<AppException>(() => Run(new JsonObject { ["code"] = code + "-X" }));
        Assert.Equal(ErrorCode.IdempotencyMismatch, mismatch.Code);
        Assert.Equal(1, executions);
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    public async Task Idempotency_keys_are_scoped_per_tenant_and_principal()
    {
        var source = new AppDataSource(DependencyInjection.BuildDataSource(fx.AppConnection));
        var key = Guid.NewGuid();
        var payload = new JsonObject { ["x"] = 1 };
        var executions = 0;

        Task<CommandOutcome> Run(Guid tenant, string subject) => Uow(source, tenant, subject).WriteIdempotentAsync(
            new IdempotencyRequest(key, "POST /test/scope", payload), (s, ct) =>
            {
                executions++;
                return Task.FromResult(new CommandOutcome(200, new JsonObject { ["ok"] = true }));
            });

        await Run(fx.Seed.A.TenantId, "user-one");
        await Run(fx.Seed.A.TenantId, "user-two");
        await Run(fx.Seed.B.TenantId, "user-one");
        Assert.Equal(3, executions);
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    public async Task Deterministic_rejection_is_stored_and_replayed_without_side_effects()
    {
        var source = new AppDataSource(DependencyInjection.BuildDataSource(fx.AppConnection));
        var uow = Uow(source, fx.Seed.A.TenantId, "user-rejection");
        var key = Guid.NewGuid();
        var code = "REJ-" + key.ToString("N")[..8].ToUpperInvariant();
        var executions = 0;

        Task<CommandOutcome> Run() => uow.WriteIdempotentAsync(new IdempotencyRequest(key, "POST /test/reject", new JsonObject { ["code"] = code }),
            async (s, ct) =>
            {
                executions++;
                s.Db.Add(Warehouse.Create(s.Caller.TenantId, code, "Rolled back", "UTC", s.Caller.Subject, s.Now));
                await s.Db.SaveChangesAsync(ct);
                throw new AppException(ErrorCode.InsufficientStock, "Not enough stock.");
            });

        var first = await Run();
        var second = await Run();
        Assert.Equal(409, first.Status);
        Assert.Equal("INSUFFICIENT_STOCK", (string?)first.Body!["code"]);
        Assert.True(second.Replayed);
        Assert.Equal(1, executions);
        Assert.Equal(0, await CountWarehousesAsync(fx.Seed.A.TenantId, code));
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "AT-STK-02")]
    public async Task Concurrent_requests_with_the_same_key_post_once()
    {
        var source = new AppDataSource(DependencyInjection.BuildDataSource(fx.AppConnection));
        var key = Guid.NewGuid();
        var code = "RACE-" + key.ToString("N")[..8].ToUpperInvariant();
        var payload = new JsonObject { ["code"] = code };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Uow(source, fx.Seed.A.TenantId, "user-race").WriteIdempotentAsync(new IdempotencyRequest(key, "POST /test/race", payload),
                async (s, ct) =>
                {
                    // Not unique-constrained on its own: only the idempotency record can stop duplicates.
                    s.Db.Add(Warehouse.Create(s.Caller.TenantId, code + "-" + Guid.NewGuid().ToString("N")[..4], "Race", "UTC", s.Caller.Subject, s.Now));
                    await s.Db.SaveChangesAsync(ct);
                    await Task.Delay(50, ct);
                    return new CommandOutcome(201, new JsonObject { ["ok"] = true });
                })));

        Assert.All(outcomes, o => Assert.Equal(201, o.Status));
        Assert.Single(outcomes, o => !o.Replayed);
        Assert.Equal(1, await CountWarehousesAsync(fx.Seed.A.TenantId, code + "-%"));
    }

    private async Task<long> CountWarehousesAsync(Guid tenantId, string codePattern)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM ti.warehouse WHERE tenant_id = @t AND code LIKE @c", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", codePattern);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
