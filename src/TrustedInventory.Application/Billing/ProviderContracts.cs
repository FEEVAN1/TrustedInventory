using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Billing;

public sealed record ProviderCustomerRequest(Guid BillingAccountId, string LegalName, string Country, string ContactEmail);

public sealed record ProviderCheckoutRequest(Guid CheckoutSessionId, string ProviderCustomerId, string ProviderPriceRef, string AcceptedPreviewDigest,
    DateTimeOffset ExpiresAt, string ReturnUrl);

public sealed record ProviderCheckout(string SessionRef, string HostedUrl, DateTimeOffset ExpiresAt);

public sealed record ProviderPortal(string Url, DateTimeOffset ExpiresAt);

/// <summary>The provider could not be reached or refused the call; our records stay unchanged (503, DEC-063).</summary>
public sealed class BillingProviderUnavailableException(string message) : Exception(message);

/// <summary>
/// Subscription payment provider adapter (DEC-039, DEC-063, docs/billing-adapter.md). Card data stays with the provider
/// (BIL-002). Every mutating call is idempotent on a key derived from our own record id.
/// </summary>
public interface IBillingProvider
{
    /// <summary>Provider key stored on every provider-referenced row, e.g. "simulator".</summary>
    string Code { get; }

    /// <summary>Key <c>customer:{billingAccountId}</c>.</summary>
    Task<string> EnsureCustomerAsync(ProviderCustomerRequest request, CancellationToken ct);

    /// <summary>Key <c>price:{priceVersionId}</c>: registers a catalog price that has no provider reference yet.</summary>
    Task<string> EnsurePriceAsync(Guid priceVersionId, MoneyDto amount, BillingInterval interval, CancellationToken ct);

    /// <summary>Key <c>checkout:{checkoutSessionId}</c>.</summary>
    Task<ProviderCheckout> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken ct);

    /// <summary>Key <c>portal:{billingAccountId}:{minute}</c>.</summary>
    Task<ProviderPortal> CreatePortalSessionAsync(Guid billingAccountId, string providerCustomerId, string returnUrl, CancellationToken ct);
}
