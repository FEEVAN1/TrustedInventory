using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Billing;

public sealed record BlockerDto(string Code, string? Quota, long? Used, long? Limit, string Detail);

public sealed record SubscriptionChangeDto(Guid Id, Guid TargetPlanVersionId, Guid TargetPriceVersionId, SubscriptionChangeState State,
    DateTimeOffset RequestedAt, string RequestedBy, DateTimeOffset EffectiveAt, IReadOnlyList<BlockerDto> Blockers);

/// <summary>Subscription schema; Version is the subscription revision (ETag, If-Match on plan changes).</summary>
public sealed record SubscriptionDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, SubscriptionState State, Guid PlanVersionId, string PlanCode,
    Guid? PriceVersionId, BillingInterval? Interval, DateTimeOffset? TrialStart, DateTimeOffset? TrialEnd, DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd, DateTimeOffset? GraceEnd, bool CancelAtPeriodEnd, DateTimeOffset? CancellationEffectiveAt,
    SubscriptionChangeDto? ScheduledChange, AccessMode AccessMode, IReadOnlyList<RestrictionReason> RestrictionReasons,
    IReadOnlyDictionary<string, long> Limits, IReadOnlyDictionary<string, long> Usage, DateTimeOffset? ComplimentaryUntil = null) : IVersioned;

public sealed record InvoiceLineDto(string Description, int Quantity, MoneyDto UnitAmount, MoneyDto Discount, MoneyDto Tax, MoneyDto Subtotal,
    MoneyDto Total);

public sealed record InvoiceDto(
    Guid Id, string Number, InvoiceStatus Status, PaymentStatus? PaymentStatus, IReadOnlyList<InvoiceLineDto> Lines, MoneyDto Subtotal,
    MoneyDto Tax, MoneyDto Total, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, DateTimeOffset? IssuedAt, DateTimeOffset? DueAt,
    DateTimeOffset? PaidAt);

/// <summary>One invoice line as snapshotted in cp.invoice.lines (BIL-001): minor units in the invoice currency.</summary>
public sealed record InvoiceLineSnapshot(string Description, int Quantity, long UnitAmount, long Discount, long Tax, long Subtotal, long Total);

/// <summary>
/// Tenant billing reads (BIL-001, BIL-006, ENT-003, LFC-001, DEC-062). They run in the tenant unit of work, where the
/// cp tenant_read policies show only the caller's own subscription, invoices and payments.
/// </summary>
public sealed class SubscriptionService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors)
{
    public Task<SubscriptionDto> GetAsync(CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingRead);
        return uow.ReadAsync((s, c) => ReadAsync(s, c), ct);
    }

    /// <summary>Reads the caller's subscription in an open tenant session (also used after billing commands).</summary>
    public static async Task<SubscriptionDto> ReadAsync(ITenantSession s, CancellationToken c)
    {
        var state = s.Caller.Access ?? throw AppException.NotFound("Subscription");
        var row = await s.Db.Database.SqlQuery<SubscriptionRow>($"""
            SELECT s.id AS "Id", s.revision AS "Revision", s.created_at AS "CreatedAt", s.updated_at AS "UpdatedAt", s.state::text AS "State",
                   s.plan_version_id AS "PlanVersionId", p.plan_code AS "PlanCode", s.price_version_id AS "PriceVersionId",
                   pr.billing_interval::text AS "Interval", s.trial_start AS "TrialStart", s.trial_end AS "TrialEnd",
                   s.period_start AS "PeriodStart", s.period_end AS "PeriodEnd", s.grace_end AS "GraceEnd",
                   s.cancel_at_period_end AS "CancelAtPeriodEnd", s.cancelled_at AS "CancelledAt"
            FROM cp.subscription s
            JOIN cp.plan_version p ON p.id = s.plan_version_id
            LEFT JOIN cp.price_version pr ON pr.id = s.price_version_id
            """).SingleOrDefaultAsync(c) ?? throw AppException.NotFound("Subscription");
        var change = await s.Db.Database.SqlQuery<ChangeRow>($"""
            SELECT id AS "Id", target_plan_version_id AS "TargetPlanVersionId", target_price_version_id AS "TargetPriceVersionId",
                   state::text AS "State", requested_at AS "RequestedAt", requested_by AS "RequestedBy", effective_at AS "EffectiveAt",
                   blockers::text AS "Blockers"
            FROM cp.subscription_change WHERE subscription_id = {row.Id} AND state IN ('Pending','Scheduled')
            """).SingleOrDefaultAsync(c);
        var usage = await TenantUsage.ReadAsync(s, c);

        return new SubscriptionDto(row.Id, checked((int)row.Revision), row.CreatedAt, row.UpdatedAt, Enum.Parse<SubscriptionState>(row.State),
            row.PlanVersionId, row.PlanCode, row.PriceVersionId, row.Interval is null ? null : Enum.Parse<BillingInterval>(row.Interval),
            row.TrialStart, row.TrialEnd, row.PeriodStart, row.PeriodEnd, row.GraceEnd, row.CancelAtPeriodEnd,
            row.CancelAtPeriodEnd ? row.PeriodEnd : row.CancelledAt,
            change is null ? null : ChangeDto(change), state.Mode(s.Now), state.Reasons(s.Now),
            // While complimentary access is in force its limits apply (DEC-094).
            (state.ComplimentaryInForce(s.Now) ? state.ComplimentaryLimits ?? new Dictionary<QuotaKey, long>() : state.Limits)
                .ToDictionary(l => SaasWire.Quota(l.Key), l => l.Value), usage,
            state.ComplimentaryInForce(s.Now) ? state.ComplimentaryUntil : null);
    }

    public Task<Page<InvoiceDto>> ListInvoicesAsync(InvoiceStatus? status, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.BillingRead);
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        var filterKey = $"invoices|{status}";
        var after = page.Cursor is { } cursor ? cursors.Decode(cursor, filterKey) : null;
        var statusText = status?.ToString() ?? "";
        return uow.ReadAsync(async (s, c) =>
        {
            // No cursor: start above every row (no NULL parameters, so every placeholder has a type).
            var afterAt = after?.CreatedAt ?? DateTimeOffset.MaxValue;
            var afterId = after?.Id ?? Guid.AllBitsSet;
            var rows = await s.Db.Database.SqlQueryRaw<InvoiceRow>(InvoiceSelect + """
                 WHERE ({0} = '' OR i.status::text = {0}) AND (i.created_at, i.id) < ({1}, {2})
                ORDER BY i.created_at DESC, i.id DESC
                LIMIT {3}
                """, statusText, afterAt, afterId, limit + 1).ToListAsync(c);
            var items = rows.Take(limit).ToList();
            var next = rows.Count > limit ? cursors.Encode(new CursorPosition(items[^1].CreatedAt, items[^1].Id), filterKey) : null;
            return new Page<InvoiceDto>(items.Select(Dto).ToList(), next);
        }, ct);
    }

    public Task<InvoiceDto> GetInvoiceAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.BillingRead);
        return uow.ReadAsync(async (s, c) =>
            Dto(await s.Db.Database.SqlQueryRaw<InvoiceRow>(InvoiceSelect + " WHERE i.id = {0}", id).SingleOrDefaultAsync(c) ?? throw AppException.NotFound("Invoice")), ct);
    }

    // Constant SQL; every value is passed as a parameter.
    private const string InvoiceSelect = """
        SELECT i.id AS "Id", i.created_at AS "CreatedAt", coalesce(i.number, i.provider_invoice_ref) AS "Number", i.status::text AS "Status",
               (SELECT p.status::text FROM cp.payment p WHERE p.invoice_id = i.id
                ORDER BY p.provider_occurred_at DESC, p.recorded_at DESC LIMIT 1) AS "PaymentStatus",
               i.lines::text AS "Lines", i.currency::text AS "Currency", i.exponent::int AS "Exponent", i.subtotal AS "Subtotal",
               i.tax AS "Tax", i.total AS "Total", coalesce(i.period_start, i.created_at) AS "PeriodStart",
               coalesce(i.period_end, i.period_start, i.created_at) AS "PeriodEnd", i.issued_at AS "IssuedAt", i.due_at AS "DueAt",
               i.paid_at AS "PaidAt"
        FROM cp.invoice i
        """;

    internal static SubscriptionChangeDto ChangeDto(ChangeRow r) =>
        new(r.Id, r.TargetPlanVersionId, r.TargetPriceVersionId, Enum.Parse<SubscriptionChangeState>(r.State), r.RequestedAt, r.RequestedBy,
            r.EffectiveAt, r.Blockers is null ? [] : JsonSerializer.Deserialize<List<BlockerDto>>(r.Blockers, JsonDefaults.Options) ?? []);

    private static InvoiceDto Dto(InvoiceRow r)
    {
        var currency = r.Currency.Trim();
        MoneyDto M(long amount) => new(currency, amount, r.Exponent);
        var lines = JsonSerializer.Deserialize<List<InvoiceLineSnapshot>>(r.Lines, JsonDefaults.Options) ?? [];
        return new InvoiceDto(r.Id, r.Number, Enum.Parse<InvoiceStatus>(r.Status),
            r.PaymentStatus is null ? null : Enum.Parse<PaymentStatus>(r.PaymentStatus),
            lines.Select(l => new InvoiceLineDto(l.Description, l.Quantity, M(l.UnitAmount), M(l.Discount), M(l.Tax), M(l.Subtotal), M(l.Total))).ToList(),
            M(r.Subtotal), M(r.Tax), M(r.Total), r.PeriodStart, r.PeriodEnd, r.IssuedAt, r.DueAt, r.PaidAt);
    }

    private sealed record SubscriptionRow(Guid Id, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string State, Guid PlanVersionId,
        string PlanCode, Guid? PriceVersionId, string? Interval, DateTimeOffset? TrialStart, DateTimeOffset? TrialEnd, DateTimeOffset? PeriodStart,
        DateTimeOffset? PeriodEnd, DateTimeOffset? GraceEnd, bool CancelAtPeriodEnd, DateTimeOffset? CancelledAt);

    internal sealed record ChangeRow(Guid Id, Guid TargetPlanVersionId, Guid TargetPriceVersionId, string State, DateTimeOffset RequestedAt,
        string RequestedBy, DateTimeOffset EffectiveAt, string? Blockers);

    private sealed record InvoiceRow(Guid Id, DateTimeOffset CreatedAt, string Number, string Status, string? PaymentStatus, string Lines,
        string Currency, int Exponent, long Subtotal, long Tax, long Total, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
        DateTimeOffset? IssuedAt, DateTimeOffset? DueAt, DateTimeOffset? PaidAt);
}
