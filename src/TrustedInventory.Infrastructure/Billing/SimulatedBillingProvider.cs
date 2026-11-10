using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// [SIMULATED BILLING] The only billing provider until the owner chooses one (D09, DEC-039, DEC-063). State lives in memory for
/// this process; nothing is ever charged. Hosted pages are https://billing-simulator.invalid/... URLs that cannot be opened:
/// outcomes are driven through the test console methods below.
/// </summary>
public sealed partial class SimulatedBillingProvider(TimeProvider clock, ILogger<SimulatedBillingProvider> log) : IBillingProvider
{
    public const string HostedBase = "https://billing-simulator.invalid";
    public static readonly TimeSpan PortalLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, object> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SimCheckout> _checkouts = new(StringComparer.Ordinal);
    private int _calls;

    public string Code => BillingOptions.Simulator;

    // ---- Test console (development and tests only) ----

    /// <summary>While set, every adapter call fails as if the provider were unreachable (AT-SOP-01).</summary>
    public bool Outage { get; set; }

    /// <summary>Adapter calls that created something, for idempotency evidence.</summary>
    public int CreatedObjects => _byKey.Count;

    public int Calls => _calls;

    public SimCheckout? Checkout(string sessionRef) => _checkouts.GetValueOrDefault(sessionRef);

    public SimCheckout? CheckoutFor(Guid checkoutSessionId) => _checkouts.Values.FirstOrDefault(c => c.CheckoutSessionId == checkoutSessionId);

    // ---- Adapter ----

    public Task<string> EnsureCustomerAsync(ProviderCustomerRequest request, CancellationToken ct) =>
        Once($"customer:{request.BillingAccountId}", () => $"sim_cus_{request.BillingAccountId:N}");

    public Task<string> EnsurePriceAsync(Guid priceVersionId, MoneyDto amount, BillingInterval interval, CancellationToken ct) =>
        Once($"price:{priceVersionId}", () => $"sim_price_{priceVersionId:N}");

    public Task<ProviderCheckout> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken ct) =>
        Once($"checkout:{request.CheckoutSessionId}", () =>
        {
            var reference = $"sim_cs_{Guid.NewGuid():N}";
            _checkouts[reference] = new SimCheckout(reference, request.CheckoutSessionId, request.ProviderCustomerId, request.ProviderPriceRef,
                request.ExpiresAt);
            return new ProviderCheckout(reference, $"{HostedBase}/checkout/{reference}", request.ExpiresAt);
        });

    public Task<ProviderPortal> CreatePortalSessionAsync(Guid billingAccountId, string providerCustomerId, string returnUrl, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return Once($"portal:{billingAccountId}:{now:yyyyMMddHHmm}", () =>
            new ProviderPortal($"{HostedBase}/portal/{Guid.NewGuid():N}", now + PortalLifetime));
    }

    private Task<T> Once<T>(string key, Func<T> create) where T : notnull
    {
        Interlocked.Increment(ref _calls);
        if (Outage)
            throw new BillingProviderUnavailableException("[SIMULATED BILLING] outage");
        T result;
        lock (_byKey) // create() may record side state; run it once per key
            result = (T)_byKey.GetOrAdd(key, _ => create());
        LogCall(log, key[..key.IndexOf(':', StringComparison.Ordinal)]);
        return Task.FromResult(result);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[SIMULATED BILLING] {Operation}")]
    private static partial void LogCall(ILogger logger, string operation);
}

/// <summary>A simulated hosted checkout; nothing completes it until slice 3's console pays it and sends a signed event.</summary>
public sealed record SimCheckout(string SessionRef, Guid CheckoutSessionId, string CustomerRef, string PriceRef, DateTimeOffset ExpiresAt);
