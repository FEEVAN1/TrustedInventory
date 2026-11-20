using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record QualityHoldRequest(IReadOnlyList<Guid> BucketIds, string Reason);

public sealed record QualityReleaseRequest(Guid BucketId, Guid ApprovalId);

public sealed record QualityDispositionRequest(Guid BucketId, Quantity Quantity, QualityDispositionType Disposition, string Reason, Guid? ApprovalId);

public sealed record BucketQualityDto(Guid BucketId, QualityStatus QualityStatus, int Version);

public sealed record QualityTransitionResult(Guid EventId, IReadOnlyList<Guid> MovementIds, IReadOnlyList<BucketQualityDto> Buckets,
    IReadOnlyList<Guid> AffectedReservationIds);

public sealed class QualityHoldRequestValidator : AbstractValidator<QualityHoldRequest>
{
    public QualityHoldRequestValidator()
    {
        RuleFor(x => x.BucketIds).NotEmpty().Must(ids => ids.Count <= 500 && ids.Distinct().Count() == ids.Count)
            .WithMessage("Give 1 to 500 distinct bucket ids.");
        RuleFor(x => x.Reason).ValidReason();
    }
}

public sealed class QualityDispositionRequestValidator : AbstractValidator<QualityDispositionRequest>
{
    public QualityDispositionRequestValidator()
    {
        RuleFor(x => x.BucketId).NotEmpty();
        RuleFor(x => x.Reason).ValidReason();
    }
}

/// <summary>
/// Quality hold, release and disposition (section 13, MED-001 to MED-003, EXC-007, DEC-075). One transaction per command with the
/// movements, audit, idempotency record and outbox (ARC-003). Lock order: approval → reservations → buckets (id order) → audit.
/// </summary>
public sealed class QualityService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    IValidator<QualityHoldRequest> holdValidator,
    IValidator<QualityDispositionRequest> dispositionValidator)
{
    public Task<CommandOutcome> HoldAsync(Guid idempotencyKey, QualityHoldRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.QualityHold);
        holdValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /quality/hold", payload), async (s, c) =>
        {
            var eventId = Guid.CreateVersion7();
            var sources = await LoadAsync(s, cmd.BucketIds, c);
            foreach (var b in sources)
            {
                caller.RequireWarehouse(Permissions.QualityHold, b.WarehouseId);
                if (!QualityRules.Holdable(b.QualityStatus))
                    throw AppException.Validation("bucketIds", $"Bucket {b.Id} is {b.QualityStatus}; only Available or Quarantine stock is put on hold.");
            }
            var result = await MoveWholeAsync(s, caller, sources, QualityStatus.Hold, DocumentType.QualityDecision, eventId, cmd.Reason, c);
            s.Audit(new AuditEntry("QualityHoldPlaced", "QualityDecision", eventId, null, cmd.Reason, null, result));
            s.Publish("QualityHoldPlaced", new { eventId, buckets = result.Buckets.Select(b => b.BucketId), reservations = result.AffectedReservationIds });
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    public Task<CommandOutcome> ReleaseAsync(Guid idempotencyKey, QualityReleaseRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.QualityRelease);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /quality/release", payload), async (s, c) =>
        {
            var bucket = (await LoadAsync(s, [cmd.BucketId], c)).Single();
            caller.RequireWarehouse(Permissions.QualityRelease, bucket.WarehouseId);
            var subjectType = QualityRules.ReleaseSubject(bucket.QualityStatus);
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == bucket.ProductId, c);
            var problems = QualityRules.ReleaseProblems(await FactsAsync(s, bucket, product, c));
            if (problems.Count > 0)
                throw Ineligible(problems);
            var subject = await ApprovalSubjects.BucketAsync(s, subjectType!.Value, bucket.Id, c);
            var approval = await ApprovalSubjects.RequireBindingAsync(s, subject, cmd.ApprovalId, c);
            if (bucket.ReservedQty > 0)
                throw AppException.InvalidState("Reservations still point at this bucket; deallocate or unpick them before release.");

            var eventId = Guid.CreateVersion7();
            var result = await MoveWholeAsync(s, caller, [bucket], QualityStatus.Available, DocumentType.QualityDecision, eventId,
                $"{subjectType} with approval {approval.Id:D}", c);
            DomainGuard.Run(() => approval.Consume($"quality-release:{eventId:D}", s.Now));
            s.Audit(new AuditEntry("QualityReleased", "QualityDecision", eventId, null, approval.SignReason ?? approval.RequestReason, null,
                new { result, approvalId = approval.Id, approver = approval.ApproverSubject, from = bucket.QualityStatus.ToString() }));
            s.Publish("QualityReleased", new { eventId, bucketId = bucket.Id, approvalId = approval.Id });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    public Task<CommandOutcome> DispositionAsync(Guid idempotencyKey, QualityDispositionRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.QualityHold);
        dispositionValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /quality/disposition", payload), async (s, c) =>
        {
            var bucket = (await LoadAsync(s, [cmd.BucketId], c)).Single();
            caller.RequireWarehouse(Permissions.QualityHold, bucket.WarehouseId);
            if (!QualityRules.Dispositionable(bucket.QualityStatus) || (bucket.QualityStatus == QualityStatus.Damaged && cmd.Disposition == QualityDispositionType.Damaged))
                throw AppException.InvalidState($"{bucket.QualityStatus} stock cannot be dispositioned as {cmd.Disposition}; hold available stock first.");
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == bucket.ProductId, c);
            var quantity = cmd.Quantity.Value;
            if (decimal.Round(quantity, product.BasePrecision) != quantity)
                throw AppException.Validation("quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (quantity > bucket.OnHandQty - bucket.ReservedQty)
                throw new AppException(ErrorCode.InsufficientStock,
                    $"Only {Quantity.Format(bucket.OnHandQty - bucket.ReservedQty)} unreserved is in this bucket; release reservations first.");

            Approval? approval = null;
            if (QualityRules.DispositionNeedsApproval(product.HandlingProfile, bucket.QualityStatus))
            {
                if (cmd.ApprovalId is not { } approvalId)
                    throw AppException.Validation("approvalId", "Medical and recalled stock need a signed QA approval for any disposition (EXC-007).");
                approval = await ApprovalSubjects.RequireBindingAsync(s, await ApprovalSubjects.BucketAsync(s, ApprovalSubjectType.QualityDisposition, bucket.Id, c),
                    approvalId, c);
            }

            var eventId = Guid.CreateVersion7();
            Guid? target = cmd.Disposition == QualityDispositionType.Damaged
                ? await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, bucket.WarehouseId, bucket.BinId, bucket.ProductId,
                    bucket.LotId, bucket.SerialId, nameof(QualityStatus.Damaged), bucket.ReceivedAt, c)
                : null;
            var locked = await StockLocations.LockBucketsAsync(s, target is { } t ? [bucket.Id, t] : [bucket.Id], c);
            if (locked[bucket.Id].OnHand - locked[bucket.Id].Reserved < quantity)
                throw new AppException(ErrorCode.InsufficientStock, "The bucket changed; retry with the current quantity.");
            await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, c);
            await StockLocations.MoveOnHandAsync(s, bucket.Id, target, quantity, c);
            var type = cmd.Disposition switch
            {
                QualityDispositionType.Disposal => MovementType.Disposal,
                QualityDispositionType.SupplierReturn => MovementType.SupplierReturn,
                _ => MovementType.QualityChange,
            };
            var movement = StockMovement.Quality(type, DocumentType.QualityDecision, caller.TenantId, $"quality:{eventId:D}:{bucket.Id:D}", bucket.ProductId,
                bucket.LotId, bucket.SerialId, bucket.Id, target, quantity, eventId, $"{cmd.Disposition}: {cmd.Reason}", caller.Subject, s.Now,
                caller.CorrelationId);
            s.Db.Add(movement);
            if (approval is not null)
                DomainGuard.Run(() => approval.Consume($"quality-disposition:{eventId:D}", s.Now));
            await s.Db.SaveChangesAsync(c);

            var buckets = new List<BucketQualityDto> { await StateAsync(s, bucket.Id, c) };
            if (target is { } tb)
                buckets.Add(await StateAsync(s, tb, c));
            var result = new QualityTransitionResult(eventId, [movement.Id], buckets, []);
            s.Audit(new AuditEntry("QualityDisposition", "QualityDecision", eventId, null, cmd.Reason, null,
                new { result, disposition = cmd.Disposition.ToString(), quantity = Quantity.Format(quantity), approvalId = approval?.Id }));
            s.Publish("QualityDisposition", new { eventId, bucketId = bucket.Id, disposition = cmd.Disposition.ToString(), movementId = movement.Id });
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    /// <summary>
    /// Moves each bucket whole (on hand and reservations) to the bucket of <paramref name="to"/> quality in the same bin, one
    /// QualityChange movement each (MED-002). Reservations follow the stock, so held stock cannot be picked or dispatched.
    /// </summary>
    internal static async Task<QualityTransitionResult> MoveWholeAsync(ITenantSession s, CallerContext caller, IReadOnlyList<StockBucket> sources,
        QualityStatus to, DocumentType document, Guid documentId, string reason, CancellationToken ct)
    {
        var targets = new Dictionary<Guid, Guid>();
        foreach (var b in sources)
            targets[b.Id] = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, b.WarehouseId, b.BinId, b.ProductId, b.LotId,
                b.SerialId, to.ToString(), b.ReceivedAt, ct);
        var sourceIds = sources.Select(b => b.Id).ToArray();
        // Lock order (STK-005): reservations, then buckets in id order.
        var reservations = await s.Db.Database.SqlQuery<ReservationRow>($"""
            SELECT id AS "Id", bucket_id AS "BucketId", qty AS "Qty" FROM ti.reservation
            WHERE bucket_id = ANY({sourceIds}) AND state IN ('Active', 'Picked') ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var locked = await StockLocations.LockBucketsAsync(s, sourceIds.Concat(targets.Values), ct);
        await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, ct);

        var movementIds = new List<Guid>();
        var affected = new List<Guid>();
        foreach (var b in sources)
        {
            var (onHand, reserved) = (locked[b.Id].OnHand, locked[b.Id].Reserved);
            if (onHand == 0)
                continue;
            var target = targets[b.Id];
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - {onHand}, reserved_qty = reserved_qty - {reserved}, updated_at = {s.Now},
                       version = version + 1 WHERE id = {b.Id}
                """, ct);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {onHand}, reserved_qty = reserved_qty + {reserved}, updated_at = {s.Now},
                       version = version + 1 WHERE id = {target}
                """, ct);
            var moved = reservations.Where(r => r.BucketId == b.Id).Select(r => r.Id).ToArray();
            if (moved.Length > 0)
            {
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.reservation SET bucket_id = {target}, updated_at = {s.Now}, version = version + 1 WHERE id = ANY({moved})
                    """, ct);
                affected.AddRange(moved);
            }
            var movement = StockMovement.Quality(MovementType.QualityChange, document, caller.TenantId, $"quality:{documentId:D}:{b.Id:D}", b.ProductId,
                b.LotId, b.SerialId, b.Id, target, onHand, documentId, reason, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            movementIds.Add(movement.Id);
        }
        await s.Db.SaveChangesAsync(ct);
        var buckets = new List<BucketQualityDto>();
        foreach (var id in targets.Values.Distinct())
            buckets.Add(await StateAsync(s, id, ct));
        return new QualityTransitionResult(documentId, movementIds, buckets, affected);
    }

    internal static async Task<List<StockBucket>> LoadAsync(ITenantSession s, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var buckets = await s.Db.Set<StockBucket>().AsNoTracking().Where(b => ids.Contains(b.Id)).OrderBy(b => b.Id).ToListAsync(ct);
        if (buckets.Count != ids.Distinct().Count())
            throw AppException.NotFound("Stock bucket");
        return buckets;
    }

    /// <summary>MED-001 facts, read in the command transaction.</summary>
    internal static async Task<ReleaseFacts> FactsAsync(ITenantSession s, StockBucket b, Product product, CancellationToken ct)
    {
        var lot = b.LotId is { } lotId ? await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == lotId, ct) : null;
        var failed = (await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.receipt_line
            WHERE product_id = {b.ProductId} AND inspection_result <> 'Passed'
              AND (bucket_id = {b.Id} OR ({b.LotId}::uuid IS NOT NULL AND lot_id = {b.LotId}))
            """).ToListAsync(ct)).Single();
        return new ReleaseFacts(b.QualityStatus, product.HandlingProfile, product.TrackingMode, b.LotId is not null, lot?.ExpiryDate, b.SerialId is not null,
            await WarehouseTodayAsync(s, b.WarehouseId, ct), failed == 0, await StockLocations.IsCompatibleAsync(s, b.BinId, product, ct),
            await RecallChecks.CoversAsync(s, b.ProductId, b.LotId, b.SerialId, ct), await ExcursionChecks.CoversAsync(s, b.Id, ct));
    }

    internal static async Task<DateOnly> WarehouseTodayAsync(ITenantSession s, Guid warehouseId, CancellationToken ct)
    {
        var timeZone = await s.Db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == warehouseId).Select(w => w.TimeZone).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);
    }

    private static async Task<BucketQualityDto> StateAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<BucketQualityRow>($"""
            SELECT id AS "BucketId", quality_status::text AS "Quality", version AS "Version" FROM ti.stock_bucket WHERE id = {id}
            """).ToListAsync(ct)).Select(r => new BucketQualityDto(r.BucketId, Enum.Parse<QualityStatus>(r.Quality), r.Version)).Single();

    internal static AppException Ineligible(IReadOnlyList<(ReleaseIneligibility Code, string Detail)> problems) =>
        new(ErrorCode.ReleaseIneligible, "This stock cannot be released; no signature can override these checks (MED-001).")
        {
            Extensions = new JsonObject
            {
                ["ineligibility"] = new JsonArray(problems.Select(p => (JsonNode)new JsonObject
                {
                    ["code"] = p.Code.ToString(),
                    ["detail"] = p.Detail,
                }).ToArray()),
            },
        };

    private sealed record ReservationRow(Guid Id, Guid BucketId, decimal Qty);

    private sealed record BucketQualityRow(Guid BucketId, string Quality, int Version);
}

/// <summary>Open recalls covering stock (EXC-006, ti.recall_covers in 0026).</summary>
internal static class RecallChecks
{
    public static async Task<bool> CoversAsync(ITenantSession s, Guid productId, Guid? lotId, Guid? serialId, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<bool>($"SELECT ti.recall_covers({productId}, {lotId}, {serialId}) AS \"Value\"").ToListAsync(ct)).Single();
}

/// <summary>Unresolved temperature excursions holding a bucket (TEL-003, DEC-077).</summary>
internal static class ExcursionChecks
{
    public static async Task<bool> CoversAsync(ITenantSession s, Guid bucketId, CancellationToken ct) =>
        await s.Db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM ti.excursion_bucket eb JOIN ti.excursion e ON e.tenant_id = eb.tenant_id AND e.id = eb.excursion_id
                           WHERE eb.bucket_id = {bucketId} AND e.status <> 'Resolved') AS "Value"
            """).SingleAsync(ct);
}
