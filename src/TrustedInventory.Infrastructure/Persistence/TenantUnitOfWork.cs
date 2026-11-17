using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure.Audit;

namespace TrustedInventory.Infrastructure.Persistence;

/// <summary>Connection pool for the runtime role ti_app (TEN-002). Never the migration or platform identity.</summary>
public sealed class AppDataSource(NpgsqlDataSource source) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    public ValueTask DisposeAsync() => Source.DisposeAsync();

    public void Dispose() => Source.Dispose();
}

/// <summary>
/// Connection pool for ti_platform: tenant provisioning tables only, no stock privileges (TEN-004). Used for
/// platform administration and by jobs to enumerate tenant ids before opening per-tenant ti_app transactions.
/// </summary>
public sealed class PlatformDataSource(NpgsqlDataSource source) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    public ValueTask DisposeAsync() => Source.DisposeAsync();

    public void Dispose() => Source.Dispose();
}

/// <summary>
/// Implements the per-command transaction (DEC-013): open, BEGIN, set_config('app.tenant_id', …, true), work,
/// audit + outbox, COMMIT. The tenant setting is transaction-local, so it cannot leak through the pool (TEN-003).
/// </summary>
public sealed class TenantUnitOfWork(AppDataSource dataSource, ICallerAccessor callers, TimeProvider clock) : ITenantUnitOfWork
{
    private const int MaxRetries = 3;

    public Task<T> ReadAsync<T>(Func<ITenantSession, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        RunAsync(work, readOnly: true, ct);

    public Task<T> WriteAsync<T>(Func<ITenantSession, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        RunAsync(work, readOnly: false, ct);

    public async Task<CommandOutcome> WriteIdempotentAsync(IdempotencyRequest request,
        Func<ITenantSession, CancellationToken, Task<CommandOutcome>> work, CancellationToken ct = default)
    {
        var caller = callers.Caller;
        var digest = CanonicalJson.PayloadDigest(request.Payload);
        try
        {
            return await RunAsync(async (s, c) =>
            {
                var session = (Session)s;
                if (await IdempotencyStore.FindAsync(session, request, c) is { } stored)
                    return IdempotencyStore.Replay(stored, digest);

                CommandOutcome outcome;
                try
                {
                    outcome = await work(s, c);
                }
                catch (AppException ex) when (IdempotencyStore.IsDeterministicRejection(ex))
                {
                    // Rejections are stored too, so a retry of the same payload gets the same answer (STK-007).
                    throw new StoreRejection(ex);
                }
                await IdempotencyStore.InsertAsync(session, request, digest, outcome, c);
                return outcome;
            }, readOnly: false, ct);
        }
        catch (StoreRejection rejection)
        {
            // The command's own changes were rolled back; record only the rejection, then report it.
            var body = ProblemBody.From(rejection.Inner, caller.RequestId);
            var outcome = new CommandOutcome(rejection.Inner.Status, body);
            try
            {
                await RunAsync<bool>(async (s, c) =>
                {
                    await IdempotencyStore.InsertAsync((Session)s, request, digest, outcome, c);
                    return true;
                }, readOnly: false, ct);
            }
            catch (IdempotencyRace)
            {
                return await ReplayAfterRaceAsync(request, digest, ct);
            }
            return outcome;
        }
        catch (IdempotencyRace)
        {
            // A concurrent request with the same key committed first; our side effects were rolled back.
            return await ReplayAfterRaceAsync(request, digest, ct);
        }
    }

    private Task<CommandOutcome> ReplayAfterRaceAsync(IdempotencyRequest request, byte[] digest, CancellationToken ct) =>
        RunAsync(async (s, c) =>
        {
            var stored = await IdempotencyStore.FindAsync((Session)s, request, c)
                ?? throw new AppException(ErrorCode.RetryableConflict, "A concurrent request is still running. Retry shortly.");
            return IdempotencyStore.Replay(stored, digest);
        }, readOnly: true, ct);

    private async Task<T> RunAsync<T>(Func<ITenantSession, CancellationToken, Task<T>> work, bool readOnly, CancellationToken ct)
    {
        var caller = callers.Caller;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(caller, work, readOnly, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt <= MaxRetries)
            {
                Application.Common.OperationsMetrics.TransactionRetried();
                // STK-005: bounded retry with jitter on deadlock / serialization failure.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt), ct);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw new AppException(ErrorCode.RetryableConflict, "The operation conflicted with concurrent changes. Retry it.");
            }
        }
    }

    private async Task<T> RunOnceAsync<T>(CallerContext caller, Func<ITenantSession, CancellationToken, Task<T>> work,
        bool readOnly, CancellationToken ct)
    {
        await using var conn = await dataSource.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (readOnly)
        {
            // Must precede every other statement in the transaction.
            await using var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
            await ro.ExecuteNonQueryAsync(ct);
        }
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", caller.TenantId.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }

        await using var db = new AppDbContext(DbOptions.For(conn));
        await db.Database.UseTransactionAsync(tx, ct);
        var session = new Session(db, conn, tx, caller, Truncate(clock.GetUtcNow()), readOnly);

        T result;
        try
        {
            result = await work(session, ct);
            if (!readOnly)
            {
                if (db.ChangeTracker.HasChanges())
                    await db.SaveChangesAsync(ct);
                await OutboxWriter.WriteAsync(conn, tx, caller.TenantId, session.Events, ct);
                await AuditAppender.AppendAsync(conn, tx,
                    caller.TenantId, new AuditActor(caller.Subject, caller.ActorType, caller.RequestId, caller.CorrelationId),
                    session.Now, session.AuditEntries, ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AppException.VersionConflict();
        }
        catch (Exception ex) when (Unwrap(ex) is PostgresException pg && !IsTransient(pg))
        {
            throw Translate(pg) ?? ex;
        }
        return result;
    }

    /// <summary>PostgreSQL stores microseconds; truncating keeps returned values equal to what is stored.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

    private static PostgresException? Unwrap(Exception ex) => ex as PostgresException ?? ex.InnerException as PostgresException;

    private static bool IsTransient(Exception ex) =>
        Unwrap(ex) is { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected };

    /// <summary>
    /// Constraint violations that slipped past validation. Messages are generic: constraint text can name
    /// columns or values and must never reach the client (section 15).
    /// </summary>
    /// <summary>Raised by trigger stock_movement_count_lock (migration 0016, DEC-059).</summary>
    private const string CountLockSqlState = "TI001";

    private static Exception? Translate(PostgresException pg) => pg.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation when pg.ConstraintName == IdempotencyStore.UniqueConstraint => new IdempotencyRace(),
        PostgresErrorCodes.UniqueViolation when pg.ConstraintName == "stock_count_bin_one_lock_uq" =>
            new AppException(ErrorCode.CountLocked, "Another count already locks one of these bins (EXC-003)."),
        PostgresErrorCodes.UniqueViolation => AppException.InvalidState("The change conflicts with an existing record."),
        PostgresErrorCodes.ForeignKeyViolation => new AppException(ErrorCode.ValidationFailed, "A referenced record does not exist."),
        PostgresErrorCodes.CheckViolation => new AppException(ErrorCode.ValidationFailed, "A value is outside the allowed range."),
        PostgresErrorCodes.InsufficientPrivilege => AppException.Forbidden(),
        CountLockSqlState => new AppException(ErrorCode.CountLocked, "A stock count has locked this bin; wait until the count is posted or cancelled (EXC-003)."),
        _ => null,
    };

    internal sealed class Session(AppDbContext db, NpgsqlConnection conn, NpgsqlTransaction tx, CallerContext caller,
        DateTimeOffset now, bool readOnly) : ITenantSession
    {
        public DbContext Db => db;
        public NpgsqlConnection Connection => conn;
        public NpgsqlTransaction Transaction => tx;
        public CallerContext Caller => caller;
        public DateTimeOffset Now => now;
        public List<AuditEntry> AuditEntries { get; } = [];
        public List<(string Type, object Payload)> Events { get; } = [];

        public void Audit(AuditEntry entry)
        {
            if (readOnly)
                throw new InvalidOperationException("Audit events cannot be recorded in a read-only session.");
            AuditEntries.Add(entry);
        }

        public void Publish(string eventType, object payload)
        {
            if (readOnly)
                throw new InvalidOperationException("Events cannot be published from a read-only session.");
            Events.Add((eventType, payload));
        }
    }

    private sealed class StoreRejection(AppException inner) : Exception(inner.Message, inner)
    {
        public AppException Inner { get; } = inner;
    }

    internal sealed class IdempotencyRace : Exception;
}

internal static class DbOptions
{
    public static DbContextOptions<AppDbContext> For(NpgsqlConnection conn) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn, PgEnums.Map)
            .Options;
}

/// <summary>Durable post-commit delivery queue (ARC-003): rows are written in the command's transaction.</summary>
internal static class OutboxWriter
{
    public static async Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId,
        IReadOnlyList<(string Type, object Payload)> events, CancellationToken ct)
    {
        foreach (var (type, payload) in events)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload) VALUES (@t, @e, @type, @payload)", conn, tx);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("e", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options),
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

internal sealed record StoredOutcome(byte[] Digest, int Status, JsonNode? Body);

/// <summary>Idempotency records scoped by tenant (RLS), principal, route and key (STK-007).</summary>
internal static class IdempotencyStore
{
    public const string UniqueConstraint = "idempotency_record_tenant_id_principal_route_key_key";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static async Task<StoredOutcome?> FindAsync(TenantUnitOfWork.Session s, IdempotencyRequest request, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT payload_digest, response_status, response_body FROM ti.idempotency_record
            WHERE principal = @p AND route = @r AND key = @k
            """, s.Connection, s.Transaction);
        cmd.Parameters.AddWithValue("p", s.Caller.Subject);
        cmd.Parameters.AddWithValue("r", request.Route);
        cmd.Parameters.AddWithValue("k", request.Key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new StoredOutcome((byte[])reader[0], reader.GetInt16(1), JsonNode.Parse(reader.GetString(2)));
    }

    public static CommandOutcome Replay(StoredOutcome stored, byte[] digest) =>
        stored.Digest.AsSpan().SequenceEqual(digest)
            ? new CommandOutcome(stored.Status, stored.Body, Replayed: true)
            : throw new AppException(ErrorCode.IdempotencyMismatch,
                "This Idempotency-Key was already used with a different request body.");

    public static async Task InsertAsync(TenantUnitOfWork.Session s, IdempotencyRequest request, byte[] digest,
        CommandOutcome outcome, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO ti.idempotency_record (tenant_id, principal, route, key, payload_digest, response_status, response_body, created_at, expires_at)
            VALUES (@t, @p, @r, @k, @d, @s, @b, @now, @exp)
            """, s.Connection, s.Transaction);
        cmd.Parameters.AddWithValue("t", s.Caller.TenantId);
        cmd.Parameters.AddWithValue("p", s.Caller.Subject);
        cmd.Parameters.AddWithValue("r", request.Route);
        cmd.Parameters.AddWithValue("k", request.Key);
        cmd.Parameters.AddWithValue("d", digest);
        cmd.Parameters.AddWithValue("s", (short)outcome.Status);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Jsonb) { Value = outcome.Body?.ToJsonString() ?? "null" });
        cmd.Parameters.AddWithValue("now", s.Now);
        cmd.Parameters.AddWithValue("exp", s.Now + Retention);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Business rejections are stable for a given payload; transient and precondition failures are not stored. A stock
    /// block (OPS-001) or a count lock (DEC-059) is transient: once it clears, a retry with the same key must be able to post.
    /// </summary>
    public static bool IsDeterministicRejection(AppException ex) =>
        ex.Status is >= 400 and < 500
        && ex.Code is not (ErrorCode.RetryableConflict or ErrorCode.VersionConflict or ErrorCode.PreconditionRequired
            or ErrorCode.IdempotencyMismatch or ErrorCode.RateLimited or ErrorCode.Forbidden or ErrorCode.Unauthenticated
            or ErrorCode.StockBlocked or ErrorCode.CountLocked);
}
