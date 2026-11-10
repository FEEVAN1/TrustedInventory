using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record CountLineDto(Guid Id, Guid BinId, Guid ProductId, Guid? LotId, Guid? SerialId, Quantity? ExpectedQuantity, Quantity? ObservedQuantity,
    SignedQuantity? Variance);

public sealed record CountDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid WarehouseId, IReadOnlyList<Guid> BinIds,
    CountStatus Status, int Revision, long? SnapshotSequence, DateTimeOffset? LockedAt, DateTimeOffset? SubmittedAt, Guid? ApprovalId,
    IReadOnlyList<Guid> PostedMovementIds, IReadOnlyList<CountLineDto> Lines) : IVersioned;

public sealed record CreateCount(Guid WarehouseId, IReadOnlyList<Guid> BinIds, string Reason);

public sealed record CountObservation(Guid BinId, Guid ProductId, string? LotNumber, string? SerialNumber, Quantity ObservedQuantity);

public sealed record SubmitCount(IReadOnlyList<CountObservation> Lines);

public sealed record CountFilter(Guid? WarehouseId, CountStatus? Status);

public sealed class CreateCountValidator : AbstractValidator<CreateCount>
{
    public CreateCountValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.BinIds).NotNull().Must(b => b is { Count: >= 1 and <= 500 } && b.Distinct().Count() == b.Count && b.All(id => id != Guid.Empty))
            .WithMessage("Send 1 to 500 distinct bin ids.");
        RuleFor(x => x.Reason).ValidReason();
    }
}

public sealed class SubmitCountValidator : AbstractValidator<SubmitCount>
{
    public SubmitCountValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: <= 10000 }).WithMessage("Send at most 10000 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.BinId).NotEmpty();
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.ObservedQuantity).Must(q => q.IsWellFormed).WithMessage("Use a decimal string of zero or more, e.g. \"12\".");
            line.RuleFor(l => l.LotNumber!).Must(v => v.Length is >= 1 and <= 100 && v == v.Trim()).When(l => l.LotNumber is not null)
                .WithMessage("Use 1 to 100 characters without leading or trailing spaces.");
            line.RuleFor(l => l.SerialNumber!).Must(v => v.Length is >= 1 and <= 100 && v == v.Trim()).When(l => l.SerialNumber is not null)
                .WithMessage("Use 1 to 100 characters without leading or trailing spaces.");
        });
    }
}

/// <summary>Count rules shared with the approval registry (EXC-003, DEC-059).</summary>
internal static class CountRules
{
    /// <summary>Locks the count row and returns the lines of its current revision.</summary>
    public static async Task<(StockCount Count, List<StockCountLine> Lines)> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.stock_count WHERE id = {id} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Count");
        var count = await s.Db.Set<StockCount>().SingleAsync(c => c.Id == id, ct);
        var revision = count.Revision;
        var lines = await s.Db.Set<StockCountLine>().Where(l => l.CountId == id && l.Revision == revision).ToListAsync(ct);
        return (count, lines);
    }

    /// <summary>SHA-256 of the canonical current revision a variance approval binds (IAM-003, DEC-059).</summary>
    public static byte[] Digest(StockCount c, IEnumerable<StockCountLine> lines)
    {
        var body = new JsonObject
        {
            ["id"] = c.Id.ToString(), ["warehouseId"] = c.WarehouseId.ToString(), ["revision"] = c.Revision,
            ["lines"] = new JsonArray(lines
                .OrderBy(l => l.BinId).ThenBy(l => l.ProductId).ThenBy(l => l.LotId).ThenBy(l => l.SerialId)
                .Select(l => (JsonNode)new JsonObject
                {
                    ["binId"] = l.BinId.ToString(), ["productId"] = l.ProductId.ToString(), ["lotId"] = l.LotId?.ToString(), ["serialId"] = l.SerialId?.ToString(),
                    ["expected"] = Quantity.Format(l.ExpectedQty), ["observed"] = l.ObservedQty is { } o ? Quantity.Format(o) : null,
                }).ToArray()),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(body)));
    }

    public static async Task<IReadOnlyList<string>> SignPermissionsAsync(ITenantSession s, IEnumerable<StockCountLine> lines, CancellationToken ct)
    {
        var products = lines.Select(l => l.ProductId).Distinct().ToList();
        var medical = await s.Db.Set<Product>().AsNoTracking().AnyAsync(p => products.Contains(p.Id) && p.HandlingProfile == HandlingProfile.Medical, ct);
        return medical ? [Permissions.ApprovalsSignInventory, Permissions.ApprovalsSignQuality] : [Permissions.ApprovalsSignInventory];
    }
}

/// <summary>
/// Stock counts (EXC-003, AT-EXC-04, DEC-008, DEC-059): blind counting in locked bins, an independently approved
/// variance, and posting once as adjustments. Lock order: count row → count bins → approval → buckets (by id) → audit.
/// </summary>
public sealed class CountService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateCount> createValidator,
    IValidator<SubmitCount> submitValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<CountDto> CreateAsync(CreateCount cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CountsPerform);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.CountsPerform, cmd.WarehouseId);
        return uow.WriteAsync(async (s, c) =>
        {
            var ids = cmd.BinIds.ToList();
            var bins = await s.Db.Set<Bin>().AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync(c);
            if (bins.Count != ids.Count || bins.Any(b => b.WarehouseId != cmd.WarehouseId))
                throw AppException.Validation("binIds", "Every bin must belong to the warehouse.");
            if (bins.Any(b => b.Kind == BinKind.Transit))
                throw AppException.Validation("binIds", "Transit bins are not counted (DEC-010).");
            var count = StockCount.Create(caller.TenantId, cmd.WarehouseId, cmd.Reason, caller.Subject, s.Now);
            s.Db.Add(count);
            foreach (var bin in ids)
                s.Db.Add(StockCountBin.Create(caller.TenantId, count.Id, cmd.WarehouseId, bin, caller.Subject, s.Now));
            await s.Db.SaveChangesAsync(c);
            var dto = await DtoAsync(s, caller, count, c);
            s.Audit(new AuditEntry("CountCreated", "StockCount", count.Id, count.Version, cmd.Reason, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    /// <summary>Locks the bins and snapshots expected on hand by product, lot and serial across qualities (DEC-008).</summary>
    public Task<CountDto> StartAsync(Guid id, int expectedVersion, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, Permissions.CountsPerform, "CountStarted", null, async (s, caller, count, _, c) =>
        {
            var countBins = await s.Db.Set<StockCountBin>().Where(b => b.CountId == count.Id).ToListAsync(c);
            var binIds = countBins.Select(b => b.BinId).ToArray();
            var locked = await s.Db.Set<StockCountBin>().AsNoTracking()
                .AnyAsync(b => binIds.Contains(b.BinId) && b.LockActive && b.CountId != count.Id, c);
            if (locked)
                throw new AppException(ErrorCode.CountLocked, "Another count already locks one of these bins (EXC-003).");
            foreach (var bin in countBins)
                bin.Lock(s.Now);
            var sequence = (await s.Db.Database.SqlQuery<long>($"SELECT last_sequence AS \"Value\" FROM ti.audit_stream WHERE tenant_id = {caller.TenantId}")
                .ToListAsync(c)).SingleOrDefault();
            DomainGuard.Run(() => count.Start(sequence, caller.Subject, s.Now));
            var snapshot = await s.Db.Database.SqlQuery<SnapshotRow>($"""
                SELECT bin_id AS "BinId", product_id AS "ProductId", lot_id AS "LotId", serial_id AS "SerialId", sum(on_hand_qty) AS "OnHand"
                FROM ti.stock_bucket WHERE bin_id = ANY({binIds}) AND on_hand_qty > 0
                GROUP BY bin_id, product_id, lot_id, serial_id
                """).ToListAsync(c);
            foreach (var row in snapshot)
                s.Db.Add(StockCountLine.Create(caller.TenantId, count.Id, count.Revision, row.BinId, row.ProductId, row.LotId, row.SerialId, row.OnHand,
                    caller.Subject, s.Now));
        }, ct);

    /// <summary>Blind observations; unreported lines count as zero (DEC-059).</summary>
    public Task<CountDto> SubmitAsync(Guid id, int expectedVersion, SubmitCount cmd, CancellationToken ct)
    {
        submitValidator.ValidateOrThrow(cmd);
        return ChangeAsync(id, expectedVersion, Permissions.CountsPerform, "CountSubmitted", null, async (s, caller, count, lines, c) =>
        {
            if (count.Status != CountStatus.Counting)
                throw AppException.InvalidState("Only a count in progress can be submitted.");
            var binIds = await s.Db.Set<StockCountBin>().Where(b => b.CountId == count.Id).Select(b => b.BinId).ToListAsync(c);
            var seen = new HashSet<(Guid, Guid, Guid?, Guid?)>();
            for (var i = 0; i < cmd.Lines.Count; i++)
            {
                var input = cmd.Lines[i];
                if (!binIds.Contains(input.BinId))
                    throw AppException.Validation($"lines[{i}].binId", "The bin is not part of this count.");
                var product = await s.Db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == input.ProductId, c)
                    ?? throw AppException.Validation($"lines[{i}].productId", "The product does not exist.");
                var observed = input.ObservedQuantity.Value;
                if (decimal.Round(observed, product.BasePrecision) != observed)
                    throw AppException.Validation($"lines[{i}].observedQuantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
                Guid? lotId = null;
                if (input.LotNumber is { } number)
                    lotId = await s.Db.Set<Lot>().Where(l => l.ProductId == product.Id && l.LotNumber == number).Select(l => (Guid?)l.Id).SingleOrDefaultAsync(c)
                        ?? throw AppException.Validation($"lines[{i}].lotNumber", "Unknown lot; record the find with an adjustment and evidence (DEC-059).");
                Guid? serialId = null;
                if (input.SerialNumber is { } serial)
                    serialId = await s.Db.Set<Serial>().Where(x => x.ProductId == product.Id && x.SerialNumber == serial).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(c)
                        ?? throw AppException.Validation($"lines[{i}].serialNumber", "Unknown serial number for this product.");
                if (!seen.Add((input.BinId, product.Id, lotId, serialId)))
                    throw AppException.Validation($"lines[{i}]", "Report each bin, product, lot and serial once.");
                var line = lines.SingleOrDefault(l => l.BinId == input.BinId && l.ProductId == product.Id && l.LotId == lotId && l.SerialId == serialId);
                if (line is null)
                {
                    line = StockCountLine.Create(caller.TenantId, count.Id, count.Revision, input.BinId, product.Id, lotId, serialId, 0, caller.Subject, s.Now);
                    s.Db.Add(line);
                    lines.Add(line);
                }
                DomainGuard.Run(() => line.Observe(observed, caller.Subject, s.Now));
            }
            foreach (var unreported in lines.Where(l => l.ObservedQty is null))
                DomainGuard.Run(() => unreported.Observe(0, caller.Subject, s.Now));
            DomainGuard.Run(() => count.Submit(caller.Subject, s.Now));
        }, ct);
    }

    /// <summary>Checks an independent signed approval of this exact submission (IAM-003, SOD-1) and records it.</summary>
    public Task<CountDto> ApproveAsync(Guid id, int expectedVersion, ApprovalReference cmd, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, Permissions.CountsManage, "CountApproved", null, async (s, caller, count, lines, c) =>
        {
            if (count.Status != CountStatus.Submitted)
                throw AppException.InvalidState("Only a submitted count can be approved.");
            var approval = await ApprovalSubjects.RequireBindingAsync(s, await ApprovalSubjects.CountAsync(s, (count, lines), c), cmd.ApprovalId, c);
            DomainGuard.Run(() => count.Approve(approval.Id, caller.Subject, s.Now));
        }, ct);

    /// <summary>A new revision; the observations of the rejected one stay as history (AT-EXC-04).</summary>
    public Task<CountDto> RejectAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        reasonValidator.ValidateOrThrow(cmd);
        return ChangeAsync(id, expectedVersion, Permissions.CountsManage, "CountRejected", cmd.Reason, async (s, caller, count, lines, c) =>
        {
            DomainGuard.Run(() => count.Reject(caller.Subject, s.Now));
            await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.CountVariance, count.Id, "The count was rejected for recount.", c);
            foreach (var line in lines)
                s.Db.Add(StockCountLine.Create(caller.TenantId, count.Id, count.Revision, line.BinId, line.ProductId, line.LotId, line.SerialId,
                    line.ExpectedQty, caller.Subject, s.Now));
        }, ct);
    }

    /// <summary>Unlocks without posting; every observation is kept (EXC-003).</summary>
    public Task<CountDto> CancelAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        reasonValidator.ValidateOrThrow(cmd);
        return ChangeAsync(id, expectedVersion, Permissions.CountsManage, "CountCancelled", cmd.Reason, async (s, caller, count, _, c) =>
        {
            DomainGuard.Run(() => count.Cancel(caller.Subject, s.Now));
            foreach (var bin in await s.Db.Set<StockCountBin>().Where(b => b.CountId == count.Id).ToListAsync(c))
                bin.Unlock(s.Now);
            await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.CountVariance, count.Id, "The count was cancelled.", c);
        }, ct);
    }

    /// <summary>Posts the approved variances once as adjustments and unlocks the bins (EXC-003, EXC-004, AT-EXC-04).</summary>
    public Task<CommandOutcome> PostAsync(Guid idempotencyKey, Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CountsManage);
        caller.RequireMutable();
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion }, JsonDefaults.Options);
        var operationKey = $"count-post:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /counts/{id}/post", payload), async (s, c) =>
        {
            var (count, lines) = await CountRules.LockAsync(s, id, c);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(200, JsonNode.Parse(earlier[0]));
            caller.RequireWarehouse(Permissions.CountsManage, count.WarehouseId);
            Versioning.EnsureVersion(count.Version, expectedVersion);
            if (count.Status != CountStatus.Approved || count.ApprovalId is not { } approvalId)
                throw AppException.InvalidState("Only an approved count can be posted.");
            // Approving changed the count's version; the lines cannot change while locked, so the content binding is what matters here.
            var approval = await ApprovalSubjects.RequireBindingAsync(s, await ApprovalSubjects.CountAsync(s, (count, lines), c), approvalId,
                c, checkVersion: false);
            if (await StockBlocks.TenantBlockedAsync(s, c))
                throw new AppException(ErrorCode.StockBlocked, "Stock changes are blocked while an audit-chain failure is under investigation (OPS-001).");

            // 1. Unlock first, so the posting movements pass the count lock inside this same transaction.
            foreach (var bin in await s.Db.Set<StockCountBin>().Where(b => b.CountId == count.Id).ToListAsync(c))
                bin.Unlock(s.Now);
            await s.Db.SaveChangesAsync(c);

            // 2. Each variance becomes adjustments against concrete buckets.
            var movements = new List<Guid>();
            foreach (var line in lines.Where(l => l.Variance is { } v && v != 0).OrderBy(l => l.BinId).ThenBy(l => l.ProductId))
                movements.AddRange(await PostVarianceAsync(s, caller, count, approval.Id, line, c));
            DomainGuard.Run(() => approval.Consume($"count:{count.Id:D}:post", s.Now));
            DomainGuard.Run(() => count.Post(movements.Count > 0, caller.Subject, s.Now));
            await s.Db.SaveChangesAsync(c);

            var result = new PostingResult(movements, count.Status.ToString(), count.Version);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
                VALUES ({caller.TenantId}, {operationKey}, 'CountPost', {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
                """, c);
            s.Audit(new AuditEntry("CountPosted", "StockCount", count.Id, count.Version, null, null, new { result, approvalId = approval.Id }));
            s.Publish("CountPosted", new { countId = count.Id, movementIds = movements });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    public Task<CountDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CountsPerform);
        return uow.ReadAsync(async (s, c) =>
        {
            var count = await s.Db.Set<StockCount>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, c) ?? throw AppException.NotFound("Count");
            caller.RequireWarehouse(Permissions.CountsPerform, count.WarehouseId);
            return await DtoAsync(s, caller, count, c);
        }, ct);
    }

    public Task<Page<CountDto>> ListAsync(CountFilter filter, PageRequest page, CancellationToken ct)
    {
        var caller = callers.Caller;
        var scope = caller.WarehouseFilter(Permissions.CountsPerform);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<StockCount>().AsNoTracking();
            if (scope is not null)
                query = query.Where(x => scope.Contains(x.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(x => x.WarehouseId == w);
            if (filter.Status is { } status)
                query = query.Where(x => x.Status == status);
            var rows = await query.ToPageAsync(page, $"counts|{filter.WarehouseId}|{filter.Status}", cursors, x => x, c);
            var dtos = new List<CountDto>();
            foreach (var count in rows.Items)
                dtos.Add(await DtoAsync(s, caller, count, c));
            return new Page<CountDto>(dtos, rows.NextCursor);
        }, ct);
    }

    /// <summary>
    /// A shortage reduces unreserved stock Available → Hold → Quarantine → others; a surplus lands in Available (Hold for
    /// medical products, DEC-022). One adjustment and one movement per bucket touched (DEC-059).
    /// </summary>
    private static async Task<List<Guid>> PostVarianceAsync(ITenantSession s, CallerContext caller, StockCount count, Guid approvalId, StockCountLine line,
        CancellationToken ct)
    {
        var variance = line.Variance!.Value;
        var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == line.ProductId, ct);
        var targets = new List<(Guid Bucket, QualityStatus Quality, decimal Delta)>();
        if (variance > 0)
        {
            var quality = product.HandlingProfile == HandlingProfile.Medical ? QualityStatus.Hold : QualityStatus.Available;
            var bucket = await StockLocations.MatchingBucketAsync(s, caller.TenantId, caller.Subject, count.WarehouseId, line.BinId, line.ProductId, line.LotId,
                line.SerialId, quality.ToString(), s.Now, ct);
            targets.Add((bucket, quality, variance));
        }
        else
        {
            var buckets = await s.Db.Set<StockBucket>().AsNoTracking()
                .Where(b => b.BinId == line.BinId && b.ProductId == line.ProductId && b.LotId == line.LotId && b.SerialId == line.SerialId && b.OnHandQty > 0)
                .ToListAsync(ct);
            var left = -variance;
            foreach (var bucket in buckets.OrderBy(b => b.QualityStatus switch
                     {
                         QualityStatus.Available => 0, QualityStatus.Hold => 1, QualityStatus.Quarantine => 2, _ => 3,
                     }).ThenBy(b => b.Id))
            {
                var take = Math.Min(left, bucket.OnHandQty - bucket.ReservedQty);
                if (take <= 0)
                    continue;
                targets.Add((bucket.Id, bucket.QualityStatus, -take));
                left -= take;
                if (left == 0)
                    break;
            }
            if (left > 0)
                throw new AppException(ErrorCode.InsufficientStock,
                    $"The shortage in bin {line.BinId} exceeds the unreserved stock; release its reservations first (EXC-004).");
        }

        var locked = await StockLocations.LockBucketsAsync(s, targets.Select(t => t.Bucket), ct);
        foreach (var (bucket, _, delta) in targets.Where(t => t.Delta < 0))
            if (locked[bucket].OnHand - locked[bucket].Reserved < -delta)
                throw new AppException(ErrorCode.InsufficientStock, "Stock was reserved while the count was being approved; release it first (EXC-004).");
        await StockBlocks.EnsureNotBlockedAsync(s, locked.Keys, ct);

        var movements = new List<Guid>();
        foreach (var (bucket, quality, delta) in targets)
        {
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {delta}, updated_at = {s.Now}, version = version + 1 WHERE id = {bucket}
                """, ct);
            var narrative = $"Count variance (count {count.Id:D}, revision {count.Revision}): {count.Reason}";
            var adjustment = Adjustment.Request(caller.TenantId, count.WarehouseId, bucket, line.BinId, line.ProductId, line.LotId, line.SerialId, quality, delta,
                "COUNT-VARIANCE", narrative.Length > 2000 ? narrative[..2000] : narrative, count.Id, caller.Subject, s.Now);
            var movement = StockMovement.Adjustment(caller.TenantId, $"count:{count.Id:D}:{adjustment.Id:D}", line.ProductId, line.LotId, line.SerialId, bucket,
                delta, adjustment.Id, adjustment.Narrative, caller.Subject, s.Now, caller.CorrelationId);
            DomainGuard.Run(() => adjustment.Post(approvalId, bucket, movement.Id, s.Now));
            s.Db.Add(movement);
            s.Db.Add(adjustment);
            movements.Add(movement.Id);
        }
        return movements;
    }

    private Task<CountDto> ChangeAsync(Guid id, int expectedVersion, string permission, string eventType, string? reason,
        Func<ITenantSession, CallerContext, StockCount, List<StockCountLine>, CancellationToken, Task> change, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(permission);
        caller.RequireMutable();
        return uow.WriteAsync(async (s, c) =>
        {
            var (count, lines) = await CountRules.LockAsync(s, id, c);
            caller.RequireWarehouse(permission, count.WarehouseId);
            Versioning.EnsureVersion(count.Version, expectedVersion);
            await change(s, caller, count, lines, c);
            await s.Db.SaveChangesAsync(c);
            var dto = await DtoAsync(s, caller, count, c);
            s.Audit(new AuditEntry(eventType, "StockCount", count.Id, count.Version, reason, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    /// <summary>Blind count: expected and variance are hidden from counters until the count is submitted (EXC-003).</summary>
    private static async Task<CountDto> DtoAsync(ITenantSession s, CallerContext caller, StockCount count, CancellationToken ct)
    {
        var revision = count.Revision;
        var lines = await s.Db.Set<StockCountLine>().AsNoTracking().Where(l => l.CountId == count.Id && l.Revision == revision)
            .OrderBy(l => l.BinId).ThenBy(l => l.ProductId).ThenBy(l => l.CreatedAt).ToListAsync(ct);
        var binIds = await s.Db.Set<StockCountBin>().AsNoTracking().Where(b => b.CountId == count.Id).OrderBy(b => b.BinId).Select(b => b.BinId).ToListAsync(ct);
        var adjustments = s.Db.Set<Adjustment>().AsNoTracking().Where(a => a.CountId == count.Id && a.PostedMovementId != null);
        var movements = await adjustments.Select(a => a.PostedMovementId!.Value).ToListAsync(ct);
        var blind = !caller.CanAccess(Permissions.CountsManage, count.WarehouseId) && count.Status is CountStatus.Draft or CountStatus.Counting;
        return new CountDto(count.Id, count.Version, count.CreatedAt, count.CreatedBy, count.UpdatedAt, count.WarehouseId, binIds, count.Status, count.Revision,
            count.SnapshotSequence, count.LockedAt, count.SubmittedAt, count.ApprovalId, movements,
            lines.Select(l => new CountLineDto(l.Id, l.BinId, l.ProductId, l.LotId, l.SerialId,
                blind ? null : Quantity.From(l.ExpectedQty),
                l.ObservedQty is { } o ? Quantity.From(o) : null,
                blind || l.Variance is not { } v || v == 0 ? null : SignedQuantity.From(v))).ToList());
    }

    private sealed record SnapshotRow(Guid BinId, Guid ProductId, Guid? LotId, Guid? SerialId, decimal OnHand);
}
