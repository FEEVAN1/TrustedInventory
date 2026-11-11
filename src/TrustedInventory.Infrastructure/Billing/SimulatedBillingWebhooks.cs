using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrustedInventory.Application.Billing;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>Simulator webhook secrets (DEC-064). The previous secret is accepted during rotation only.</summary>
public sealed record SimulatorSigning(string Current, string? Previous)
{
    public const int MinLength = 32;
}

/// <summary>A signed simulator event ready to deliver: the exact bytes and the signature header value.</summary>
public sealed record SimEvent(string EventId, string Type, byte[] Body, string Signature);

public sealed record SimPrice(string Ref, string Currency, long Amount, short Exponent, BillingInterval Interval);

/// <summary>A simulated provider subscription (console state, this process only).</summary>
public sealed record SimSubscription(string Ref, string CustomerRef, string PriceRef, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd)
{
    public string? OpenInvoiceRef { get; init; }
    public DateTimeOffset? OpenInvoicePeriodEnd { get; init; }
    public string? PendingPriceRef { get; init; }
    public DateTimeOffset? PendingFrom { get; init; }
    public bool CancelAtPeriodEnd { get; init; }
}

/// <summary>Wire format of simulator events. Real providers get their own parser once D09 is decided.</summary>
public sealed record SimEventBody(string Id, string Type, DateTimeOffset Created, SimEventData Data);

public sealed record SimEventData(
    string? Customer,
    string? Subscription = null,
    long? SubscriptionRevision = null,
    string? CheckoutSession = null,
    SimInvoice? Invoice = null,
    SimPayment? Payment = null,
    SimDispute? Dispute = null);

public sealed record SimInvoice(string Id, string? Number, string Currency, short Exponent, IReadOnlyList<InvoiceLineSnapshot> Lines, long Subtotal,
    long Discount, long Tax, long Total, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd, InvoiceStatus Status, DateTimeOffset? IssuedAt,
    DateTimeOffset? DueAt, DateTimeOffset? PaidAt, long Revision, bool Initial);

public sealed record SimPayment(string Id, PaymentStatus Status, long Amount, DateTimeOffset OccurredAt, string? RefundOf = null);

public sealed record SimDispute(string Id, string Payment, long Amount, string Reason);

public sealed partial class SimulatedBillingProvider
{
    public const string SignatureHeader = "Simulator-Signature";
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, SimPrice> _prices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SimSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Customer, SimPayment Payment)> _payments = new(StringComparer.Ordinal);
    private long _revision;

    // ---- Verification (IBillingWebhooks, BIL-004) ----

    public WebhookEnvelope Verify(ReadOnlySpan<byte> rawBody, Func<string, string?> header)
    {
        var value = header(SignatureHeader) ?? throw new WebhookSignatureException("The event is not signed.");
        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal)
                && long.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                timestamp = t;
            else if (part.StartsWith("v1=", StringComparison.Ordinal) && part.Length == 3 + 64)
            {
                try
                {
                    signatures.Add(Convert.FromHexString(part.AsSpan(3)));
                }
                catch (FormatException)
                {
                    // An unreadable signature is simply not a match.
                }
            }
        }
        if (timestamp is null || signatures.Count == 0)
            throw new WebhookSignatureException("The signature header is incomplete.");
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if ((clock.GetUtcNow() - signedAt).Duration() > ReplayWindow)
            throw new WebhookSignatureException("The event timestamp is outside the replay window.");

        var matched = false;
        foreach (var secret in new[] { signing.Current, signing.Previous })
        {
            if (secret is null)
                continue;
            var expected = Mac(secret, timestamp.Value, rawBody);
            foreach (var candidate in signatures)
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        if (!matched)
            throw new WebhookSignatureException("The signature does not match.");

        var parsed = Parse(rawBody);
        return new WebhookEnvelope(parsed.EventId, parsed.EventType, parsed.CreatedAt);
    }

    public BillingEvent Parse(ReadOnlySpan<byte> rawBody)
    {
        SimEventBody? body;
        try
        {
            body = JsonSerializer.Deserialize<SimEventBody>(rawBody, Wire);
        }
        catch (JsonException)
        {
            throw new WebhookPayloadException("The event is not valid JSON in the simulator format.");
        }
        if (body is null || string.IsNullOrWhiteSpace(body.Id) || body.Id.Length > 200 || string.IsNullOrWhiteSpace(body.Type)
            || body.Type.Length > 100 || body.Data is null)
            throw new WebhookPayloadException("The event needs an id, a type, a creation time and data.");

        var d = body.Data;
        var kind = body.Type switch
        {
            "invoice.paid" => BillingEventKind.InvoicePaid,
            "invoice.payment_failed" => BillingEventKind.InvoicePaymentFailed,
            "charge.dispute.created" => BillingEventKind.DisputeOpened,
            "charge.dispute.funds_withdrawn" => BillingEventKind.ChargebackRecorded,
            "charge.refunded" => BillingEventKind.PaymentRefunded,
            "customer.subscription.deleted" => BillingEventKind.SubscriptionEnded,
            _ => BillingEventKind.Unsupported,
        };
        var invoice = d.Invoice is { } i
            ? new ProviderInvoice(i.Id, i.Number, i.Currency, i.Exponent, i.Lines ?? [], i.Subtotal, i.Discount, i.Tax, i.Total, i.PeriodStart, i.PeriodEnd,
                i.Status, i.IssuedAt, i.DueAt, i.PaidAt, i.Revision, i.Initial)
            : null;
        var payment = d.Payment is { } p ? new ProviderPayment(p.Id, p.Status, p.Amount, p.OccurredAt, p.RefundOf) : null;
        var dispute = d.Dispute is { } x ? new ProviderDispute(x.Id, x.Payment, x.Amount, x.Reason) : null;

        var complete = kind switch
        {
            BillingEventKind.InvoicePaid or BillingEventKind.InvoicePaymentFailed =>
                d.Customer is not null && invoice is not null && payment is not null
                && (invoice.Initial ? d.CheckoutSession is not null : d.Subscription is not null && d.SubscriptionRevision is not null)
                && (kind != BillingEventKind.InvoicePaid || (d.Subscription is not null && d.SubscriptionRevision is not null
                    && invoice.PeriodStart is not null && invoice.PeriodEnd > invoice.PeriodStart)),
            BillingEventKind.DisputeOpened or BillingEventKind.ChargebackRecorded => d.Customer is not null && dispute is not null,
            BillingEventKind.PaymentRefunded => d.Customer is not null && payment?.RefundOfRef is not null,
            BillingEventKind.SubscriptionEnded => d.Customer is not null && d.Subscription is not null && d.SubscriptionRevision is not null,
            _ => true,
        };
        if (!complete)
            throw new WebhookPayloadException($"The {body.Type} event is missing required references.");
        return new BillingEvent(body.Id, body.Type, kind, body.Created, d.Customer, d.Subscription, d.SubscriptionRevision, d.CheckoutSession, invoice,
            payment, dispute);
    }

    private static byte[] Mac(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }

    // ---- Test console: signed events (development and tests only; nothing is charged) ----

    public SimSubscription? Subscription(string reference) => _subscriptions.GetValueOrDefault(reference);

    /// <summary>The customer pays the hosted checkout: the initial invoice is paid and a provider subscription starts.</summary>
    public SimEvent PayCheckout(Guid checkoutSessionId)
    {
        var checkout = CheckoutFor(checkoutSessionId) ?? throw new InvalidOperationException("No simulated checkout for that session.");
        var price = PriceOf(checkout.PriceRef);
        var now = clock.GetUtcNow();
        // Paying the same checkout again repeats the same subscription and invoice (a provider resend under a new event id).
        var (subscription, invoiceRef) = _checkoutSubscriptions.TryGetValue(checkout.SessionRef, out var known)
            ? (_subscriptions[known.SubscriptionRef], known.InvoiceRef)
            : NewSubscription(checkout, price, now);
        var invoice = Invoice(price, subscription.PeriodStart, subscription.PeriodEnd, InvoiceStatus.Paid, now, initial: true, invoiceRef);
        var payment = Payment(checkout.CustomerRef, PaymentStatus.Succeeded, invoice.Total, now);
        return Build("invoice.paid", new SimEventData(checkout.CustomerRef, subscription.Ref, NextRevision(), checkout.SessionRef, invoice, payment));
    }

    /// <summary>The customer's card is declined on the hosted checkout: nothing starts (BIL-003).</summary>
    public SimEvent FailCheckout(Guid checkoutSessionId)
    {
        var checkout = CheckoutFor(checkoutSessionId) ?? throw new InvalidOperationException("No simulated checkout for that session.");
        var price = PriceOf(checkout.PriceRef);
        var now = clock.GetUtcNow();
        var invoice = Invoice(price, now, Advance(now, price.Interval), InvoiceStatus.Open, now, initial: true);
        var payment = Payment(checkout.CustomerRef, PaymentStatus.Failed, invoice.Total, now);
        return Build("invoice.payment_failed", new SimEventData(checkout.CustomerRef, CheckoutSession: checkout.SessionRef, Invoice: invoice, Payment: payment));
    }

    /// <summary>A renewal attempt fails; repeated calls retry the same open invoice.</summary>
    public SimEvent FailRenewal(string subscriptionRef)
    {
        var (sub, price, invoiceRef, periodEnd) = OpenRenewal(subscriptionRef);
        var now = clock.GetUtcNow();
        var invoice = Invoice(price, sub.PeriodEnd, periodEnd, InvoiceStatus.Open, now, initial: false, invoiceRef, dueAt: sub.PeriodEnd);
        var payment = Payment(sub.CustomerRef, PaymentStatus.Failed, invoice.Total, now);
        return Build("invoice.payment_failed", new SimEventData(sub.CustomerRef, sub.Ref, NextRevision(), Invoice: invoice, Payment: payment));
    }

    /// <summary>The renewal invoice (the open one, if a failure left one) is paid and the period advances.</summary>
    public SimEvent PayRenewal(string subscriptionRef)
    {
        var (sub, price, invoiceRef, periodEnd) = OpenRenewal(subscriptionRef);
        var now = clock.GetUtcNow();
        var invoice = Invoice(price, sub.PeriodEnd, periodEnd, InvoiceStatus.Paid, now, initial: false, invoiceRef, dueAt: sub.PeriodEnd);
        var payment = Payment(sub.CustomerRef, PaymentStatus.Succeeded, invoice.Total, now);
        _subscriptions[sub.Ref] = sub with
        {
            PeriodStart = sub.PeriodEnd, PeriodEnd = periodEnd, OpenInvoiceRef = null, OpenInvoicePeriodEnd = null, PriceRef = price.Ref,
            PendingPriceRef = price.Ref == sub.PendingPriceRef ? null : sub.PendingPriceRef, PendingFrom = price.Ref == sub.PendingPriceRef ? null : sub.PendingFrom,
        };
        return Build("invoice.paid", new SimEventData(sub.CustomerRef, sub.Ref, NextRevision(), Invoice: invoice, Payment: payment));
    }

    /// <summary>The cardholder disputes a payment; <paramref name="fundsWithdrawn"/> is the chargeback itself.</summary>
    public SimEvent Dispute(string paymentRef, bool fundsWithdrawn = false)
    {
        var (customer, payment) = _payments.GetValueOrDefault(paymentRef);
        if (customer is null)
            throw new InvalidOperationException("No simulated payment with that reference.");
        var dispute = new SimDispute($"sim_dp_{Hash(paymentRef)}", paymentRef, payment.Amount, "Synthetic dispute");
        return Build(fundsWithdrawn ? "charge.dispute.funds_withdrawn" : "charge.dispute.created", new SimEventData(customer, Dispute: dispute));
    }

    /// <summary>The provider refunds a payment in full.</summary>
    public SimEvent Refund(string paymentRef)
    {
        var (customer, payment) = _payments.GetValueOrDefault(paymentRef);
        if (customer is null)
            throw new InvalidOperationException("No simulated payment with that reference.");
        var refund = new SimPayment($"sim_re_{Guid.NewGuid():N}", PaymentStatus.Refunded, payment.Amount, clock.GetUtcNow(), paymentRef);
        return Build("charge.refunded", new SimEventData(customer, Payment: refund));
    }

    /// <summary>The paid period ends after a cancellation: the provider ends the subscription.</summary>
    public SimEvent EndSubscription(string subscriptionRef)
    {
        var sub = _subscriptions.GetValueOrDefault(subscriptionRef) ?? throw new InvalidOperationException("No simulated subscription with that reference.");
        return Build("customer.subscription.deleted", new SimEventData(sub.CustomerRef, sub.Ref, NextRevision()));
    }

    private void UpdateSubscription(string reference, Func<SimSubscription, SimSubscription> change)
    {
        if (_subscriptions.TryGetValue(reference, out var sub))
            _subscriptions[reference] = change(sub);
    }

    /// <summary>Any event, for unknown-reference and unsupported-type cases.</summary>
    public SimEvent Build(string type, SimEventData data)
    {
        var id = $"evt_sim_{Guid.NewGuid():N}";
        var body = JsonSerializer.SerializeToUtf8Bytes(new SimEventBody(id, type, clock.GetUtcNow(), data), Wire);
        return Sign(id, type, body, clock.GetUtcNow(), signing.Current);
    }

    /// <summary>The provider sends the same event content again under a new event id (a resend, not a byte-for-byte duplicate).</summary>
    public SimEvent Resend(SimEvent original)
    {
        var body = JsonSerializer.Deserialize<SimEventBody>(original.Body, Wire)!;
        var id = $"evt_sim_{Guid.NewGuid():N}";
        return Sign(id, body.Type, JsonSerializer.SerializeToUtf8Bytes(body with { Id = id }, Wire), clock.GetUtcNow(), signing.Current);
    }

    /// <summary>Re-signs the same bytes at another time or with another secret (replay window and forgery tests).</summary>
    public static SimEvent Sign(string eventId, string type, byte[] body, DateTimeOffset at, string secret)
    {
        var t = at.ToUnixTimeSeconds();
        return new SimEvent(eventId, type, body, $"t={t.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(Mac(secret, t, body))}");
    }

    private readonly ConcurrentDictionary<string, (string SubscriptionRef, string InvoiceRef)> _checkoutSubscriptions = new(StringComparer.Ordinal);

    private (SimSubscription Subscription, string InvoiceRef) NewSubscription(SimCheckout checkout, SimPrice price, DateTimeOffset now)
    {
        var sub = new SimSubscription($"sim_sub_{Guid.NewGuid():N}", checkout.CustomerRef, price.Ref, now, Advance(now, price.Interval));
        var invoiceRef = $"sim_in_{Guid.NewGuid():N}";
        _subscriptions[sub.Ref] = sub;
        _checkoutSubscriptions[checkout.SessionRef] = (sub.Ref, invoiceRef);
        return (sub, invoiceRef);
    }

    private (SimSubscription Sub, SimPrice Price, string InvoiceRef, DateTimeOffset PeriodEnd) OpenRenewal(string subscriptionRef)
    {
        var sub = _subscriptions.GetValueOrDefault(subscriptionRef) ?? throw new InvalidOperationException("No simulated subscription with that reference.");
        // A scheduled change takes effect with the renewal that starts at or after its date (DEC-065).
        var price = PriceOf(sub.PendingPriceRef is { } pending && sub.PendingFrom <= sub.PeriodEnd ? pending : sub.PriceRef);
        if (sub.OpenInvoiceRef is null)
        {
            sub = sub with { OpenInvoiceRef = $"sim_in_{Guid.NewGuid():N}", OpenInvoicePeriodEnd = Advance(sub.PeriodEnd, price.Interval) };
            _subscriptions[sub.Ref] = sub;
        }
        return (sub, price, sub.OpenInvoiceRef!, sub.OpenInvoicePeriodEnd!.Value);
    }

    private SimInvoice Invoice(SimPrice price, DateTimeOffset start, DateTimeOffset end, InvoiceStatus status, DateTimeOffset now, bool initial,
        string? reference = null, DateTimeOffset? dueAt = null)
    {
        reference ??= $"sim_in_{Guid.NewGuid():N}";
        var line = new InvoiceLineSnapshot("Subscription (simulated)", 1, price.Amount, 0, 0, price.Amount, price.Amount);
        return new SimInvoice(reference, "SIM-" + reference[^8..].ToUpperInvariant(), price.Currency, price.Exponent, [line], price.Amount, 0, 0, price.Amount,
            start, end, status, now, dueAt ?? now, status == InvoiceStatus.Paid ? now : null, NextRevision(), initial);
    }

    private SimPayment Payment(string customer, PaymentStatus status, long amount, DateTimeOffset now)
    {
        var payment = new SimPayment($"sim_py_{Guid.NewGuid():N}", status, amount, now);
        _payments[payment.Id] = (customer, payment);
        return payment;
    }

    private SimPrice PriceOf(string reference) =>
        _prices.GetValueOrDefault(reference) ?? throw new InvalidOperationException($"Price {reference} was not registered with this simulator instance.");

    private static DateTimeOffset Advance(DateTimeOffset from, BillingInterval interval) =>
        interval == BillingInterval.Year ? from.AddYears(1) : from.AddMonths(1);

    private long NextRevision() => Interlocked.Increment(ref _revision);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
