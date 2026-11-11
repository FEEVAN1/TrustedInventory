using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Billing;

public sealed record SubscriptionChangePreviewRequest(Guid TargetPriceVersionId);

public sealed record SubscriptionChangeRequest(Guid TargetPriceVersionId, string AcceptedPreviewDigest);

public sealed record SubscriptionChangePreviewDto(Guid TargetPriceVersionId, string TargetPlanCode, MoneyDto Amount, DateTimeOffset EffectiveAt,
    IReadOnlyList<BlockerDto> Blockers, string PreviewDigest);

public sealed record CancelSubscriptionRequest(string Reason);

/// <summary>Features and limits of a plan or of the entitlement snapshot in force.</summary>
public sealed record PlanTerms(Guid PlanVersionId, IReadOnlySet<string> Features, IReadOnlyDictionary<QuotaKey, long> Limits);

public sealed record PlanChangeContext(Guid SubscriptionId, SubscriptionState State, long Revision, bool CancelAtPeriodEnd, DateTimeOffset? PeriodEnd,
    string? ProviderSubscriptionRef, Guid? CurrentPriceVersionId, Guid ContractedPlanVersionId, PlanTerms Current, CheckoutPriceRow? Target,
    PlanTerms? TargetTerms);

public sealed record PlanChangeRow(Guid Id, Guid TargetPriceVersionId, Guid TargetPlanVersionId, string Digest, SubscriptionChangeState State,
    string? ProviderChangeRef, DateTimeOffset RequestedAt, string RequestedBy, DateTimeOffset EffectiveAt, long SubscriptionRevision, bool Created);

/// <summary>Control-plane persistence for plan changes, cancel and resume (ti_control, DEC-065); audit goes to the tenant's chain.</summary>
public interface IPlanChangeStore
{
    Task<PlanChangeContext?> LoadAsync(Guid tenantId, Guid targetPriceVersionId, CancellationToken ct);

    Task<PlanChangeRow?> FindByKeyAsync(Guid tenantId, Guid idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Under the billing account and subscription locks: the existing change for this key, or (revision matching) cancels any open
    /// change and inserts a Pending one. Throws VERSION_CONFLICT when the subscription moved on.
    /// </summary>
    Task<PlanChangeRow> BeginAsync(BillingActor actor, Guid idempotencyKey, int expectedRevision, CheckoutPriceRow target, string digest,
        DateTimeOffset effectiveAt, DateTimeOffset now, CancellationToken ct);

    Task AttachProviderChangeAsync(Guid changeId, string providerChangeRef, DateTimeOffset now, CancellationToken ct);

    Task CancelAsync(BillingActor actor, int expectedRevision, string reason, DateTimeOffset now, CancellationToken ct);

    Task ResumeAsync(BillingActor actor, int expectedRevision, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Stock-dependent blockers, read in a tenant transaction (ti_app) because the control role reads no stock (SAS-004).</summary>
public static class PlanChangeBlockers
{
    public static async Task<IReadOnlyList<ChangeBlocker>> ComputeAsync(ITenantSession s, PlanTerms current, PlanTerms target, CancellationToken c)
    {
        var usage = (await TenantUsage.ReadAsync(s, c)).ToDictionary(u => SaasWire.ParseQuota(u.Key), u => u.Value);
        var blockers = PlanChangeRules.QuotaBlockers(usage, target.Limits).ToList();
        if (!PlanChangeRules.RemovesMedical(current.Features, target.Features))
            return blockers;

        var onHand = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.stock_bucket b JOIN ti.product p ON p.tenant_id = b.tenant_id AND p.id = b.product_id
            WHERE p.handling_profile = 'Medical' AND b.on_hand_qty > 0
            """).SingleAsync(c);
        var reserved = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.reservation r JOIN ti.product p ON p.tenant_id = r.tenant_id AND p.id = r.product_id
            WHERE p.handling_profile = 'Medical' AND r.state IN ('Active','Picked')
            """).SingleAsync(c);
        var transit = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.transfer_line l
            JOIN ti.transfer t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            JOIN ti.product p ON p.tenant_id = l.tenant_id AND p.id = l.product_id
            WHERE p.handling_profile = 'Medical' AND t.status IN ('InTransit','PartReceived') AND l.dispatched_qty > l.received_qty + l.disposed_qty
            """).SingleAsync(c);
        if (onHand > 0)
            blockers.Add(new ChangeBlocker("MEDICAL_STOCK_ON_HAND", null, onHand, null,
                $"{onHand} medical stock positions are on hand. Medical storage cannot be removed while medical stock remains (ENT-006)."));
        if (reserved > 0)
            blockers.Add(new ChangeBlocker("MEDICAL_RESERVATIONS", null, reserved, null,
                $"{reserved} reservations hold medical stock. Release or ship them first."));
        if (transit > 0)
            blockers.Add(new ChangeBlocker("MEDICAL_IN_TRANSIT", null, transit, null,
                $"{transit} transfer lines with medical stock are in transit. Receive them first."));
        return blockers;
    }

    public static BlockerDto Dto(ChangeBlocker b) => new(b.Code, b.Quota is { } q ? SaasWire.Quota(q) : null, b.Used, b.Limit, b.Detail);
}

/// <summary>
/// Plan changes at the next full billing cycle, cancellation at period end and resume (ENT-005, ENT-006, BIL-008, DEC-065).
/// Provider calls are outside our transactions and idempotent on our ids.
/// </summary>
public sealed class PlanChangeService(IPlanChangeStore store, ICheckoutStore checkouts, IBillingProvider provider, ITenantUnitOfWork uow,
    ICallerAccessor callers, TimeProvider clock)
{
    public const string ProrationPolicy = "The new price applies from the next billing period; no proration and no refund for the current period.";

    public async Task<SubscriptionChangePreviewDto> PreviewAsync(SubscriptionChangePreviewRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingManage);
        var context = await EligibleAsync(caller.TenantId, request.TargetPriceVersionId, ct);
        var blockers = await uow.ReadAsync((s, c) => PlanChangeBlockers.ComputeAsync(s, context.Current, context.TargetTerms!, c), ct);
        return Preview(caller.TenantId, context, blockers);
    }

    public async Task<(SubscriptionChangeDto Change, int Revision)> ChangeAsync(Guid idempotencyKey, int ifMatch, SubscriptionChangeRequest request,
        CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingManage);
        if (request.AcceptedPreviewDigest is null || request.AcceptedPreviewDigest.Length != 64)
            throw AppException.Validation("acceptedPreviewDigest", "Send the previewDigest of the preview you accepted.");

        // A retry after a lost response is answered before If-Match: the first attempt already moved the revision on.
        var row = await store.FindByKeyAsync(caller.TenantId, idempotencyKey, ct);
        PlanChangeContext context;
        if (row is not null)
        {
            if (row.TargetPriceVersionId != request.TargetPriceVersionId || row.Digest != request.AcceptedPreviewDigest)
                throw new AppException(ErrorCode.IdempotencyMismatch, "This Idempotency-Key was used for a different plan change.");
            if (row.State != SubscriptionChangeState.Pending || row.ProviderChangeRef is not null)
                return (Dto(row), checked((int)row.SubscriptionRevision));
            context = await store.LoadAsync(caller.TenantId, row.TargetPriceVersionId, ct) ?? throw AppException.NotFound("Subscription");
        }
        else
        {
            context = await EligibleAsync(caller.TenantId, request.TargetPriceVersionId, ct);
            if (context.Revision != ifMatch)
                throw AppException.VersionConflict();
            var blockers = await uow.ReadAsync((s, c) => PlanChangeBlockers.ComputeAsync(s, context.Current, context.TargetTerms!, c), ct);
            var preview = Preview(caller.TenantId, context, blockers);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(preview.PreviewDigest), Encoding.ASCII.GetBytes(request.AcceptedPreviewDigest)))
                throw AppException.InvalidState("The accepted preview does not match the current terms or date. Preview again (ENT-005).");
            if (blockers.Count > 0)
                throw Blocked(blockers);
            var actor = new BillingActor(caller.TenantId, caller.Subject, caller.RequestId, caller.CorrelationId);
            row = await store.BeginAsync(actor, idempotencyKey, ifMatch, context.Target!, preview.PreviewDigest, preview.EffectiveAt, clock.GetUtcNow(), ct);
            if (!row.Created && (row.TargetPriceVersionId != request.TargetPriceVersionId || row.Digest != preview.PreviewDigest))
                throw new AppException(ErrorCode.IdempotencyMismatch, "This Idempotency-Key was used for a different plan change.");
            if (row.State != SubscriptionChangeState.Pending || row.ProviderChangeRef is not null)
                return (Dto(row), checked((int)row.SubscriptionRevision));
        }

        try
        {
            var target = context.Target!;
            var priceRef = target.Provider == provider.Code ? target.ProviderPriceRef : null;
            if (priceRef is null)
            {
                priceRef = await provider.EnsurePriceAsync(target.PriceVersionId, new MoneyDto(target.Currency, target.MinorUnits, target.Exponent),
                    target.Interval, ct);
                await checkouts.SetPriceRefAsync(target.PriceVersionId, provider.Code, priceRef, ct);
            }
            var reference = await provider.ScheduleChangeAsync(row.Id, context.ProviderSubscriptionRef!, priceRef, row.EffectiveAt, ct);
            await store.AttachProviderChangeAsync(row.Id, reference, clock.GetUtcNow(), ct);
            return (Dto(row with { State = SubscriptionChangeState.Scheduled, ProviderChangeRef = reference }), checked((int)row.SubscriptionRevision));
        }
        catch (BillingProviderUnavailableException ex)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, $"The billing provider is unavailable; retry with the same Idempotency-Key. {ex.Message}");
        }
    }

    public async Task<SubscriptionDto> CancelAsync(int ifMatch, CancelSubscriptionRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.SubscriptionCancel);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 2000)
            throw AppException.Validation("reason", "Give a reason of 1 to 2000 characters.");
        var context = await store.LoadAsync(caller.TenantId, Guid.Empty, ct) ?? throw AppException.NotFound("Subscription");
        if (context.State is not (SubscriptionState.Active or SubscriptionState.PastDue))
            throw AppException.InvalidState("Only a paid subscription can be cancelled; a trial simply ends at its end date.");
        if (context.CancelAtPeriodEnd)
            return await ReadAsync(ct); // already cancelled at period end: nothing changes
        if (context.Revision != ifMatch)
            throw AppException.VersionConflict();
        var subscriptionRef = Linked(context);
        await Provider(() => provider.CancelAtPeriodEndAsync(context.SubscriptionId, context.Revision, subscriptionRef, ct));
        await store.CancelAsync(Actor(caller), ifMatch, reason, clock.GetUtcNow(), ct);
        return await ReadAsync(ct);
    }

    public async Task<SubscriptionDto> ResumeAsync(int ifMatch, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.SubscriptionCancel);
        var context = await store.LoadAsync(caller.TenantId, Guid.Empty, ct) ?? throw AppException.NotFound("Subscription");
        if (!context.CancelAtPeriodEnd || context.State is not (SubscriptionState.Active or SubscriptionState.PastDue)
            || context.PeriodEnd is not { } end || clock.GetUtcNow() >= end)
            throw AppException.InvalidState("There is no pending cancellation to withdraw. Ended service needs a new checkout.");
        if (context.Revision != ifMatch)
            throw AppException.VersionConflict();
        var subscriptionRef = Linked(context);
        await Provider(() => provider.ResumeAsync(context.SubscriptionId, context.Revision, subscriptionRef, ct));
        await store.ResumeAsync(Actor(caller), ifMatch, clock.GetUtcNow(), ct);
        return await ReadAsync(ct);
    }

    private Task<SubscriptionDto> ReadAsync(CancellationToken ct) => uow.ReadAsync((s, c) => SubscriptionService.ReadAsync(s, c), ct);

    private async Task<PlanChangeContext> EligibleAsync(Guid tenantId, Guid targetPriceVersionId, CancellationToken ct)
    {
        var context = await store.LoadAsync(tenantId, targetPriceVersionId, ct) ?? throw AppException.NotFound("Subscription");
        var now = clock.GetUtcNow();
        var price = context.Target;
        if (price is null || context.TargetTerms is null || !price.PlanPublished
            || !(price.PlanPublic || price.PlanVersionId == context.ContractedPlanVersionId)
            || price.ActiveFrom > now || (price.ActiveTo is { } to && to <= now))
            throw AppException.NotFound("Price");
        switch (context.State)
        {
            case SubscriptionState.Trialing or SubscriptionState.Cancelled:
                throw AppException.InvalidState("There is no paid subscription to change; start a checkout instead.");
            case SubscriptionState.PastDue:
                throw AppException.InvalidState("Settle the overdue invoice before changing plan.");
        }
        if (context.CancelAtPeriodEnd)
            throw AppException.InvalidState("The subscription is cancelled at period end; resume it before changing plan.");
        if (price.PriceVersionId == context.CurrentPriceVersionId)
            throw AppException.InvalidState("The subscription is already on this price.");
        Linked(context);
        return context;
    }

    /// <summary>The terms the caller accepts; effective at the current period end (ENT-005, DEC-065).</summary>
    public static SubscriptionChangePreviewDto Preview(Guid tenantId, PlanChangeContext context, IReadOnlyList<ChangeBlocker> blockers)
    {
        var price = context.Target!;
        var effectiveAt = context.PeriodEnd ?? throw AppException.InvalidState("The subscription has no current period.");
        var terms = new JsonObject
        {
            ["tenantId"] = tenantId.ToString(),
            ["subscriptionId"] = context.SubscriptionId.ToString(),
            ["currentPriceVersionId"] = context.CurrentPriceVersionId?.ToString(),
            ["targetPriceVersionId"] = price.PriceVersionId.ToString(),
            ["targetPlanVersionId"] = price.PlanVersionId.ToString(),
            ["currency"] = price.Currency,
            ["minorUnits"] = price.MinorUnits,
            ["exponent"] = price.Exponent,
            ["interval"] = price.Interval.ToString(),
            ["effectiveAt"] = effectiveAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["policy"] = ProrationPolicy,
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(terms))));
        return new SubscriptionChangePreviewDto(price.PriceVersionId, price.PlanCode, new MoneyDto(price.Currency, price.MinorUnits, price.Exponent),
            effectiveAt, blockers.Select(PlanChangeBlockers.Dto).ToList(), digest);
    }

    public static AppException Blocked(IReadOnlyList<ChangeBlocker> blockers) =>
        new(ErrorCode.DowngradeBlocked, "The plan change cannot be scheduled while current usage or medical obligations exceed the new plan.")
        {
            Extensions = new JsonObject { ["blockers"] = JsonSerializer.SerializeToNode(blockers.Select(PlanChangeBlockers.Dto).ToList(), JsonDefaults.Options) },
        };

    private static string Linked(PlanChangeContext context) =>
        context.ProviderSubscriptionRef
        ?? throw AppException.InvalidState("The subscription is not linked to the billing provider; contact support to change it.");

    private static async Task Provider(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (BillingProviderUnavailableException ex)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, $"The billing provider is unavailable; try again shortly. {ex.Message}");
        }
    }

    private static BillingActor Actor(CallerContext caller) => new(caller.TenantId, caller.Subject, caller.RequestId, caller.CorrelationId);

    private static SubscriptionChangeDto Dto(PlanChangeRow row) =>
        new(row.Id, row.TargetPlanVersionId, row.TargetPriceVersionId, row.State, row.RequestedAt, row.RequestedBy, row.EffectiveAt, []);
}
