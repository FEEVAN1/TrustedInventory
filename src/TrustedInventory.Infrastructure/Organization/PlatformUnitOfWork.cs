using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Organization;

/// <summary>
/// Transactions on the ti_platform pool (TEN-004): no tenant context, access limited by the platform policies
/// to tenants, memberships, role grants and audit streams. Audit entries are appended to each affected tenant's
/// chain in the same transaction (AUD-001, ARC-003).
/// </summary>
public sealed class PlatformUnitOfWork(PlatformDataSource dataSource, TimeProvider clock) : IPlatformUnitOfWork
{
    public Task<T> ReadAsync<T>(PlatformCaller caller, Func<IPlatformSession, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        RunAsync(caller, work, readOnly: true, ct);

    public Task<T> WriteAsync<T>(PlatformCaller caller, Func<IPlatformSession, CancellationToken, Task<T>> work, CancellationToken ct = default) =>
        RunAsync(caller, work, readOnly: false, ct);

    private async Task<T> RunAsync<T>(PlatformCaller caller, Func<IPlatformSession, CancellationToken, Task<T>> work, bool readOnly,
        CancellationToken ct)
    {
        await using var conn = await dataSource.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (readOnly)
        {
            await using var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
            await ro.ExecuteNonQueryAsync(ct);
        }

        await using var db = new AppDbContext(DbOptions.For(conn));
        await db.Database.UseTransactionAsync(tx, ct);
        var now = clock.GetUtcNow();
        var session = new Session(db, conn, tx, new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero), readOnly);

        try
        {
            var result = await work(session, ct);
            if (!readOnly)
            {
                if (db.ChangeTracker.HasChanges())
                    await db.SaveChangesAsync(ct);
                var actor = new AuditActor(caller.Subject, ActorType.User, caller.RequestId, null);
                foreach (var group in session.Entries.GroupBy(e => e.TenantId))
                    await AuditAppender.AppendAsync(conn, tx, group.Key, actor, session.Now, group.Select(e => e.Entry).ToList(), ct);
            }
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AppException.VersionConflict();
        }
        catch (Exception ex) when ((ex as PostgresException ?? ex.InnerException as PostgresException) is { } pg)
        {
            throw pg.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => AppException.InvalidState("The change conflicts with an existing record."),
                PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation =>
                    new AppException(ErrorCode.ValidationFailed, "A value is outside the allowed range."),
                _ => ex,
            };
        }
    }

    private sealed class Session(AppDbContext db, NpgsqlConnection conn, NpgsqlTransaction tx, DateTimeOffset now, bool readOnly)
        : IPlatformSession
    {
        public DbContext Db => db;
        public DateTimeOffset Now => now;
        public List<(Guid TenantId, AuditEntry Entry)> Entries { get; } = [];

        public void Audit(Guid tenantId, AuditEntry entry)
        {
            if (readOnly)
                throw new InvalidOperationException("Audit events cannot be recorded in a read-only session.");
            Entries.Add((tenantId, entry));
        }

        public async Task CreateAuditStreamAsync(Guid tenantId, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO ti.audit_stream (tenant_id) VALUES (@t)", conn, tx);
            cmd.Parameters.AddWithValue("t", tenantId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
