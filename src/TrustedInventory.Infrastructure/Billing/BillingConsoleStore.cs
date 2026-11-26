using System.Data;
using Npgsql;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>Platform billing console queries on the ti_control pool (DEC-066). Payloads are never read here.</summary>
public sealed class BillingConsoleStore(ControlDataSource control) : IBillingConsoleStore
{
    private const string TaskColumns = "id, kind::text, status::text, tenant_id, detail, created_at, resolved_at, resolved_by, resolution";

    public async Task<IReadOnlyList<ReconciliationTaskDto>> ListTasksAsync(ReconciliationTaskStatus? status, ReconciliationTaskKind? kind,
        CursorPosition? after, int take, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand($"""
            SELECT {TaskColumns} FROM cp.billing_task
            WHERE (@status::text IS NULL OR status::text = @status) AND (@kind::text IS NULL OR kind::text = @kind)
              AND (@at::timestamptz IS NULL OR (created_at, id) < (@at, @id))
            ORDER BY created_at DESC, id DESC LIMIT @take
            """);
        cmd.Parameters.AddWithValue("status", (object?)status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("kind", (object?)kind?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", (object?)after?.CreatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", after?.Id ?? Guid.Empty);
        cmd.Parameters.AddWithValue("take", take);
        var rows = new List<ReconciliationTaskDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(ReadTask(r));
        return rows;
    }

    public async Task<ReconciliationTaskDto> ResolveAsync(Guid id, PlatformCaller caller, string resolution, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        ReconciliationTaskDto task;
        await using (var find = new NpgsqlCommand($"SELECT {TaskColumns} FROM cp.billing_task WHERE id = @id FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("id", id);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw AppException.NotFound("Reconciliation task");
            task = ReadTask(r);
        }
        if (task.Status == ReconciliationTaskStatus.Resolved)
            throw AppException.InvalidState("This task is already resolved.");

        await using (var update = new NpgsqlCommand("""
            UPDATE cp.billing_task SET status = 'Resolved', resolved_at = @now, resolved_by = @by, resolution = @res, version = version + 1 WHERE id = @id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("by", caller.Subject);
            update.Parameters.AddWithValue("res", resolution);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        if (task.TenantId is { } tenant)
            await AuditAppender.AppendAsync(conn, tx, tenant, new AuditActor(caller.Subject, ActorType.User, caller.RequestId, null), now,
                [new AuditEntry("BillingTaskResolved", "BillingTask", id, null, resolution, new { status = "Open", kind = task.Kind.ToString() },
                    new { status = "Resolved" })], ct);
        await tx.CommitAsync(ct);
        return task with { Status = ReconciliationTaskStatus.Resolved, ResolvedAt = now, ResolvedBy = caller.Subject, Resolution = resolution };
    }

    public async Task<IReadOnlyList<WebhookEventDto>> ListWebhookEventsAsync(WebhookProcessingState? state, CursorPosition? after, int take,
        CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("""
            SELECT id, provider, event_id, event_type, processing_state::text, provider_created_at, received_at, processed_at, attempts, failure_code
            FROM cp.webhook_inbox
            WHERE (@state::text IS NULL OR processing_state::text = @state) AND (@at::timestamptz IS NULL OR (received_at, id) < (@at, @id))
            ORDER BY received_at DESC, id DESC LIMIT @take
            """);
        cmd.Parameters.AddWithValue("state", (object?)state?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", (object?)after?.CreatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", after?.Id ?? Guid.Empty);
        cmd.Parameters.AddWithValue("take", take);
        var rows = new List<WebhookEventDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new WebhookEventDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), Enum.Parse<WebhookProcessingState>(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9)));
        return rows;
    }

    private static ReconciliationTaskDto ReadTask(NpgsqlDataReader r) =>
        new(r.GetGuid(0), Enum.Parse<ReconciliationTaskKind>(r.GetString(1)), Enum.Parse<ReconciliationTaskStatus>(r.GetString(2)),
            r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));
}
