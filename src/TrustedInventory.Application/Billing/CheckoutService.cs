using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Billing;

public sealed record CheckoutPreviewRequest(Guid PriceVersionId);

public sealed record CheckoutRequest(Guid PriceVersionId, string AcceptedPreviewDigest);

public sealed record CheckoutPreviewDto(Guid PriceVersionId, string PlanCode, MoneyDto Amount, BillingInterval Interval, DateTimeOffset StartsAt,
    string Renewal, string CancellationPolicy, string PreviewDigest);

public sealed record CheckoutSessionDto(Guid CheckoutId, string HostedUrl, DateTimeOffset ExpiresAt, CheckoutStatus Status, string? RetryGuidance);

public sealed record BillingPortalSessionDto(string Url, DateTimeOffset ExpiresAt);

/// <summary>Who acts, for the tenant's audit chain (AUD-001).</summary>
public sealed record BillingActor(Guid TenantId, string Subject, string? RequestId, string? CorrelationId);

public sealed record BillingAccountRow(Guid Id, string LegalName, string Country, string ContactEmail, string? Provider, string? ProviderCustomerId);

public sealed record CheckoutPriceRow(Guid PriceVersionId, Guid PlanVersionId, string PlanCode, bool PlanPublished, bool PlanPublic, string Currency,
    long MinorUnits, int Exponent, BillingInterval Interval, DateTimeOffset ActiveFrom, DateTimeOffset? ActiveTo, string? Provider, string? ProviderPriceRef);

public sealed record CheckoutContext(SubscriptionState Subscription, Guid ContractedPlanVersionId, BillingAccountRow Account, CheckoutPriceRow? Price);

public sealed record CheckoutRow(Guid Id, Guid PriceVersionId, string AcceptedPreviewDigest, Guid IdempotencyKey, CheckoutStatus Status,
    string? HostedUrl, DateTimeOffset ExpiresAt, bool Created);

/// <summary>Control-plane checkout persistence (ti_control); audit entries go to the tenant's chain in the same transaction.</summary>
public interface ICheckoutStore
{
    Task<CheckoutContext?> LoadAsync(Guid tenantId, Guid priceVersionId, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's session for this idempotency key unchanged, or else expires any other open session and inserts a
    /// new open one (CheckoutExpired / CheckoutStarted), under the billing account row lock.
    /// </summary>
    Task<CheckoutRow> BeginAsync(BillingActor actor, Guid idempotencyKey, Guid priceVersionId, string digest, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken ct);

    Task SetCustomerAsync(Guid billingAccountId, string providerCode, string providerCustomerId, CancellationToken ct);

    /// <summary>Records a provider price reference only where none exists yet.</summary>
    Task SetPriceRefAsync(Guid priceVersionId, string providerCode, string providerPriceRef, CancellationToken ct);

    Task AttachProviderSessionAsync(Guid checkoutId, string providerSessionRef, string hostedUrl, CancellationToken ct);

    Task RecordPortalAsync(BillingActor actor, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct);
}

/// <summary>
/// Preview, consent and hosted checkout (BIL-002, BIL-003, AT-BIL-01, DEC-063). Only a verified paid provider event completes a
/// session (slice 3); a browser returning from the hosted page changes nothing.
/// </summary>
public sealed class CheckoutService(ICheckoutStore store, IBillingProvider provider, ITenantUnitOfWork uow, ICallerAccessor callers,
    TimeProvider clock, BillingOptions options)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(60);

    public const string CancellationPolicy =
        "Cancel any time: cancellation takes effect at the end of the paid period and access continues until then. " +
        "No automatic refunds and no proration (BIL-008).";

    public const string RetryGuidance = "This checkout expired. Preview the price again and start a new checkout.";

    public async Task<CheckoutPreviewDto> PreviewAsync(CheckoutPreviewRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingManage);
        var context = await EligibleAsync(caller.TenantId, request.PriceVersionId, ct);
        return Preview(caller.TenantId, context.Price!, clock.GetUtcNow());
    }

    public async Task<CheckoutSessionDto> CreateAsync(Guid idempotencyKey, CheckoutRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingManage);
        if (request.AcceptedPreviewDigest is null || request.AcceptedPreviewDigest.Length != 64)
            throw AppException.Validation("acceptedPreviewDigest", "Send the previewDigest of the preview you accepted.");
        var context = await EligibleAsync(caller.TenantId, request.PriceVersionId, ct);
        var now = clock.GetUtcNow();
        var expected = Preview(caller.TenantId, context.Price!, now).PreviewDigest;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(request.AcceptedPreviewDigest)))
            throw AppException.InvalidState("The accepted preview does not match the current terms. Preview again and accept the new terms (BIL-003).");

        var actor = new BillingActor(caller.TenantId, caller.Subject, caller.RequestId, caller.CorrelationId);
        var row = await store.BeginAsync(actor, idempotencyKey, request.PriceVersionId, expected, now, now + SessionLifetime, ct);
        if (!row.Created && (row.PriceVersionId != request.PriceVersionId || row.AcceptedPreviewDigest != expected))
            throw new AppException(ErrorCode.IdempotencyMismatch, "This Idempotency-Key was used for a different checkout.");
        if (row.HostedUrl is not null || Status(row.Status, row.ExpiresAt, now) != CheckoutStatus.Open)
            return Dto(row.Id, row.HostedUrl ?? "", row.ExpiresAt, row.Status, now);

        // Provider calls are outside our transactions and idempotent on our ids, so a retry with the same key finishes the job.
        try
        {
            var customer = context.Account.ProviderCustomerId;
            if (customer is null)
            {
                customer = await provider.EnsureCustomerAsync(new ProviderCustomerRequest(context.Account.Id, context.Account.LegalName,
                    context.Account.Country, context.Account.ContactEmail), ct);
                await store.SetCustomerAsync(context.Account.Id, provider.Code, customer, ct);
            }
            var price = context.Price!;
            var priceRef = price.Provider == provider.Code ? price.ProviderPriceRef : null;
            if (priceRef is null)
            {
                priceRef = await provider.EnsurePriceAsync(price.PriceVersionId, new MoneyDto(price.Currency, price.MinorUnits, price.Exponent),
                    price.Interval, ct);
                await store.SetPriceRefAsync(price.PriceVersionId, provider.Code, priceRef, ct);
            }
            var session = await provider.CreateCheckoutSessionAsync(
                new ProviderCheckoutRequest(row.Id, customer, priceRef, expected, row.ExpiresAt, options.ReturnUrl, context.Account.ContactEmail,
                    new MoneyDto(price.Currency, price.MinorUnits, price.Exponent)), ct);
            await store.AttachProviderSessionAsync(row.Id, session.SessionRef, session.HostedUrl, ct);
            return Dto(row.Id, session.HostedUrl, row.ExpiresAt, CheckoutStatus.Open, now);
        }
        catch (BillingProviderUnavailableException ex)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, $"The billing provider is unavailable; retry with the same Idempotency-Key. {ex.Message}");
        }
    }

    /// <summary>Bounded status read for the return page (SUI-001): Open until a verified paid event completes it.</summary>
    public Task<CheckoutSessionDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.BillingManage);
        return uow.ReadAsync(async (s, c) =>
        {
            var row = await s.Db.Database.SqlQuery<SessionRow>($"""
                SELECT id AS "Id", hosted_url AS "HostedUrl", expires_at AS "ExpiresAt", status::text AS "Status"
                FROM cp.checkout_session WHERE id = {id} AND hosted_url IS NOT NULL
                """).SingleOrDefaultAsync(c) ?? throw AppException.NotFound("Checkout");
            return Dto(row.Id, row.HostedUrl, row.ExpiresAt, Enum.Parse<CheckoutStatus>(row.Status), s.Now);
        }, ct);
    }

    public async Task<BillingPortalSessionDto> PortalAsync(CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingManage);
        var context = await store.LoadAsync(caller.TenantId, Guid.Empty, ct) ?? throw AppException.NotFound("Billing account");
        try
        {
            var customer = context.Account.ProviderCustomerId;
            if (customer is null)
            {
                customer = await provider.EnsureCustomerAsync(new ProviderCustomerRequest(context.Account.Id, context.Account.LegalName,
                    context.Account.Country, context.Account.ContactEmail), ct);
                await store.SetCustomerAsync(context.Account.Id, provider.Code, customer, ct);
            }
            var portal = await provider.CreatePortalSessionAsync(context.Account.Id, customer, options.ReturnUrl, ct);
            await store.RecordPortalAsync(new BillingActor(caller.TenantId, caller.Subject, caller.RequestId, caller.CorrelationId),
                clock.GetUtcNow(), portal.ExpiresAt, ct);
            return new BillingPortalSessionDto(portal.Url, portal.ExpiresAt);
        }
        catch (BillingProviderUnavailableException ex)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, $"The billing provider is unavailable; try again shortly. {ex.Message}");
        }
    }

    private async Task<CheckoutContext> EligibleAsync(Guid tenantId, Guid priceVersionId, CancellationToken ct)
    {
        var context = await store.LoadAsync(tenantId, priceVersionId, ct) ?? throw AppException.NotFound("Subscription");
        var now = clock.GetUtcNow();
        var price = context.Price;
        if (price is null || !price.PlanPublished || !(price.PlanPublic || price.PlanVersionId == context.ContractedPlanVersionId)
            || !provider.Supports(price.Currency) || price.ActiveFrom > now || (price.ActiveTo is { } to && to <= now))
            throw AppException.NotFound("Price");
        if (context.Subscription is SubscriptionState.Active or SubscriptionState.PastDue)
            throw AppException.InvalidState("The subscription is already paid; change plan instead of starting a new checkout.");
        return context;
    }

    /// <summary>The terms the caller accepts; the digest covers everything but the time (DEC-063).</summary>
    public static CheckoutPreviewDto Preview(Guid tenantId, CheckoutPriceRow price, DateTimeOffset now)
    {
        var renewal = $"Renews every {(price.Interval == BillingInterval.Month ? "month" : "year")} at {Format(price)} until cancelled. " +
                      "The first period starts when the provider confirms payment.";
        var terms = new JsonObject
        {
            ["tenantId"] = tenantId.ToString(),
            ["priceVersionId"] = price.PriceVersionId.ToString(),
            ["planVersionId"] = price.PlanVersionId.ToString(),
            ["currency"] = price.Currency,
            ["minorUnits"] = price.MinorUnits,
            ["exponent"] = price.Exponent,
            ["interval"] = price.Interval.ToString(),
            ["renewal"] = renewal,
            ["cancellationPolicy"] = CancellationPolicy,
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(terms))));
        return new CheckoutPreviewDto(price.PriceVersionId, price.PlanCode, new MoneyDto(price.Currency, price.MinorUnits, price.Exponent),
            price.Interval, now, renewal, CancellationPolicy, digest);
    }

    /// <summary>"XTS 10.00": integer minor units shown with the currency's exponent, never via floating point.</summary>
    public static string Format(CheckoutPriceRow price)
    {
        var scale = 1L;
        for (var i = 0; i < price.Exponent; i++)
            scale *= 10;
        var whole = price.MinorUnits / scale;
        var fraction = price.MinorUnits % scale;
        return price.Exponent == 0
            ? $"{price.Currency} {whole.ToString(CultureInfo.InvariantCulture)}"
            : $"{price.Currency} {whole.ToString(CultureInfo.InvariantCulture)}.{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(price.Exponent, '0')}";
    }

    public static CheckoutStatus Status(CheckoutStatus stored, DateTimeOffset expiresAt, DateTimeOffset now) =>
        stored == CheckoutStatus.Open && now >= expiresAt ? CheckoutStatus.Expired : stored;

    private static CheckoutSessionDto Dto(Guid id, string url, DateTimeOffset expiresAt, CheckoutStatus stored, DateTimeOffset now)
    {
        var status = Status(stored, expiresAt, now);
        return new CheckoutSessionDto(id, url, expiresAt, status, status == CheckoutStatus.Expired ? RetryGuidance : null);
    }

    private sealed record SessionRow(Guid Id, string HostedUrl, DateTimeOffset ExpiresAt, string Status);
}
