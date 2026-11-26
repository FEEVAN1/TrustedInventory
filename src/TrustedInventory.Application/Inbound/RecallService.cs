using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public enum RecallStatus { Open, Closed }

public enum RecallOutreachStatus { Pending, Contacted, Acknowledged, Completed }

public sealed record CreateRecallRequest(Guid ProductId, IReadOnlyList<Guid>? LotIds, IReadOnlyList<Guid>? SerialIds, string Reason, DateTimeOffset EffectiveAt);

public sealed record RecordOutreachRequest(Guid CustomerId, RecallOutreachStatus Status, string? Note);

public sealed record CloseRecallRequest(Guid ApprovalId, string Reason);

public sealed record RecallOnHand(Guid BucketId, Guid WarehouseId, Guid BinId, Guid? LotId, Guid? SerialId, QualityStatus QualityStatus, Quantity Quantity);

public sealed record RecallInTransit(Guid TransferId, Guid TransferLineId, Quantity Quantity);

public sealed record RecallReservation(Guid ReservationId, Guid OrderId, string State, Quantity Quantity);

public sealed record RecallShipment(Guid ShipmentId, Guid ShipmentLineId, Guid CustomerId, Guid? LotId, Quantity Quantity, DateTimeOffset DispatchedAt);

public sealed record RecallImpact(IReadOnlyList<RecallOnHand> OnHand, IReadOnlyList<RecallInTransit> InTransit, IReadOnlyList<RecallReservation> Reservations,
    IReadOnlyList<RecallShipment> Shipments);

public sealed record RecallOutreachDto(Guid CustomerId, RecallOutreachStatus Status, string? Note, DateTimeOffset RecordedAt, string RecordedBy);

public sealed record RecallDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ProductId,
    IReadOnlyList<Guid> LotIds, IReadOnlyList<Guid> SerialIds, string Reason, DateTimeOffset EffectiveAt, RecallStatus Status, RecallImpact Impact,
    IReadOnlyList<RecallOutreachDto> Outreach, DateTimeOffset? ClosedAt, Guid? ClosureApprovalId) : IVersioned;

public sealed record RecallFilter(Guid? ProductId, RecallStatus? Status);

public sealed class CreateRecallRequestValidator : AbstractValidator<CreateRecallRequest>
{
    public CreateRecallRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.LotIds).Must(l => l is null || (l.Count <= 1000 && l.Distinct().Count() == l.Count)).WithMessage("Give up to 1000 distinct lots.");
        RuleFor(x => x.SerialIds).Must(l => l is null || (l.Count <= 10000 && l.Distinct().Count() == l.Count)).WithMessage("Give up to 10000 distinct serials.");
    }
}

public sealed class RecordOutreachRequestValidator : AbstractValidator<RecordOutreachRequest>
{
    public RecordOutreachRequestValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(2000);
    }
}

/// <summary>
/// Recalls (EXC-006, EXC-007, DEC-076). Opening a recall immediately moves every covered bucket that is Available, in Quarantine or on
/// Hold to Recalled, reservations included (MED-002), so allocation, picking and dispatch stop at once; allocation also excludes any
/// stock an open recall covers. Closure needs every recalled quantity dispositioned, nothing in transit or reserved, and an independent
/// QA approval. The recall and its history are kept.
/// </summary>
public sealed class RecallService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateRecallRequest> createValidator,
    IValidator<RecordOutreachRequest> outreachValidator)
{
    public Task<CommandOutcome> CreateAsync(Guid idempotencyKey, CreateRecallRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.RecallsManage);
        createValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /recalls", payload), async (s, c) =>
        {
            var product = await s.Db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == cmd.ProductId, c)
                ?? throw AppException.Validation("productId", "The product does not exist.");
            var lots = cmd.LotIds ?? [];
            var serials = cmd.SerialIds ?? [];
            if (lots.Count > 0 && await s.Db.Set<Lot>().CountAsync(l => lots.Contains(l.Id) && l.ProductId == product.Id, c) != lots.Count)
                throw AppException.Validation("lotIds", "Every lot must be a lot of this product.");
            if (serials.Count > 0 && await s.Db.Set<Serial>().CountAsync(x => serials.Contains(x.Id) && x.ProductId == product.Id, c) != serials.Count)
                throw AppException.Validation("serialIds", "Every serial must be a serial of this product.");

            var id = Guid.CreateVersion7();
            var allLots = lots.Count == 0 && serials.Count == 0;
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.recall (id, tenant_id, product_id, all_lots, reason, effective_at, last_modified_by, created_by, created_at, updated_at)
                VALUES ({id}, {caller.TenantId}, {product.Id}, {allLots}, {cmd.Reason}, {cmd.EffectiveAt}, {caller.Subject}, {caller.Subject}, {s.Now}, {s.Now})
                """, c);
            foreach (var lot in lots)
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.recall_item (tenant_id, recall_id, product_id, lot_id, created_by, created_at)
                    VALUES ({caller.TenantId}, {id}, {product.Id}, {lot}, {caller.Subject}, {s.Now})
                    """, c);
            foreach (var serial in serials)
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.recall_item (tenant_id, recall_id, product_id, serial_id, created_by, created_at)
                    VALUES ({caller.TenantId}, {id}, {product.Id}, {serial}, {caller.Subject}, {s.Now})
                    """, c);

            // Block at once: covered stock that could still move becomes Recalled with its reservations (EXC-006, MED-002).
            var coveredIds = await s.Db.Database.SqlQuery<Guid>($"""
                SELECT b.id AS "Value" FROM ti.stock_bucket b WHERE b.product_id = {product.Id} AND b.on_hand_qty > 0
                  AND b.quality_status IN ('Available', 'Quarantine', 'Hold') AND ti.recall_covers(b.product_id, b.lot_id, b.serial_id)
                """).ToListAsync(c);
            var covered = coveredIds.Count == 0 ? [] : await QualityService.LoadAsync(s, coveredIds, c);
            var moved = covered.Count == 0
                ? new QualityTransitionResult(id, [], [], [])
                : await QualityService.MoveWholeAsync(s, caller, covered, QualityStatus.Recalled, DocumentType.Recall, id, $"Recall: {cmd.Reason}", c);

            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("RecallOpened", "Recall", id, dto.Version, cmd.Reason, null,
                new { recall = dto, movementIds = moved.MovementIds, affectedReservationIds = moved.AffectedReservationIds }));
            s.Publish("RecallOpened", new { recallId = id, productId = product.Id, lotIds = lots, serialIds = serials, affectedReservations = moved.AffectedReservationIds });
            return new CommandOutcome(201, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
        }, ct);
    }

    public Task<RecallDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        return uow.ReadAsync((s, c) => DtoAsync(s, id, c), ct);
    }

    public Task<Page<RecallDto>> ListAsync(RecallFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        page.EnsureSupportedSort();
        var filterKey = $"recalls|{filter.ProductId}|{filter.Status}";
        var after = page.Cursor is { } cursor ? cursors.Decode(cursor, filterKey) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<RecallKey>($"""
                SELECT id AS "Id", created_at AS "CreatedAt" FROM ti.recall
                WHERE ({filter.ProductId}::uuid IS NULL OR product_id = {filter.ProductId})
                  AND ({filter.Status?.ToString()}::text IS NULL OR status::text = {filter.Status?.ToString()})
                  AND ({after?.CreatedAt}::timestamptz IS NULL OR created_at < {after?.CreatedAt} OR (created_at = {after?.CreatedAt} AND id < {after?.Id}))
                ORDER BY created_at DESC, id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            string? next = null;
            if (rows.Count > page.EffectiveLimit)
            {
                rows.RemoveAt(page.EffectiveLimit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), filterKey);
            }
            var items = new List<RecallDto>();
            foreach (var r in rows)
                items.Add(await DtoAsync(s, r.Id, c));
            return new Page<RecallDto>(items, next);
        }, ct);
    }

    /// <summary>Manual outreach status per customer (EXC-006); automatic external messaging is outside R1.</summary>
    public Task<RecallDto> RecordOutreachAsync(Guid id, int expectedVersion, RecordOutreachRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.RecallsManage);
        outreachValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var recall = await LockAsync(s, id, c);
            Versioning.EnsureVersion(recall.Version, expectedVersion);
            if (recall.Status != nameof(RecallStatus.Open))
                throw AppException.InvalidState("The recall is closed; its outreach history is final.");
            if (!await s.Db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (SELECT 1 FROM ti.partner WHERE id = {cmd.CustomerId} AND type IN ('Customer', 'Both')) AS "Value"
                    """).SingleAsync(c))
                throw AppException.Validation("customerId", "The customer does not exist.");
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.recall_outreach (tenant_id, recall_id, customer_id, status, note, created_by, created_at)
                VALUES ({caller.TenantId}, {id}, {cmd.CustomerId}, {cmd.Status.ToString()}::ti.recall_outreach_status, {cmd.Note}, {caller.Subject}, {s.Now})
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.recall SET version = version + 1, updated_at = {s.Now}, last_modified_by = {caller.Subject} WHERE id = {id}
                """, c);
            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("RecallOutreachRecorded", "Recall", id, dto.Version, cmd.Note, null,
                new { customerId = cmd.CustomerId, status = cmd.Status.ToString() }));
            return dto;
        }, ct);
    }

    /// <summary>Closure (EXC-007): nothing recalled left on hand, in transit or reserved, and an independent QA approval of this version.</summary>
    public Task<CommandOutcome> CloseAsync(Guid idempotencyKey, Guid id, int expectedVersion, CloseRecallRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.RecallsClose);
        if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Length > 2000)
            throw AppException.Validation("reason", "Give a reason of 1 to 2000 characters.");
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /recalls/{id}/close", payload), async (s, c) =>
        {
            var subject = await ApprovalSubjects.RecallAsync(s, id, c);
            Versioning.EnsureVersion(subject.Version, expectedVersion);
            if (!subject.Open)
                throw AppException.InvalidState("The recall is already closed.");
            var outstanding = await OutstandingAsync(s, id, c);
            if (outstanding.Count > 0)
                throw AppException.InvalidState("Recalled stock remains: " + string.Join("; ", outstanding) + ". Disposition it through QA first (EXC-007).");
            var approval = await ApprovalSubjects.RequireBindingAsync(s, subject, cmd.ApprovalId, c);
            DomainGuard.Run(() => approval.Consume($"recall-close:{id:D}", s.Now));
            await s.Db.SaveChangesAsync(c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.recall SET status = 'Closed', closure_approval_id = {approval.Id}, closure_reason = {cmd.Reason.Trim()}, closed_at = {s.Now},
                       closed_by = {caller.Subject}, last_modified_by = {caller.Subject}, version = version + 1, updated_at = {s.Now}
                WHERE id = {id}
                """, c);
            var dto = await DtoAsync(s, id, c);
            s.Audit(new AuditEntry("RecallClosed", "Recall", id, dto.Version, cmd.Reason, null, new { approvalId = approval.Id, approver = approval.ApproverSubject }));
            s.Publish("RecallClosed", new { recallId = id, approvalId = approval.Id });
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(dto, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>What still stops closure: recalled stock on hand, in transit, or reserved.</summary>
    internal static async Task<List<string>> OutstandingAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var impact = await ImpactAsync(s, id, ct);
        var outstanding = new List<string>();
        var onHand = impact.OnHand.Sum(b => b.Quantity.Value);
        if (onHand > 0)
            outstanding.Add($"{Quantity.Format(onHand)} on hand");
        if (impact.InTransit.Count > 0)
            outstanding.Add($"{impact.InTransit.Count} transfer lines in transit");
        if (impact.Reservations.Count > 0)
            outstanding.Add($"{impact.Reservations.Count} reservations");
        return outstanding;
    }

    /// <summary>Covered stock, transit, reservations and shipments (EXC-006). Shipments list customers for outreach.</summary>
    internal static async Task<RecallImpact> ImpactAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var onHand = await s.Db.Database.SqlQuery<OnHandRow>($"""
            SELECT b.id AS "BucketId", b.warehouse_id AS "WarehouseId", b.bin_id AS "BinId", b.lot_id AS "LotId", b.serial_id AS "SerialId",
                   b.quality_status::text AS "Quality", b.on_hand_qty AS "Qty"
            FROM ti.recall r JOIN ti.stock_bucket b ON b.tenant_id = r.tenant_id AND b.product_id = r.product_id
            WHERE r.id = {id} AND b.on_hand_qty > 0
              AND (r.all_lots OR EXISTS (SELECT 1 FROM ti.recall_item i WHERE i.recall_id = r.id
                   AND ((i.lot_id IS NOT NULL AND i.lot_id = b.lot_id) OR (i.serial_id IS NOT NULL AND i.serial_id = b.serial_id))))
            ORDER BY b.warehouse_id, b.bin_id, b.id
            """).ToListAsync(ct);
        var transit = await s.Db.Database.SqlQuery<TransitRow>($"""
            SELECT l.transfer_id AS "TransferId", l.id AS "LineId", l.dispatched_qty - l.received_qty - l.disposed_qty AS "Qty"
            FROM ti.recall r JOIN ti.transfer_line l ON l.tenant_id = r.tenant_id AND l.product_id = r.product_id
            JOIN ti.transfer t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            WHERE r.id = {id} AND t.status IN ('InTransit', 'PartReceived') AND l.dispatched_qty > l.received_qty + l.disposed_qty
              AND (r.all_lots OR EXISTS (SELECT 1 FROM ti.recall_item i WHERE i.recall_id = r.id
                   AND i.lot_id IS NOT NULL AND i.lot_id = l.lot_id))   -- transfer lines carry lots, not serials
            ORDER BY l.transfer_id, l.id
            """).ToListAsync(ct);
        var reservations = await s.Db.Database.SqlQuery<ReservationRow>($"""
            SELECT v.id AS "ReservationId", ol.order_id AS "OrderId", v.state::text AS "State", v.qty AS "Qty"
            FROM ti.recall r JOIN ti.stock_bucket b ON b.tenant_id = r.tenant_id AND b.product_id = r.product_id
            JOIN ti.reservation v ON v.tenant_id = b.tenant_id AND v.bucket_id = b.id AND v.state IN ('Active', 'Picked')
            JOIN ti.outbound_order_line ol ON ol.tenant_id = v.tenant_id AND ol.id = v.order_line_id
            WHERE r.id = {id}
              AND (r.all_lots OR EXISTS (SELECT 1 FROM ti.recall_item i WHERE i.recall_id = r.id
                   AND ((i.lot_id IS NOT NULL AND i.lot_id = b.lot_id) OR (i.serial_id IS NOT NULL AND i.serial_id = b.serial_id))))
            ORDER BY v.id
            """).ToListAsync(ct);
        var shipments = await s.Db.Database.SqlQuery<ShipmentRow>($"""
            SELECT sl.shipment_id AS "ShipmentId", sl.id AS "LineId", o.customer_id AS "CustomerId", sl.lot_id AS "LotId", sl.base_qty AS "Qty",
                   sh.dispatched_at AS "DispatchedAt"
            FROM ti.recall r JOIN ti.shipment_line sl ON sl.tenant_id = r.tenant_id AND sl.product_id = r.product_id
            JOIN ti.shipment sh ON sh.tenant_id = sl.tenant_id AND sh.id = sl.shipment_id
            JOIN ti.outbound_order o ON o.tenant_id = sh.tenant_id AND o.id = sh.order_id
            WHERE r.id = {id}
              AND (r.all_lots OR EXISTS (SELECT 1 FROM ti.recall_item i WHERE i.recall_id = r.id
                   AND ((i.lot_id IS NOT NULL AND i.lot_id = sl.lot_id) OR (i.serial_id IS NOT NULL AND i.serial_id = sl.serial_id))))
            ORDER BY sh.dispatched_at, sl.id
            """).ToListAsync(ct);
        return new RecallImpact(
            onHand.Select(b => new RecallOnHand(b.BucketId, b.WarehouseId, b.BinId, b.LotId, b.SerialId, Enum.Parse<QualityStatus>(b.Quality), Quantity.From(b.Qty))).ToList(),
            transit.Select(t => new RecallInTransit(t.TransferId, t.LineId, Quantity.From(t.Qty))).ToList(),
            reservations.Select(r => new RecallReservation(r.ReservationId, r.OrderId, r.State, Quantity.From(r.Qty))).ToList(),
            shipments.Select(x => new RecallShipment(x.ShipmentId, x.LineId, x.CustomerId, x.LotId, Quantity.From(x.Qty), x.DispatchedAt)).ToList());
    }

    /// <summary>The digest a closure approval binds: the recall version plus everything that still stops closure (IAM-003).</summary>
    internal static byte[] Digest(RecallRow r, IReadOnlyList<string> outstanding) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{r.Id:N}|{r.Version}|{r.Status}|{string.Join(';', outstanding)}"));

    internal static async Task<RecallRow> LockAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<RecallRow>($"""
            SELECT id AS "Id", version AS "Version", status::text AS "Status", created_by AS "CreatedBy", last_modified_by AS "LastModifiedBy"
            FROM ti.recall WHERE id = {id} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Recall");

    private static async Task<RecallDto> DtoAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var r = (await s.Db.Database.SqlQuery<RecallHead>($"""
            SELECT id AS "Id", version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy", updated_at AS "UpdatedAt",
                   product_id AS "ProductId", reason AS "Reason", effective_at AS "EffectiveAt", status::text AS "Status", closed_at AS "ClosedAt",
                   closure_approval_id AS "ClosureApprovalId"
            FROM ti.recall WHERE id = {id}
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Recall");
        var items = await s.Db.Database.SqlQuery<ItemRow>($"""
            SELECT lot_id AS "LotId", serial_id AS "SerialId" FROM ti.recall_item WHERE recall_id = {id} ORDER BY created_at, id
            """).ToListAsync(ct);
        var outreach = await s.Db.Database.SqlQuery<OutreachRow>($"""
            SELECT customer_id AS "CustomerId", status::text AS "Status", note AS "Note", created_at AS "RecordedAt", created_by AS "RecordedBy"
            FROM ti.recall_outreach WHERE recall_id = {id} ORDER BY created_at, id
            """).ToListAsync(ct);
        return new RecallDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.ProductId,
            items.Where(i => i.LotId is not null).Select(i => i.LotId!.Value).ToList(), items.Where(i => i.SerialId is not null).Select(i => i.SerialId!.Value).ToList(),
            r.Reason, r.EffectiveAt, Enum.Parse<RecallStatus>(r.Status), await ImpactAsync(s, id, ct),
            outreach.Select(o => new RecallOutreachDto(o.CustomerId, Enum.Parse<RecallOutreachStatus>(o.Status), o.Note, o.RecordedAt, o.RecordedBy)).ToList(),
            r.ClosedAt, r.ClosureApprovalId);
    }

    internal sealed record RecallRow(Guid Id, int Version, string Status, string CreatedBy, string LastModifiedBy);

    private sealed record RecallKey(Guid Id, DateTimeOffset CreatedAt);

    private sealed record RecallHead(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ProductId, string Reason,
        DateTimeOffset EffectiveAt, string Status, DateTimeOffset? ClosedAt, Guid? ClosureApprovalId);

    private sealed record ItemRow(Guid? LotId, Guid? SerialId);

    private sealed record OutreachRow(Guid CustomerId, string Status, string? Note, DateTimeOffset RecordedAt, string RecordedBy);

    private sealed record OnHandRow(Guid BucketId, Guid WarehouseId, Guid BinId, Guid? LotId, Guid? SerialId, string Quality, decimal Qty);

    private sealed record TransitRow(Guid TransferId, Guid LineId, decimal Qty);

    private sealed record ReservationRow(Guid ReservationId, Guid OrderId, string State, decimal Qty);

    private sealed record ShipmentRow(Guid ShipmentId, Guid LineId, Guid CustomerId, Guid? LotId, decimal Qty, DateTimeOffset DispatchedAt);
}
