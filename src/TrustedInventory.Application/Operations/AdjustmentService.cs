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
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Operations;

public sealed record AdjustmentDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid WarehouseId, Guid? BucketId, Guid BinId,
    Guid ProductId, Guid? LotId, Guid? SerialId, QualityStatus QualityStatus, SignedQuantity Delta, string ReasonCode, string Narrative,
    IReadOnlyList<Guid> EvidenceAttachmentIds, AdjustmentStatus Status, Guid? ApprovalId, Guid? PostedMovementId, string ContentDigest) : IVersioned;

public sealed record CreateAdjustment(Guid? BucketId, Guid BinId, Guid ProductId, Guid? LotId, string? SerialNumber, QualityStatus? QualityStatus,
    SignedQuantity Delta, string ReasonCode, string Narrative, IReadOnlyList<Guid>? EvidenceAttachmentIds);

public sealed record UpdateAdjustment(SignedQuantity? Delta, string? ReasonCode, string? Narrative, IReadOnlyList<Guid>? EvidenceAttachmentIds);

public sealed record ApprovalReference(Guid ApprovalId);

public sealed record PostingResult(IReadOnlyList<Guid> MovementIds, string Status, int Version);

public sealed record AdjustmentFilter(Guid? WarehouseId, AdjustmentStatus? Status);

public sealed class CreateAdjustmentValidator : AbstractValidator<CreateAdjustment>
{
    public CreateAdjustmentValidator()
    {
        RuleFor(x => x.BinId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Delta).Must(d => d.IsValid).WithMessage(SignedQuantity.Message);
        RuleFor(x => x.ReasonCode).ValidCode();
        RuleFor(x => x.Narrative).ValidReason();
        RuleFor(x => x.SerialNumber!).Must(v => v.Length is >= 1 and <= 100 && v == v.Trim())
            .WithMessage("Use 1 to 100 characters without leading or trailing spaces.").When(x => x.SerialNumber is not null);
        RuleFor(x => x.EvidenceAttachmentIds!).Must(AdjustmentRules.ValidEvidence).WithMessage(AdjustmentRules.EvidenceMessage)
            .When(x => x.EvidenceAttachmentIds is not null);
    }
}

public sealed class UpdateAdjustmentValidator : AbstractValidator<UpdateAdjustment>
{
    public UpdateAdjustmentValidator()
    {
        RuleFor(x => x.Delta!.Value).Must(d => d.IsValid).WithMessage(SignedQuantity.Message).OverridePropertyName("delta").When(x => x.Delta is not null);
        RuleFor(x => x.ReasonCode!).ValidCode().When(x => x.ReasonCode is not null);
        RuleFor(x => x.Narrative!).ValidReason().When(x => x.Narrative is not null);
        RuleFor(x => x.EvidenceAttachmentIds!).Must(AdjustmentRules.ValidEvidence).WithMessage(AdjustmentRules.EvidenceMessage)
            .When(x => x.EvidenceAttachmentIds is not null);
        RuleFor(x => x).Must(x => x.Delta is not null || x.ReasonCode is not null || x.Narrative is not null || x.EvidenceAttachmentIds is not null)
            .WithMessage("Change at least one field.").OverridePropertyName("delta");
    }
}

/// <summary>Shared adjustment rules for the adjustment and approval services (EXC-004, IAM-003, DEC-057).</summary>
internal static class AdjustmentRules
{
    public const string EvidenceMessage = "Send at most 20 distinct attachment ids.";

    public static bool ValidEvidence(IReadOnlyList<Guid> ids) => ids.Count <= 20 && ids.Distinct().Count() == ids.Count && ids.All(i => i != Guid.Empty);

    public static Task<List<Guid>> EvidenceAsync(ITenantSession s, Guid adjustmentId, CancellationToken ct) =>
        s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.attachment WHERE entity_type = 'Adjustment' AND entity_id = {adjustmentId} ORDER BY id
            """).ToListAsync(ct);

    /// <summary>SHA-256 of the canonical (RFC 8785) adjustment content an approval binds (IAM-003).</summary>
    public static async Task<byte[]> DigestAsync(ITenantSession s, Adjustment a, CancellationToken ct)
    {
        var evidence = await EvidenceAsync(s, a.Id, ct);
        var body = new JsonObject
        {
            ["id"] = a.Id.ToString(), ["warehouseId"] = a.WarehouseId.ToString(), ["bucketId"] = a.BucketId?.ToString(), ["binId"] = a.BinId.ToString(),
            ["productId"] = a.ProductId.ToString(), ["lotId"] = a.LotId?.ToString(), ["serialId"] = a.SerialId?.ToString(),
            ["qualityStatus"] = a.QualityStatus.ToString(), ["delta"] = Quantity.Format(a.DeltaQty), ["reasonCode"] = a.ReasonCode,
            ["narrative"] = a.Narrative,
            ["evidenceAttachmentIds"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e.ToString())!).ToArray()),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(body)));
    }

    /// <summary>The permissions a signer needs: inventory, plus quality for medical stock (SOD-2).</summary>
    public static async Task<IReadOnlyList<string>> SignPermissionsAsync(ITenantSession s, Adjustment a, CancellationToken ct)
    {
        var profile = await s.Db.Set<Product>().AsNoTracking().Where(p => p.Id == a.ProductId).Select(p => p.HandlingProfile).SingleAsync(ct);
        return profile == HandlingProfile.Medical
            ? [Permissions.ApprovalsSignInventory, Permissions.ApprovalsSignQuality]
            : [Permissions.ApprovalsSignInventory];
    }

    /// <summary>A reduction never takes on hand below what is reserved (EXC-004).</summary>
    public static async Task EnsureReducibleAsync(ITenantSession s, Guid bucketId, decimal delta, bool forUpdate, CancellationToken ct)
    {
        if (delta >= 0)
            return;
        var rows = forUpdate
            ? await s.Db.Database.SqlQuery<BucketQty>($"SELECT on_hand_qty AS \"OnHand\", reserved_qty AS \"Reserved\" FROM ti.stock_bucket WHERE id = {bucketId} FOR UPDATE").ToListAsync(ct)
            : await s.Db.Database.SqlQuery<BucketQty>($"SELECT on_hand_qty AS \"OnHand\", reserved_qty AS \"Reserved\" FROM ti.stock_bucket WHERE id = {bucketId}").ToListAsync(ct);
        var bucket = rows.Single();
        if (bucket.OnHand - bucket.Reserved < -delta)
            throw new AppException(ErrorCode.InsufficientStock,
                $"Only {Quantity.Format(bucket.OnHand - bucket.Reserved)} is unreserved here; release reservations before reducing stock (EXC-004).");
    }

    public static async Task<Adjustment> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.adjustment WHERE id = {id} FOR UPDATE").ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Adjustment");
        return await s.Db.Set<Adjustment>().SingleAsync(a => a.Id == id, ct);
    }

    internal sealed record BucketQty(decimal OnHand, decimal Reserved);
}

/// <summary>
/// Adjustments (EXC-004, IAM-003, DEC-057): request, edit, cancel and post once against an independent approval of the
/// exact version. Lock order: adjustment row → approval → bucket → audit stream.
/// </summary>
public sealed class AdjustmentService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateAdjustment> createValidator,
    IValidator<UpdateAdjustment> updateValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<AdjustmentDto> CreateAsync(CreateAdjustment cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AdjustmentsRequest);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var bin = await s.Db.Set<Bin>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == cmd.BinId, c)
                ?? throw AppException.Validation("binId", "The bin does not exist.");
            if (bin.Kind == BinKind.Transit)
                throw AppException.Validation("binId", "Transit stock changes only through transfers (DEC-010).");
            caller.RequireWarehouse(Permissions.AdjustmentsRequest, bin.WarehouseId);
            var product = await s.Db.Set<Product>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == cmd.ProductId, c)
                ?? throw AppException.Validation("productId", "The product does not exist.");
            var delta = cmd.Delta.Value;
            if (decimal.Round(delta, product.BasePrecision) != delta)
                throw AppException.Validation("delta", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (product.IsSerialTracked && Math.Abs(delta) != 1)
                throw AppException.Validation("delta", "A serial-tracked adjustment changes exactly one unit.");

            Guid? bucketId, lotId, serialId;
            QualityStatus quality;
            if (cmd.BucketId is { } given)
            {
                var bucket = await s.Db.Set<StockBucket>().AsNoTracking().SingleOrDefaultAsync(b => b.Id == given, c);
                if (bucket is null || bucket.BinId != bin.Id || bucket.ProductId != product.Id)
                    throw AppException.Validation("bucketId", "The bucket is not this product in this bin.");
                if ((cmd.LotId is not null && cmd.LotId != bucket.LotId) || (cmd.QualityStatus is { } q && q != bucket.QualityStatus))
                    throw AppException.Validation("bucketId", "The lot and quality status come from the bucket.");
                (bucketId, lotId, serialId, quality) = (bucket.Id, bucket.LotId, bucket.SerialId, bucket.QualityStatus);
            }
            else
            {
                lotId = cmd.LotId;
                if (lotId is { } lid && !await s.Db.Set<Lot>().AnyAsync(l => l.Id == lid && l.ProductId == product.Id, c))
                    throw AppException.Validation("lotId", "The lot is not a lot of this product.");
                if (product.TrackingMode is TrackingMode.Lot or TrackingMode.LotAndSerial && lotId is null)
                    throw AppException.Validation("lotId", "This product is lot-tracked; name the lot.");
                serialId = null;
                if (cmd.SerialNumber is { } number)
                    serialId = await s.Db.Set<Serial>().Where(x => x.ProductId == product.Id && x.SerialNumber == number).Select(x => (Guid?)x.Id)
                        .SingleOrDefaultAsync(c) ?? throw AppException.Validation("serialNumber", "No such serial number for this product.");
                if (product.IsSerialTracked && serialId is null)
                    throw AppException.Validation("serialNumber", "This product is serial-tracked; name the serial number.");
                quality = cmd.QualityStatus
                    ?? (product.HandlingProfile == HandlingProfile.Medical && delta > 0 ? QualityStatus.Hold   // DEC-022
                        : throw AppException.Validation("qualityStatus", "Name the quality status when no bucket is given."));
                var q = quality;
                bucketId = await s.Db.Set<StockBucket>().AsNoTracking()
                    .Where(b => b.BinId == bin.Id && b.ProductId == product.Id && b.LotId == lotId && b.SerialId == serialId && b.QualityStatus == q)
                    .Select(b => (Guid?)b.Id).SingleOrDefaultAsync(c);
            }
            if (delta < 0 && bucketId is null)
                throw AppException.Validation("bucketId", "There is no stock here to reduce.");
            if (bucketId is { } target)
                await AdjustmentRules.EnsureReducibleAsync(s, target, delta, forUpdate: false, c);

            var adjustment = DomainGuard.Run(() => Adjustment.Request(caller.TenantId, bin.WarehouseId, bucketId, bin.Id, product.Id, lotId, serialId,
                quality, delta, cmd.ReasonCode, cmd.Narrative, null, caller.Subject, s.Now));
            s.Db.Add(adjustment);
            await s.Db.SaveChangesAsync(c);
            await BindEvidenceAsync(s, adjustment.Id, cmd.EvidenceAttachmentIds ?? [], c);
            var dto = await DtoAsync(s, adjustment, c);
            s.Audit(new AuditEntry("AdjustmentRequested", "Adjustment", adjustment.Id, adjustment.Version, cmd.Narrative, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    /// <summary>Any change voids the adjustment's live approval (IAM-003, AT-EXC-02).</summary>
    public Task<AdjustmentDto> UpdateAsync(Guid id, int expectedVersion, UpdateAdjustment cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AdjustmentsRequest);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var adjustment = await AdjustmentRules.LockAsync(s, id, c);
            caller.RequireWarehouse(Permissions.AdjustmentsRequest, adjustment.WarehouseId);
            Versioning.EnsureVersion(adjustment.Version, expectedVersion);
            if (!adjustment.IsOpen)
                throw AppException.InvalidState("Only a requested adjustment can be changed.");
            var product = await s.Db.Set<Product>().AsNoTracking().SingleAsync(p => p.Id == adjustment.ProductId, c);
            var before = await DtoAsync(s, adjustment, c);
            if (cmd.Delta is { } d)
            {
                var delta = d.Value;
                if (decimal.Round(delta, product.BasePrecision) != delta)
                    throw AppException.Validation("delta", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
                if (product.IsSerialTracked && Math.Abs(delta) != 1)
                    throw AppException.Validation("delta", "A serial-tracked adjustment changes exactly one unit.");
                if (delta < 0 && adjustment.BucketId is null)
                    throw AppException.Validation("delta", "There is no stock here to reduce.");
                if (adjustment.BucketId is { } bucket)
                    await AdjustmentRules.EnsureReducibleAsync(s, bucket, delta, forUpdate: false, c);
            }
            DomainGuard.Run(() => adjustment.Edit(cmd.Delta?.Value, cmd.ReasonCode, cmd.Narrative, caller.Subject, s.Now));
            if (cmd.EvidenceAttachmentIds is { } evidence)
            {
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.attachment SET entity_type = NULL, entity_id = NULL, updated_at = {s.Now}, version = version + 1
                    WHERE entity_type = 'Adjustment' AND entity_id = {id}
                    """, c);
                await BindEvidenceAsync(s, id, evidence, c);
            }
            var invalidated = await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.Adjustment, id, "The adjustment changed after the approval was requested (IAM-003).", c);
            await s.Db.SaveChangesAsync(c);
            var after = await DtoAsync(s, adjustment, c);
            s.Audit(new AuditEntry("AdjustmentUpdated", "Adjustment", id, adjustment.Version, null, before, new { adjustment = after, invalidatedApprovals = invalidated }));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<AdjustmentDto> CancelAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AdjustmentsRequest);
        reasonValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            var adjustment = await AdjustmentRules.LockAsync(s, id, c);
            caller.RequireWarehouse(Permissions.AdjustmentsRequest, adjustment.WarehouseId);
            Versioning.EnsureVersion(adjustment.Version, expectedVersion);
            if (!adjustment.IsOpen)
                throw AppException.InvalidState("Only a requested adjustment can be cancelled.");
            var before = await DtoAsync(s, adjustment, c);
            DomainGuard.Run(() => adjustment.Cancel(caller.Subject, s.Now));
            var invalidated = await ApprovalSubjects.InvalidateLiveAsync(s, ApprovalSubjectType.Adjustment, id, "The adjustment was cancelled.", c);
            await s.Db.SaveChangesAsync(c);
            var after = await DtoAsync(s, adjustment, c);
            s.Audit(new AuditEntry("AdjustmentCancelled", "Adjustment", id, adjustment.Version, cmd.Reason, before, new { adjustment = after, invalidatedApprovals = invalidated }));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    /// <summary>Posts once against a signed approval of this exact version (EXC-004, IAM-003, SOD-4, AT-EXC-02, AT-IAM-01).</summary>
    public Task<CommandOutcome> PostAsync(Guid idempotencyKey, Guid id, int expectedVersion, ApprovalReference cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AdjustmentsPost);
        caller.RequireMutable();
        if (cmd.ApprovalId == Guid.Empty)
            throw AppException.Validation("approvalId", "Name the approval that signed this adjustment.");
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd.ApprovalId }, JsonDefaults.Options);
        var operationKey = $"adjustment:{caller.Subject}:{idempotencyKey:D}";

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /adjustments/{id}/post", payload), async (s, c) =>
        {
            var adjustment = await AdjustmentRules.LockAsync(s, id, c);
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
                return new CommandOutcome(200, JsonNode.Parse(earlier[0]));
            caller.RequireWarehouse(Permissions.AdjustmentsPost, adjustment.WarehouseId);
            Versioning.EnsureVersion(adjustment.Version, expectedVersion);
            if (!adjustment.IsOpen)
                throw AppException.InvalidState("Only a requested adjustment can be posted.");

            // 1. The approval binds this exact version and content, is unexpired, and its signer still may sign (IAM-003, SOD-1, SOD-4).
            var approval = await ApprovalSubjects.RequireBindingAsync(s, await ApprovalSubjects.AdjustmentAsync(s, adjustment, c), cmd.ApprovalId, c);

            // 2. The bucket: the named one, or the matching one created for an increase; reserved rechecked under lock.
            if (await StockBlocks.TenantBlockedAsync(s, c))
                throw new AppException(ErrorCode.StockBlocked, "Stock changes are blocked while an audit-chain failure is under investigation (OPS-001).");
            var bucketId = adjustment.BucketId ?? await BucketForIncreaseAsync(s, caller, adjustment, c);
            await AdjustmentRules.EnsureReducibleAsync(s, bucketId, adjustment.DeltaQty, forUpdate: true, c);
            await StockBlocks.EnsureNotBlockedAsync(s, [bucketId], c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + {adjustment.DeltaQty}, updated_at = {s.Now}, version = version + 1 WHERE id = {bucketId}
                """, c);

            // 3. One movement; the approval is consumed by this posting only (unique operation).
            var movement = StockMovement.Adjustment(caller.TenantId, $"adjustment:{adjustment.Id:D}", adjustment.ProductId, adjustment.LotId,
                adjustment.SerialId, bucketId, adjustment.DeltaQty, adjustment.Id, adjustment.Narrative, caller.Subject, s.Now, caller.CorrelationId);
            s.Db.Add(movement);
            var before = await DtoAsync(s, adjustment, c);
            DomainGuard.Run(() => approval.Consume($"adjustment:{adjustment.Id:D}:post", s.Now));
            DomainGuard.Run(() => adjustment.Post(approval.Id, bucketId, movement.Id, s.Now));
            await s.Db.SaveChangesAsync(c);

            var result = new PostingResult([movement.Id], nameof(AdjustmentStatus.Posted), adjustment.Version);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
                VALUES ({caller.TenantId}, {operationKey}, 'AdjustmentPost', {JsonSerializer.Serialize(result, JsonDefaults.Options)}::jsonb, {caller.Subject}, {s.Now})
                """, c);
            s.Audit(new AuditEntry("AdjustmentPosted", "Adjustment", adjustment.Id, adjustment.Version, adjustment.Narrative, before,
                new { adjustment = await DtoAsync(s, adjustment, c), approvalId = approval.Id, movementId = movement.Id }));
            s.Publish("StockAdjusted", new { adjustmentId = adjustment.Id, movementId = movement.Id, delta = Quantity.Format(adjustment.DeltaQty) });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    public Task<AdjustmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var adjustment = await s.Db.Set<Adjustment>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, c) ?? throw AppException.NotFound("Adjustment");
            caller.RequireWarehouse(Permissions.StockRead, adjustment.WarehouseId);
            return await DtoAsync(s, adjustment, c);
        }, ct);
    }

    public Task<Page<AdjustmentDto>> ListAsync(AdjustmentFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Adjustment>().AsNoTracking();
            if (scope is not null)
                query = query.Where(a => scope.Contains(a.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(a => a.WarehouseId == w);
            if (filter.Status is { } status)
            {
                // "Approved" is not stored: it is a Requested adjustment with a live signed approval (DEC-057).
                var stored = status == AdjustmentStatus.Approved ? AdjustmentStatus.Requested : status;
                query = query.Where(a => a.Status == stored);
            }
            var rows = await query.ToPageAsync(page, $"adjustments|{filter.WarehouseId}|{filter.Status}", cursors, a => a, c);
            var dtos = new List<AdjustmentDto>();
            foreach (var adjustment in rows.Items)
            {
                var dto = await DtoAsync(s, adjustment, c);
                if (filter.Status is not { } wanted || dto.Status == wanted)
                    dtos.Add(dto);
            }
            return new Page<AdjustmentDto>(dtos, rows.NextCursor);
        }, ct);
    }

    internal static async Task<AdjustmentDto> DtoAsync(ITenantSession s, Adjustment a, CancellationToken ct)
    {
        var subject = await ApprovalSubjects.AdjustmentAsync(s, a, ct);
        var binding = a.Status == AdjustmentStatus.Requested ? await ApprovalSubjects.BindingAsync(s, subject, ct) : null;
        return new AdjustmentDto(a.Id, a.Version, a.CreatedAt, a.CreatedBy, a.UpdatedAt, a.WarehouseId, a.BucketId, a.BinId, a.ProductId, a.LotId, a.SerialId,
            a.QualityStatus, SignedQuantity.From(a.DeltaQty), a.ReasonCode, a.Narrative, await AdjustmentRules.EvidenceAsync(s, a.Id, ct),
            binding is not null ? AdjustmentStatus.Approved : a.Status, binding?.Id ?? a.ApprovalId, a.PostedMovementId, Convert.ToHexStringLower(subject.Digest));
    }

    private static async Task<Guid> BucketForIncreaseAsync(ITenantSession s, CallerContext caller, Adjustment a, CancellationToken ct)
    {
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at, on_hand_qty,
                                         created_by, created_at, updated_at)
            VALUES ({caller.TenantId}, {a.WarehouseId}, {a.BinId}, {a.ProductId}, {a.LotId}, {a.SerialId}, {a.QualityStatus.ToString()}::ti.quality_status,
                    {s.Now}, 0, {caller.Subject}, {s.Now}, {s.Now})
            ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status) DO NOTHING
            """, ct);
        return (await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.stock_bucket
            WHERE bin_id = {a.BinId} AND product_id = {a.ProductId} AND lot_id IS NOT DISTINCT FROM {a.LotId}
              AND serial_id IS NOT DISTINCT FROM {a.SerialId} AND quality_status = {a.QualityStatus.ToString()}::ti.quality_status
            """).ToListAsync(ct)).Single();
    }

    /// <summary>Binds evidence files to the adjustment; a file already bound to something else is refused.</summary>
    private static async Task BindEvidenceAsync(ITenantSession s, Guid adjustmentId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        var array = ids.ToArray();
        var bound = await s.Db.Database.ExecuteSqlAsync($"""
            UPDATE ti.attachment SET entity_type = 'Adjustment', entity_id = {adjustmentId}, updated_at = {s.Now}, version = version + 1
            WHERE id = ANY({array}) AND (entity_id IS NULL OR (entity_type = 'Adjustment' AND entity_id = {adjustmentId}))
            """, ct);
        if (bound != ids.Count)
            throw AppException.Validation("evidenceAttachmentIds", "Every evidence file must exist and not belong to another record.");
    }

}
