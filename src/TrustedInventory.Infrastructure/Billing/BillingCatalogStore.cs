using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// Plan and price catalog on the ti_control pool (DEC-035, DEC-062). Drafts are changed under a row lock with a revision
/// check; every change writes a cp.catalog_event row in the same transaction.
/// </summary>
public sealed class BillingCatalogStore(ControlDataSource control) : IBillingCatalogStore
{
    private const string PlanColumns = """
        SELECT id, plan_code, version, display_name, features, quotas::text, trial_days, is_public, published_at, revision, created_at
        FROM cp.plan_version
        """;

    public async Task<IReadOnlyList<CatalogPlanRow>> ListAsync(string? planCode, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        return await ReadPlansAsync(conn, null, planCode is null ? "" : " WHERE plan_code = @code", ct, ("code", planCode ?? ""));
    }

    public async Task<CatalogPlanRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        return (await ReadPlansAsync(conn, null, " WHERE id = @id", ct, ("id", id))).SingleOrDefault();
    }

    public async Task<CatalogPlanRow> CreatePlanAsync(NewPlanVersion plan, CatalogChange change, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Serialise version numbering per plan code; the unique (plan_code, version) is the backstop.
        await ExecAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended('plan:' || @code, 0))", ct, ("code", plan.PlanCode));
        var id = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO cp.plan_version (id, plan_code, version, display_name, features, quotas, trial_days, is_public, created_by, created_at)
            SELECT @id, @code, coalesce(max(version), 0) + 1, @name, @features, @quotas::jsonb, @trial, true, @actor, @at
            FROM cp.plan_version WHERE plan_code = @code
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("code", plan.PlanCode);
            insert.Parameters.AddWithValue("name", plan.Name);
            insert.Parameters.AddWithValue("features", plan.Features.ToArray());
            insert.Parameters.AddWithValue("quotas", JsonSerializer.Serialize(plan.Limits));
            insert.Parameters.AddWithValue("trial", plan.TrialDays);
            insert.Parameters.AddWithValue("actor", change.Actor);
            insert.Parameters.AddWithValue("at", change.At);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await RecordAsync(conn, tx, "PlanVersionCreated", id, null, change, new { plan.PlanCode, plan.Features, plan.Limits, plan.TrialDays }, ct);
        await tx.CommitAsync(ct);
        return (await ReadPlansAsync(conn, null, " WHERE id = @id", ct, ("id", id))).Single();
    }

    public async Task<CatalogPriceRow> AddPriceAsync(Guid planVersionId, int expectedRevision, NewPriceVersion price, CatalogChange change,
        CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockDraftAsync(conn, tx, planVersionId, expectedRevision, ct);
        var id = Guid.CreateVersion7();
        try
        {
            await ExecAsync(conn, tx, """
                INSERT INTO cp.price_version (id, plan_version_id, currency, minor_unit_amount, exponent, billing_interval, active_from,
                                              provider, provider_price_ref, created_by, created_at)
                VALUES (@id, @plan, @cur, @amt, @exp, @int::cp.billing_interval, @from, @provider, @ref, @actor, @at)
                """, ct, ("id", id), ("plan", planVersionId), ("cur", price.Currency), ("amt", price.MinorUnits), ("exp", (short)price.Exponent),
                ("int", price.Interval.ToString()), ("from", price.ActiveFrom), ("provider", (object?)price.Provider ?? DBNull.Value),
                ("ref", (object?)price.ProviderPriceRef ?? DBNull.Value), ("actor", change.Actor), ("at", change.At));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw AppException.Validation("providerPriceRef", "That provider price reference is already used by another price.");
        }
        await ExecAsync(conn, tx, "UPDATE cp.plan_version SET revision = revision + 1 WHERE id = @id", ct, ("id", planVersionId));
        await RecordAsync(conn, tx, "PriceVersionCreated", planVersionId, id, change,
            new { price.Currency, price.MinorUnits, price.Exponent, Interval = price.Interval.ToString(), price.ActiveFrom }, ct);
        await tx.CommitAsync(ct);
        var plan = (await ReadPlansAsync(conn, null, " WHERE id = @id", ct, ("id", planVersionId))).Single();
        return plan.Prices.Single(p => p.Id == id);
    }

    public async Task<CatalogPlanRow> PublishAsync(Guid planVersionId, int expectedRevision, CatalogChange change, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockDraftAsync(conn, tx, planVersionId, expectedRevision, ct);
        var prices = await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM cp.price_version WHERE plan_version_id = @id", ct, ("id", planVersionId));
        if (prices == 0)
            throw AppException.Validation("prices", "Add at least one price before publishing.");
        // The last change to the row: once published_at is set the 0004 guard freezes it (ENT-001).
        await ExecAsync(conn, tx, "UPDATE cp.plan_version SET published_at = @at, revision = revision + 1 WHERE id = @id", ct,
            ("id", planVersionId), ("at", change.At));
        await RecordAsync(conn, tx, "PlanVersionPublished", planVersionId, null, change, new { publishedAt = change.At }, ct);
        await tx.CommitAsync(ct);
        return (await ReadPlansAsync(conn, null, " WHERE id = @id", ct, ("id", planVersionId))).Single();
    }

    private static async Task LockDraftAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, int expectedRevision, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT revision, published_at FROM cp.plan_version WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw AppException.NotFound("Plan version");
        var revision = r.GetInt32(0);
        var published = !r.IsDBNull(1);
        await r.CloseAsync();
        if (revision != expectedRevision)
            throw AppException.VersionConflict();
        if (published)
            throw AppException.InvalidState("The plan version is published and immutable; create a new version for new terms (ENT-001).");
    }

    private static async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string action, Guid planVersionId, Guid? priceVersionId,
        CatalogChange change, object detail, CancellationToken ct) =>
        await ExecAsync(conn, tx, """
            INSERT INTO cp.catalog_event (action, plan_version_id, price_version_id, actor_subject, reason, request_id, detail, occurred_at)
            VALUES (@action::cp.catalog_action, @plan, @price, @actor, @reason, @request, @detail::jsonb, @at)
            """, ct, ("action", action), ("plan", planVersionId), ("price", (object?)priceVersionId ?? DBNull.Value), ("actor", change.Actor),
            ("reason", change.Reason), ("request", (object?)change.RequestId ?? DBNull.Value),
            ("detail", JsonSerializer.Serialize(detail, Application.Common.JsonDefaults.Options)), ("at", change.At));

    private static async Task<IReadOnlyList<CatalogPlanRow>> ReadPlansAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string where,
        CancellationToken ct, params (string Name, object Value)[] args)
    {
        var plans = new List<(Guid Id, string Code, int Version, string Name, string[] Features, string Quotas, int Trial, bool Public,
            DateTimeOffset? Published, int Revision, DateTimeOffset Created)>();
        await using (var cmd = new NpgsqlCommand(PlanColumns + where, conn, tx))
        {
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                plans.Add((r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetString(5), r.GetInt32(6),
                    r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10)));
        }
        if (plans.Count == 0)
            return [];

        var prices = new List<CatalogPriceRow>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, plan_version_id, currency, minor_unit_amount, exponent, billing_interval::text, active_from, active_to, display_approved,
                   provider, provider_price_ref
            FROM cp.price_version WHERE plan_version_id = ANY(@ids) ORDER BY created_at, id
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = plans.Select(p => p.Id).ToArray() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                prices.Add(new CatalogPriceRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2).Trim(), r.GetInt64(3), r.GetInt16(4),
                    Enum.Parse<BillingInterval>(r.GetString(5)), r.GetFieldValue<DateTimeOffset>(6),
                    r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10)));
        }

        return plans.Select(p =>
        {
            using var doc = JsonDocument.Parse(p.Quotas);
            var limits = doc.RootElement.EnumerateObject()
                .ToDictionary(e => e.Name, e => e.Value.ValueKind == JsonValueKind.Number ? e.Value.GetInt64() : (long?)null, StringComparer.Ordinal);
            return new CatalogPlanRow(p.Id, p.Code, p.Version, p.Name, p.Features, limits, p.Trial, p.Public, p.Published, p.Revision, p.Created,
                prices.Where(x => x.PlanVersionId == p.Id).ToList());
        }).ToList();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
