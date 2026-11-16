using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public enum ExcursionStatus { Open, Acknowledged, Resolved }

public enum ExcursionDisposition { Release, KeepOnHold, Damaged }

public sealed record AcknowledgeRequest(string? Note);

public sealed record ResolveExcursionRequest(ExcursionDisposition Disposition, string Reason, Guid ApprovalId);

public sealed record ExcursionDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ZoneId, Guid DeviceId,
    Guid StorageProfileId, ExcursionStatus Status, bool Late, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, decimal FirstAbnormalCelsius,
    decimal LastAbnormalCelsius, decimal MinCelsius, decimal MaxCelsius, IReadOnlyList<Guid> AffectedBucketIds, IReadOnlyList<Guid> AffectedProductIds,
    IReadOnlyList<Guid> AffectedLotIds, IReadOnlyList<Guid> PotentiallyAffectedShipmentIds, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt,
    ExcursionDisposition? Disposition, string? ResolvedBy, DateTimeOffset? ResolvedAt, Guid? ApprovalId) : IVersioned;

public sealed record ExcursionFilter(ExcursionStatus? Status, Guid? ZoneId, Guid? WarehouseId);

public sealed record AlertDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, AlertType Type, AlertSeverity Severity,
    AlertStatus Status, Guid? WarehouseId, Guid? ZoneId, string Title, string? Detail, string? SubjectType, Guid? SubjectId, IReadOnlyList<Guid> ProductIds,
    IReadOnlyList<Guid> LotIds, decimal? FirstAbnormalCelsius, decimal? LastAbnormalCelsius, DateTimeOffset RaisedAt, string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt) : IVersioned;

public sealed record AlertFilter(AlertType? Type, AlertStatus? Status, AlertSeverity? Severity, Guid? WarehouseId, Guid? ZoneId);

/// <summary>
/// Excursions (TEL-003, TEL-004, section 13, DEC-077): acknowledged by operations, resolved only with a QA-signed ExcursionDisposition
/// approval. Release returns held stock to Available only when it passes MED-001 apart from this excursion, and never in restricted modes.
/// </summary>
public sealed class ExcursionService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors, TimeProvider clock)
{
    public Task<ExcursionDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        return uow.ReadAsync((s, c) => DtoAsync(s, id, c), ct);
    }

    public Task<Page<ExcursionDto>> ListAsync(ExcursionFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        page.EnsureSupportedSort();
        var key = $"excursions|{filter.Status}|{filter.ZoneId}|{filter.WarehouseId}";
        var after = page.Cursor is { } cur ? cursors.Decode(cur, key) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<KeyRow>($"""
                SELECT e.id AS "Id", e.created_at AS "CreatedAt" FROM ti.excursion e JOIN ti.zone z ON z.tenant_id = e.tenant_id AND z.id = e.zone_id
                WHERE ({filter.Status?.ToString()}::text IS NULL OR e.status::text = {filter.Status?.ToString()})
                  AND ({filter.ZoneId}::uuid IS NULL OR e.zone_id = {filter.ZoneId}) AND ({filter.WarehouseId}::uuid IS NULL OR z.warehouse_id = {filter.WarehouseId})
                  AND ({after?.CreatedAt}::timestamptz IS NULL OR e.created_at < {after?.CreatedAt} OR (e.created_at = {after?.CreatedAt} AND e.id < {after?.Id}))
                ORDER BY e.created_at DESC, e.id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            string? next = null;
            if (rows.Count > page.EffectiveLimit)
            {
                rows.RemoveAt(page.EffectiveLimit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), key);
            }
            var items = new List<ExcursionDto>();
            foreach (var r in rows)
                items.Add(await DtoAsync(s, r.Id, c));
            return new Page<ExcursionDto>(items, next);
        }, ct);
    }

    public Task<ExcursionDto> AcknowledgeAsync(Guid id, int expectedVersion, AcknowledgeRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AlertsAcknowledge);
        return uow.WriteAsync(async (s, c) =>
        {
            var row = await LockAsync(s, id, c);
            Versioning.EnsureVersion(row.Version, expectedVersion);
            if (row.Status != nameof(ExcursionStatus.Open))
                throw AppException.InvalidState($"The excursion is {row.Status}.");
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.excursion SET status = 'Acknowledged', acknowledged_by = {caller.Subject}, acknowledged_at = {s.Now},
                       last_modified_by = {caller.Subject}, version = version + 1, updated_at = {s.Now}
                WHERE id = {id}
                """, c);
            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("ExcursionAcknowledged", "Excursion", id, dto.Version, cmd.Note, null, new { status = dto.Status.ToString() }));
            return dto;
        }, ct);
    }

    public Task<CommandOutcome> ResolveAsync(Guid idempotencyKey, Guid id, int expectedVersion, ResolveExcursionRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.QualityHold);
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Length > 2000)
            throw AppException.Validation("reason", "Give a reason of 1 to 2000 characters.");
        // Release makes stock available, so it follows the routine-write rule of the state matrix (docs/saas-state-matrix.md).
        if (cmd.Disposition == ExcursionDisposition.Release && caller.Access is { } access
            && access.Mode(clock.GetUtcNow()) is AccessMode.BillingRestricted or AccessMode.Closed)
            throw AccessGate.Rejection(access, clock.GetUtcNow(), access.Mode(clock.GetUtcNow()), AccessCategory.RoutineWrite);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /excursions/{id}/resolve", payload), async (s, c) =>
        {
            var subject = await ApprovalSubjects.ExcursionAsync(s, id, c);
            Versioning.EnsureVersion(subject.Version, expectedVersion);
            if (!subject.Open)
                throw AppException.InvalidState("The excursion is already resolved.");
            var approval = await ApprovalSubjects.RequireBindingAsync(s, subject, cmd.ApprovalId, c);
            var held = await HeldBucketsAsync(s, id, c);
            if (cmd.Disposition != ExcursionDisposition.KeepOnHold && held.Any(b => b.ReservedQty > 0))
                throw AppException.InvalidState("Reservations still point at held stock; deallocate or unpick them first.");

            var movements = new List<Guid>();
            if (cmd.Disposition == ExcursionDisposition.Release && held.Count > 0)
            {
                foreach (var b in held)
                {
                    var product = await s.Db.Set<Domain.Catalog.Product>().AsNoTracking().SingleAsync(p => p.Id == b.ProductId, c);
                    var facts = await QualityService.FactsAsync(s, b, product, c) with { OpenExcursion = await OtherExcursionAsync(s, b.Id, id, c) };
                    var problems = QualityRules.ReleaseProblems(facts);
                    if (problems.Count > 0)
                        throw new AppException(ErrorCode.ReleaseIneligible, $"Bucket {b.Id} cannot be released: {string.Join(" ", problems.Select(p => p.Detail))}");
                }
                movements.AddRange((await QualityService.MoveWholeAsync(s, caller, held, QualityStatus.Available, DocumentType.Excursion, id,
                    $"Excursion released: {cmd.Reason}", c)).MovementIds);
            }
            else if (cmd.Disposition == ExcursionDisposition.Damaged && held.Count > 0)
            {
                movements.AddRange((await QualityService.MoveWholeAsync(s, caller, held, QualityStatus.Damaged, DocumentType.Excursion, id,
                    $"Excursion damaged: {cmd.Reason}", c)).MovementIds);
            }
            DomainGuard.Run(() => approval.Consume($"excursion-resolve:{id:D}", s.Now));
            await s.Db.SaveChangesAsync(c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.excursion SET status = 'Resolved', disposition = {cmd.Disposition.ToString()}::ti.excursion_disposition,
                       disposition_approval_id = {approval.Id}, resolved_by = {caller.Subject}, resolved_at = {s.Now},
                       acknowledged_by = coalesce(acknowledged_by, {caller.Subject}), acknowledged_at = coalesce(acknowledged_at, {s.Now}),
                       last_modified_by = {caller.Subject}, version = version + 1, updated_at = {s.Now}
                WHERE id = {id}
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.alert SET status = 'Closed', closed_at = {s.Now}, version = version + 1, updated_at = {s.Now}
                WHERE dedupe_key = {$"excursion:{id:D}"} AND status <> 'Closed'
                """, c);
            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("ExcursionResolved", "Excursion", id, dto.Version, cmd.Reason, null,
                new { disposition = cmd.Disposition.ToString(), approvalId = approval.Id, movements }));
            s.Publish("ExcursionResolved", new { excursionId = id, disposition = cmd.Disposition.ToString() });
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
        }, ct);
    }

    internal static async Task<ExcursionRow> LockAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<ExcursionRow>($"""
            SELECT id AS "Id", version AS "Version", status::text AS "Status", created_by AS "CreatedBy", last_modified_by AS "LastModifiedBy"
            FROM ti.excursion WHERE id = {id} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Excursion");

    internal static async Task<List<StockBucket>> HeldBucketsAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var ids = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT eb.bucket_id AS "Value" FROM ti.excursion_bucket eb JOIN ti.stock_bucket b ON b.tenant_id = eb.tenant_id AND b.id = eb.bucket_id
            WHERE eb.excursion_id = {id} AND b.on_hand_qty > 0 AND b.quality_status = 'Hold'
            """).ToListAsync(ct);
        return ids.Count == 0 ? [] : await QualityService.LoadAsync(s, ids, ct);
    }

    /// <summary>The approval digest: the excursion version plus the held stock it would act on (IAM-003).</summary>
    internal static async Task<byte[]> DigestAsync(ITenantSession s, ExcursionRow row, CancellationToken ct)
    {
        var held = await HeldBucketsAsync(s, row.Id, ct);
        var text = $"{row.Id:N}|{row.Version}|{row.Status}|" + string.Join(';', held.Select(b => $"{b.Id:N}:{b.OnHandQty:0.######}"));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    private static Task<bool> OtherExcursionAsync(ITenantSession s, Guid bucketId, Guid excursionId, CancellationToken ct) =>
        s.Db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM ti.excursion_bucket eb JOIN ti.excursion e ON e.tenant_id = eb.tenant_id AND e.id = eb.excursion_id
                           WHERE eb.bucket_id = {bucketId} AND e.status <> 'Resolved' AND e.id <> {excursionId}) AS "Value"
            """).SingleAsync(ct);

    internal static async Task<ExcursionDto> DtoAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var e = (await s.Db.Database.SqlQuery<ExcursionHead>($"""
            SELECT id AS "Id", version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy", updated_at AS "UpdatedAt", zone_id AS "ZoneId",
                   device_id AS "DeviceId", storage_profile_id AS "ProfileId", status::text AS "Status", is_historical AS "Late", started_at AS "StartedAt",
                   ended_at AS "EndedAt", first_abnormal_c AS "First", last_abnormal_c AS "Last", min_c AS "MinC", max_c AS "MaxC",
                   acknowledged_by AS "AcknowledgedBy", acknowledged_at AS "AcknowledgedAt", disposition::text AS "Disposition", resolved_by AS "ResolvedBy",
                   resolved_at AS "ResolvedAt", disposition_approval_id AS "ApprovalId"
            FROM ti.excursion WHERE id = {id}
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Excursion");
        var buckets = await s.Db.Database.SqlQuery<BucketRow>($"""
            SELECT b.id AS "Id", b.product_id AS "ProductId", b.lot_id AS "LotId" FROM ti.excursion_bucket eb
            JOIN ti.stock_bucket b ON b.tenant_id = eb.tenant_id AND b.id = eb.bucket_id WHERE eb.excursion_id = {id} ORDER BY b.id
            """).ToListAsync(ct);
        var shipments = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT sl.shipment_id AS "Value" FROM ti.excursion_shipment_flag f JOIN ti.shipment_line sl ON sl.tenant_id = f.tenant_id AND sl.id = f.shipment_line_id
            WHERE f.excursion_id = {id}
            """).ToListAsync(ct);
        return new ExcursionDto(e.Id, e.Version, e.CreatedAt, e.CreatedBy, e.UpdatedAt, e.ZoneId, e.DeviceId, e.ProfileId, Enum.Parse<ExcursionStatus>(e.Status),
            e.Late, e.StartedAt, e.EndedAt, e.First, e.Last, e.MinC, e.MaxC, buckets.Select(b => b.Id).ToList(), buckets.Select(b => b.ProductId).Distinct().ToList(),
            buckets.Where(b => b.LotId is not null).Select(b => b.LotId!.Value).Distinct().ToList(), shipments.Order().ToList(), e.AcknowledgedBy, e.AcknowledgedAt,
            e.Disposition is { } d ? Enum.Parse<ExcursionDisposition>(d) : null, e.ResolvedBy, e.ResolvedAt, e.ApprovalId);
    }

    internal sealed record ExcursionRow(Guid Id, int Version, string Status, string CreatedBy, string LastModifiedBy);

    private sealed record KeyRow(Guid Id, DateTimeOffset CreatedAt);

    private sealed record BucketRow(Guid Id, Guid ProductId, Guid? LotId);

    private sealed record ExcursionHead(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ZoneId, Guid DeviceId,
        Guid ProfileId, string Status, bool Late, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, decimal First, decimal Last, decimal MinC, decimal MaxC,
        string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, string? Disposition, string? ResolvedBy, DateTimeOffset? ResolvedAt, Guid? ApprovalId);
}

/// <summary>Tenant alerts (TEL-005): excursions, stale devices, reconciliation and audit alerts, with acknowledgement.</summary>
public sealed class AlertService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors)
{
    public Task<AlertDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        return uow.ReadAsync((s, c) => DtoAsync(s, id, c), ct);
    }

    public Task<Page<AlertDto>> ListAsync(AlertFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        page.EnsureSupportedSort();
        var key = $"alerts|{filter.Type}|{filter.Status}|{filter.Severity}|{filter.WarehouseId}|{filter.ZoneId}";
        var after = page.Cursor is { } cur ? cursors.Decode(cur, key) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<KeyRow>($"""
                SELECT id AS "Id", created_at AS "CreatedAt" FROM ti.alert
                WHERE ({filter.Type?.ToString()}::text IS NULL OR type::text = {filter.Type?.ToString()})
                  AND ({filter.Status?.ToString()}::text IS NULL OR status::text = {filter.Status?.ToString()})
                  AND ({filter.Severity?.ToString()}::text IS NULL OR severity::text = {filter.Severity?.ToString()})
                  AND ({filter.WarehouseId}::uuid IS NULL OR warehouse_id = {filter.WarehouseId}) AND ({filter.ZoneId}::uuid IS NULL OR zone_id = {filter.ZoneId})
                  AND ({after?.CreatedAt}::timestamptz IS NULL OR created_at < {after?.CreatedAt} OR (created_at = {after?.CreatedAt} AND id < {after?.Id}))
                ORDER BY created_at DESC, id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            string? next = null;
            if (rows.Count > page.EffectiveLimit)
            {
                rows.RemoveAt(page.EffectiveLimit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), key);
            }
            var items = new List<AlertDto>();
            foreach (var r in rows)
                items.Add(await DtoAsync(s, r.Id, c));
            return new Page<AlertDto>(items, next);
        }, ct);
    }

    public Task<AlertDto> AcknowledgeAsync(Guid id, int expectedVersion, AcknowledgeRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AlertsAcknowledge);
        return uow.WriteAsync(async (s, c) =>
        {
            var version = (await s.Db.Database.SqlQuery<int>($"SELECT version AS \"Value\" FROM ti.alert WHERE id = {id} FOR UPDATE").ToListAsync(c))
                .Cast<int?>().SingleOrDefault() ?? throw AppException.NotFound("Alert");
            Versioning.EnsureVersion(version, expectedVersion);
            var updated = await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.alert SET status = 'Acknowledged', acknowledged_by = {caller.Subject}, acknowledged_at = {s.Now}, version = version + 1, updated_at = {s.Now}
                WHERE id = {id} AND status = 'Open'
                """, c);
            if (updated == 0)
                throw AppException.InvalidState("Only an open alert can be acknowledged.");
            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("AlertAcknowledged", "Alert", id, dto.Version, cmd.Note, null, new { status = dto.Status.ToString() }));
            return dto;
        }, ct);
    }

    private static async Task<AlertDto> DtoAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var a = (await s.Db.Database.SqlQuery<AlertRow>($"""
            SELECT id AS "Id", version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy", updated_at AS "UpdatedAt", type::text AS "Type",
                   severity::text AS "Severity", status::text AS "Status", warehouse_id AS "WarehouseId", zone_id AS "ZoneId", details::text AS "Details",
                   opened_at AS "OpenedAt", acknowledged_by AS "AcknowledgedBy", acknowledged_at AS "AcknowledgedAt"
            FROM ti.alert WHERE id = {id}
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Alert");
        var d = JsonNode.Parse(a.Details)!.AsObject();
        static IReadOnlyList<Guid> Ids(JsonNode? n) => n is JsonArray arr ? arr.Select(x => Guid.Parse((string)x!)).ToList() : [];
        var type = Enum.Parse<AlertType>(a.Type);
        return new AlertDto(a.Id, a.Version, a.CreatedAt, a.CreatedBy, a.UpdatedAt, type, Enum.Parse<AlertSeverity>(a.Severity), Enum.Parse<AlertStatus>(a.Status),
            a.WarehouseId, a.ZoneId, (string?)d["title"] ?? Titles(type), (string?)d["detail"], (string?)d["subjectType"],
            d["subjectId"] is { } sid ? Guid.Parse((string)sid!) : null, Ids(d["productIds"]), Ids(d["lotIds"]), (decimal?)d["firstAbnormalCelsius"],
            (decimal?)d["lastAbnormalCelsius"], a.OpenedAt, a.AcknowledgedBy, a.AcknowledgedAt);
    }

    private static string Titles(AlertType type) => type switch
    {
        AlertType.ReconciliationMismatch => "Stock projection differs from the movement ledger",
        AlertType.AuditChainFailure => "Audit chain verification failed",
        AlertType.StaleDevice => "Temperature device has stopped reporting",
        AlertType.OutboxDeadLetter => "An integration message could not be delivered",
        _ => type.ToString(),
    };

    private sealed record KeyRow(Guid Id, DateTimeOffset CreatedAt);

    private sealed record AlertRow(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, string Type, string Severity,
        string Status, Guid? WarehouseId, Guid? ZoneId, string Details, DateTimeOffset OpenedAt, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt);
}
