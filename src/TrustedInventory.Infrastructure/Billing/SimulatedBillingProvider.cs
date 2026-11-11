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
public sealed partial class SimulatedBillingProvider(TimeProvider clock, SimulatorSigning signing, ILogger<SimulatedBillingProvider> log)
    : IBillingProvider, IBillingWebhooks
{
    public const string HostedBase = "https://billing-simulator.invalid";
    public static readonly TimeSpan PortalLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, object> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SimCheckout> _checkouts = new(StringComparer.Ordinal);
    private int _calls;

    public string Code => BillingOptions.Simulator;

    public bool Supports(string currency) => currency is Domain.Saas.Money.TestCurrency or Domain.Saas.Money.Naira;

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
        Once($"price:{priceVersionId}", () =>
        {
            var reference = $"sim_price_{priceVersionId:N}";
            _prices[reference] = new SimPrice(reference, amount.Currency, amount.MinorUnits, (short)amount.Exponent, interval);
            return reference;
        });

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

    public Task<ProviderChangeSchedule> ScheduleChangeAsync(Guid changeId, string providerSubscriptionRef, string targetPriceRef,
        DateTimeOffset effectiveAt, CancellationToken ct) =>
        Once($"change:{changeId}", () =>
        {
            UpdateSubscription(providerSubscriptionRef, s => s with { PendingPriceRef = targetPriceRef, PendingFrom = effectiveAt });
            return new ProviderChangeSchedule($"sim_sch_{changeId:N}", null); // the simulator reprices in place
        });

    public Task CancelScheduledChangeAsync(Guid changeId, string providerSubscriptionRef, string? newSubscriptionRef, CancellationToken ct) =>
        Once($"change-cancel:{changeId}", () =>
        {
            UpdateSubscription(providerSubscriptionRef, s => s with { PendingPriceRef = null, PendingFrom = null });
            return "cancelled";
        });

    public Task CancelAtPeriodEndAsync(Guid subscriptionId, long revision, string providerSubscriptionRef, CancellationToken ct) =>
        Once($"cancel:{subscriptionId}:{revision}", () =>
        {
            UpdateSubscription(providerSubscriptionRef, s => s with { CancelAtPeriodEnd = true, PendingPriceRef = null, PendingFrom = null });
            return "cancel";
        });

    public Task ResumeAsync(Guid subscriptionId, long revision, string providerSubscriptionRef, CancellationToken ct) =>
        Once($"resume:{subscriptionId}:{revision}", () =>
        {
            UpdateSubscription(providerSubscriptionRef, s => s with { CancelAtPeriodEnd = false });
            return "resume";
        });

    public Task<ProviderEventPage> ListEventsSinceAsync(string? cursor, int limit, CancellationToken ct) =>
        Read(() =>
        {
            var after = cursor is null ? 0 : long.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            List<(long Seq, SimEvent Event)> page;
            lock (_log)
                page = _log.Where(e => e.Seq > after).OrderBy(e => e.Seq).Take(limit).ToList();
            return new ProviderEventPage(page.Select(e => new ProviderEventRecord(e.Event.EventId, e.Event.Body)).ToList(),
                page.Count == 0 ? cursor : page[^1].Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

    public Task<ProviderSubscriptionState?> FetchSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct) =>
        Read(() => _subscriptions.TryGetValue(providerSubscriptionRef, out var s)
            ? new ProviderSubscriptionState(s.Ref, s.Status, s.PeriodEnd, s.CancelAtPeriodEnd)
            : null);

    private Task<T> Read<T>(Func<T> read)
    {
        Interlocked.Increment(ref _calls);
        if (Outage)
            throw new BillingProviderUnavailableException("[SIMULATED BILLING] outage");
        return Task.FromResult(read());
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

/// <summary>A simulated hosted checkout; only the console paying it (a signed event) completes our session.</summary>
public sealed record SimCheckout(string SessionRef, Guid CheckoutSessionId, string CustomerRef, string PriceRef, DateTimeOffset ExpiresAt);
