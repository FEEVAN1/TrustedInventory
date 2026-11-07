using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Audit;

public enum AuditVerificationFailure
{
    HashMismatch,
    SequenceGap,
    CheckpointMismatch,
    MissingCheckpoint,
}

public sealed record AuditEventDto(
    long Sequence, DateTimeOffset OccurredAt, string ActorSubject, ActorType ActorType, string CorrelationId,
    string EventType, string ObjectType, Guid? ObjectId, int? ObjectVersion, string? Reason, JsonNode? Before,
    JsonNode? After, string PreviousHash, string EventHash)
{
    // The contract has one correlation field; the request id is used when the caller sent no correlation id.
    public static AuditEventDto From(StoredAuditEvent e) =>
        new(e.Sequence, e.OccurredAt, e.ActorSubject, e.ActorType, e.CorrelationId ?? e.RequestId ?? "", e.EventType,
            e.ObjectType, e.ObjectId, e.ObjectVersion, e.Reason, StoredAuditEvent.Parse(e.BeforeState),
            StoredAuditEvent.Parse(e.AfterState), Convert.ToHexStringLower(e.PreviousHash), Convert.ToHexStringLower(e.EventHash));
}

public sealed record AuditEventFilter(
    DateTimeOffset? From, DateTimeOffset? To, long? FromSequence, long? ToSequence, string? ObjectType, Guid? ObjectId,
    string? ActorSubject);

public sealed record AuditVerifyRequest(long? FromSequence, long? ToSequence);

public sealed record CheckpointDto(DateOnly Date, long Sequence, string EventHash, string SignatureReference);

public sealed record AuditVerificationResult(
    bool Verified, long FromSequence, long ToSequence, long EventsChecked, long? FirstFailureSequence,
    AuditVerificationFailure? Failure, CheckpointDto? Checkpoint);

public sealed class AuditVerifyRequestValidator : AbstractValidator<AuditVerifyRequest>
{
    public AuditVerifyRequestValidator()
    {
        RuleFor(x => x.FromSequence).GreaterThanOrEqualTo(1);
        RuleFor(x => x.ToSequence).GreaterThanOrEqualTo(1);
        RuleFor(x => x).Must(x => x.FromSequence is null || x.ToSequence is null || x.FromSequence <= x.ToSequence)
            .WithName("toSequence").WithMessage("toSequence must not be lower than fromSequence.");
    }
}

/// <summary>
/// Audit trail reading and hash-chain verification (AUD-001..003, AT-AUD-01). A failed verification opens a
/// High AuditChainFailure alert (OPS-001); blocking stock mutations on that alert arrives with stock in M2.
/// </summary>
public sealed class AuditService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IAuditSupport support,
    IValidator<AuditVerifyRequest> verifyValidator)
{
    public Task<Page<AuditEventDto>> ListAsync(AuditEventFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AuditRead);
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"audit|{filter.From:O}|{filter.To:O}|{filter.FromSequence}|{filter.ToSequence}|{filter.ObjectType}|{filter.ObjectId}|{filter.ActorSubject}";

        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<StoredAuditEvent>().AsNoTracking();
            if (filter.From is { } from)
                query = query.Where(e => e.OccurredAt >= from);
            if (filter.To is { } to)
                query = query.Where(e => e.OccurredAt < to);
            if (filter.FromSequence is { } fs)
                query = query.Where(e => e.Sequence >= fs);
            if (filter.ToSequence is { } ts)
                query = query.Where(e => e.Sequence <= ts);
            if (filter.ObjectType is { Length: > 0 } ot)
                query = query.Where(e => e.ObjectType == ot);
            if (filter.ObjectId is { } oid)
                query = query.Where(e => e.ObjectId == oid);
            if (filter.ActorSubject is { Length: > 0 } actor)
                query = query.Where(e => e.ActorSubject == actor);
            if (page.Cursor is { } cursor)
            {
                // Newest first (default -createdAt); the sequence orders events exactly as they were written.
                var after = cursors.Decode(cursor, filterKey).CreatedAt.UtcTicks;
                query = query.Where(e => e.Sequence < after);
            }

            var rows = await query.OrderByDescending(e => e.Sequence).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(new DateTimeOffset(rows[^1].Sequence, TimeSpan.Zero), rows[^1].Id), filterKey);
            }
            return new Page<AuditEventDto>(rows.Select(AuditEventDto.From).ToList(), next);
        }, ct);
    }

    /// <summary>
    /// Recomputes the chain over a range: contiguous sequences, previous-hash links and each event hash, then
    /// compares the newest checkpoint in range. Checkpoint signatures are verified by the checkpoint job.
    /// </summary>
    public Task<AuditVerificationResult> VerifyAsync(AuditVerifyRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.AuditRead);
        verifyValidator.ValidateOrThrow(request);

        return uow.WriteAsync(async (s, c) =>
        {
            var head = await support.StreamHeadAsync(s, c);
            var from = request.FromSequence ?? 1;
            var to = request.ToSequence ?? Math.Max(head, from);
            if (from > Math.Max(head, 1))
                throw AppException.Validation("fromSequence", $"The stream currently ends at sequence {head}.");

            var events = s.Db.Set<StoredAuditEvent>().AsNoTracking();
            byte[] previous;
            long? failureAt = null;
            AuditVerificationFailure? failure = null;
            if (from == 1)
            {
                previous = AuditHash.Genesis;
            }
            else
            {
                var prior = await events.Where(e => e.Sequence == from - 1).Select(e => e.EventHash).SingleOrDefaultAsync(c);
                previous = prior ?? [];
                if (prior is null)
                    (failureAt, failure) = (from - 1, AuditVerificationFailure.SequenceGap);
            }

            var verifiedHashes = new Dictionary<long, byte[]>();
            long checkedCount = 0;
            var expected = from;
            if (failure is null)
            {
                await foreach (var e in events.Where(e => e.Sequence >= from && e.Sequence <= to).OrderBy(e => e.Sequence).AsAsyncEnumerable().WithCancellation(c))
                {
                    if (e.Sequence != expected)
                    {
                        (failureAt, failure) = (expected, AuditVerificationFailure.SequenceGap);
                        break;
                    }
                    checkedCount++;
                    if (!e.PreviousHash.AsSpan().SequenceEqual(previous) || !e.ToRecord().Hash(previous).AsSpan().SequenceEqual(e.EventHash))
                    {
                        (failureAt, failure) = (e.Sequence, AuditVerificationFailure.HashMismatch);
                        break;
                    }
                    verifiedHashes[e.Sequence] = e.EventHash;
                    previous = e.EventHash;
                    expected++;
                }
                if (failure is null && expected <= Math.Min(to, head))
                    (failureAt, failure) = (expected, AuditVerificationFailure.SequenceGap);
            }

            CheckpointDto? checkpointDto = null;
            if (await support.LatestCheckpointAsync(s, to, c) is { } checkpoint)
            {
                checkpointDto = new CheckpointDto(checkpoint.Date, checkpoint.Sequence, Convert.ToHexStringLower(checkpoint.EventHash),
                    checkpoint.SignatureReference);
                if (failure is null && verifiedHashes.TryGetValue(checkpoint.Sequence, out var hash)
                    && !hash.AsSpan().SequenceEqual(checkpoint.EventHash))
                    (failureAt, failure) = (checkpoint.Sequence, AuditVerificationFailure.CheckpointMismatch);
            }

            if (failure is { } f)
            {
                await support.RaiseAlertAsync(s, AlertType.AuditChainFailure, AlertSeverity.High, "audit-chain",
                    new JsonObject
                    {
                        ["failure"] = f.ToString(),
                        ["sequence"] = failureAt,
                        ["detectedBy"] = caller.Subject,
                    }, c);
            }
            return new AuditVerificationResult(failure is null, from, Math.Max(to, 1), checkedCount, failureAt, failure, checkpointDto);
        }, ct);
    }
}
