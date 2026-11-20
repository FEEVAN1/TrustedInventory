using System.Globalization;
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
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record LotFilter(Guid? ProductId, string? LotNumber, DateOnly? ExpiresBefore);

public sealed record SerialFilter(Guid? ProductId, Guid? LotId, string? SerialNumber);

/// <summary>POST /lots/{id}/expiry-correction (section 07, DEC-090).</summary>
public sealed record ExpiryCorrectionRequest(DateOnly ExpiryDate, string Reason, Guid ApprovalId);

public sealed class ExpiryCorrectionValidator : AbstractValidator<ExpiryCorrectionRequest>
{
    public ExpiryCorrectionValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.ApprovalId).NotEmpty();
    }
}

/// <summary>How a lot's stock stands, as far as an expiry correction is concerned (section 07).</summary>
internal sealed record LotHoldState(IReadOnlyList<LotBucketRow> Buckets, decimal InTransit)
{
    /// <summary>Every unit is held: nothing Available, nothing reserved, nothing in transit.</summary>
    public bool AllHeld => InTransit == 0 && Buckets.All(b => b.Quality != "Available" && b.Reserved == 0);
}

internal sealed record LotBucketRow(Guid Id, string Quality, decimal OnHand, decimal Reserved);

/// <summary>
/// Serials are append-only (created by receipt posting only), so the API reports version 1 and <c>updatedAt = createdAt</c>, as
/// shipments do (DEC-055).
/// </summary>
public sealed record SerialDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid ProductId,
    string SerialNumber, Guid? LotId) : IVersioned;

/// <summary>
/// Read access to lots and serials (section 07, DEC-087). Both are created only by receipt posting and identify product units across
/// warehouses, so reading them needs <c>stock.read</c> in the tenant, as the lot traceability report does (DEC-078). Number filters
/// match a prefix. The only change to a lot is the audited expiry correction (DEC-090).
/// </summary>
public sealed class LotService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors, IValidator<ExpiryCorrectionRequest> correctionValidator)
{
    /// <summary>
    /// Audited QA correction of a lot's expiry (section 07, MED-003, IAM-003, DEC-090): the lot at the expected version, all its stock
    /// held, and a signed ExpiryCorrection approval of this lot whose proposal names this date, used once. One transaction writes the
    /// correction record, the lot, the approval, the audit event, the outbox event and the idempotency record (ARC-003). Lock order:
    /// lot → approval → audit stream. The database refuses an expiry change without the correction row (migration 0030).
    /// </summary>
    public Task<CommandOutcome> CorrectExpiryAsync(Guid idempotencyKey, Guid id, int expectedVersion, ExpiryCorrectionRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.QualityHold);
        caller.RequireMutable();
        correctionValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd }, JsonDefaults.Options);
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /lots/{id}/expiry-correction", payload), async (s, c) =>
        {
            var subject = await ApprovalSubjects.LockAsync(s, ApprovalSubjectType.ExpiryCorrection, id, c);
            var lot = await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == id, c);
            Versioning.EnsureVersion(lot.Version, expectedVersion);
            var hold = await HoldStateAsync(s, id, c);
            if (!hold.AllHeld)
                throw AppException.InvalidState("Hold all stock of this lot first: nothing may be available, reserved or in transit (section 07).");
            if (lot.ExpiryDate == cmd.ExpiryDate)
                throw AppException.Validation("expiryDate", "The lot already has this expiry date.");
            var approval = await ApprovalSubjects.RequireBindingAsync(s, subject, cmd.ApprovalId, c);
            if (ApprovalProposal.FromJson(approval.Proposal)?.ExpiryDate != cmd.ExpiryDate)
                throw new AppException(ErrorCode.ApprovalStale, "The approval was signed for a different expiry date (IAM-003); request approval of this date.");

            var before = ToDto(lot, await CorrectedAsync(s, id, c));
            var correctionId = Guid.CreateVersion7();
            await s.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ti.lot_expiry_correction (id, tenant_id, lot_id, previous_expiry, new_expiry, approval_id, reason, created_by, created_at)
                VALUES ({correctionId}, {caller.TenantId}, {id}, {lot.ExpiryDate}, {cmd.ExpiryDate}, {approval.Id}, {cmd.Reason}, {caller.Subject}, {s.Now})
                """, c);
            await s.Db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ti.lot SET expiry_date = {cmd.ExpiryDate}, version = version + 1, updated_at = {s.Now} WHERE id = {id} AND version = {lot.Version}
                """, c);
            DomainGuard.Run(() => approval.Consume($"expiry-correction:{correctionId:D}", s.Now));
            var updated = await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == id, c);
            var after = ToDto(updated, true);
            s.Audit(new AuditEntry("LotExpiryCorrected", "Lot", id, updated.Version, cmd.Reason, before,
                new { lot = after, approvalId = approval.Id, approver = approval.ApproverSubject, correctionId }));
            s.Publish("LotExpiryCorrected", new { lotId = id, previous = lot.ExpiryDate, expiryDate = cmd.ExpiryDate, approvalId = approval.Id });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(after, JsonDefaults.Options));
        }, ct);
    }

    internal static async Task<Lot> LockAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var locked = await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.lot WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        if (locked.Count == 0)
            throw AppException.NotFound("Lot");
        return await s.Db.Set<Lot>().AsNoTracking().SingleAsync(l => l.Id == id, ct);
    }

    internal static async Task<LotHoldState> HoldStateAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var buckets = await s.Db.Database.SqlQuery<LotBucketRow>($"""
            SELECT id AS "Id", quality_status::text AS "Quality", on_hand_qty AS "OnHand", reserved_qty AS "Reserved"
            FROM ti.stock_bucket WHERE lot_id = {id} AND on_hand_qty > 0 ORDER BY id
            """).ToListAsync(ct);
        var transit = await s.Db.Database.SqlQuery<decimal>($"""
            SELECT coalesce(sum(l.dispatched_qty - l.received_qty - l.disposed_qty), 0) AS "Value"
            FROM ti.transfer_line l JOIN ti.transfer t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            WHERE l.lot_id = {id} AND t.status IN ('InTransit', 'PartReceived')
            """).ToListAsync(ct);
        return new LotHoldState(buckets, transit.SingleOrDefault());
    }

    /// <summary>What an ExpiryCorrection approval binds: the lot's identity, expiry and version and how each unit of it is held.</summary>
    internal static byte[] Digest(Lot lot, LotHoldState hold)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"lot|{lot.Id:D}|{lot.ProductId:D}|{lot.LotNumber}|{lot.ExpiryDate:yyyy-MM-dd}|{lot.Version}|transit:{Quantity.Format(hold.InTransit)}"));
        foreach (var b in hold.Buckets)
            text.Append(CultureInfo.InvariantCulture, $"|{b.Id:D}:{b.Quality}:{Quantity.Format(b.OnHand)}:{Quantity.Format(b.Reserved)}");
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }

    internal static async Task<bool> CorrectedAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ti.lot_expiry_correction WHERE lot_id = {id}").ToListAsync(ct)).Single() > 0;

    public Task<Page<LotDto>> ListLotsAsync(LotFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        if (filter.LotNumber is { Length: > 100 })
            throw AppException.Validation("lotNumber", "Use at most 100 characters.");
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Lot>().AsNoTracking();
            if (filter.ProductId is { } product)
                query = query.Where(l => l.ProductId == product);
            if (!string.IsNullOrEmpty(filter.LotNumber))
                query = query.Where(l => l.LotNumber.StartsWith(filter.LotNumber));
            if (filter.ExpiresBefore is { } before)
                query = query.Where(l => l.ExpiryDate != null && l.ExpiryDate < before);
            var lots = await query.ToPageAsync(page, $"lots|{filter.ProductId}|{filter.LotNumber}|{filter.ExpiresBefore:O}", cursors, l => l, c);
            var ids = lots.Items.Select(l => l.Id).ToArray();
            var corrected = (await s.Db.Database.SqlQuery<Guid>($"SELECT DISTINCT lot_id AS \"Value\" FROM ti.lot_expiry_correction WHERE lot_id = ANY({ids})").ToListAsync(c)).ToHashSet();
            return new Page<LotDto>(lots.Items.Select(l => ToDto(l, corrected.Contains(l.Id))).ToList(), lots.NextCursor);
        }, ct);
    }

    public Task<LotDto> GetLotAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var lot = await s.Db.Set<Lot>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, c) ?? throw AppException.NotFound("Lot");
            return ToDto(lot, await CorrectedAsync(s, id, c));
        }, ct);
    }

    public Task<Page<SerialDto>> ListSerialsAsync(SerialFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.StockRead);
        if (filter.SerialNumber is { Length: > 100 })
            throw AppException.Validation("serialNumber", "Use at most 100 characters.");
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var key = $"serials|{filter.ProductId}|{filter.LotId}|{filter.SerialNumber}";
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Serial>().AsNoTracking();
            if (filter.ProductId is { } product)
                query = query.Where(x => x.ProductId == product);
            if (filter.LotId is { } lot)
                query = query.Where(x => x.LotId == lot);
            if (!string.IsNullOrEmpty(filter.SerialNumber))
                query = query.Where(x => x.SerialNumber.StartsWith(filter.SerialNumber));
            // Serial is not a TenantEntity, so it is paged here in the same order and with the same cursor rules (API-004).
            if (page.Cursor is { } cursor)
            {
                var p = cursors.Decode(cursor, key);
                query = query.Where(x => x.CreatedAt < p.CreatedAt || (x.CreatedAt == p.CreatedAt && x.Id.CompareTo(p.Id) < 0));
            }
            var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), key);
            }
            return new Page<SerialDto>(rows.Select(x => new SerialDto(x.Id, 1, x.CreatedAt, x.CreatedBy, x.CreatedAt, x.ProductId, x.SerialNumber, x.LotId))
                .ToList(), next);
        }, ct);
    }

    /// <summary><c>expiryCorrected</c> is true once any correction of the lot is recorded (DEC-090).</summary>
    internal static LotDto ToDto(Lot lot, bool corrected) =>
        new(lot.Id, lot.Version, lot.CreatedAt, lot.CreatedBy, lot.UpdatedAt, lot.ProductId, lot.LotNumber, lot.ExpiryDate, lot.SupplierRef, corrected);
}
