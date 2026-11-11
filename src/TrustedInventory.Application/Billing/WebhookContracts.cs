using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Billing;

/// <summary>What the inbox needs from a verified event before anything else is parsed (BIL-004).</summary>
public sealed record WebhookEnvelope(string EventId, string EventType, DateTimeOffset? CreatedAt);

/// <summary>Signature missing, wrong or outside the replay window: 401, nothing stored.</summary>
public sealed class WebhookSignatureException(string message) : Exception(message);

/// <summary>Verified but not an event we can read: 400, nothing stored.</summary>
public sealed class WebhookPayloadException(string message) : Exception(message);

/// <summary>The inbox could not store the event: 503 so the provider retries.</summary>
public sealed class WebhookStorageException(string message, Exception inner) : Exception(message, inner);

public enum BillingEventKind
{
    InvoicePaid,
    InvoicePaymentFailed,
    DisputeOpened,
    ChargebackRecorded,
    PaymentRefunded,
    SubscriptionEnded,
    Unsupported,
}

/// <summary>A provider invoice as the event reports it; amounts in minor units (BIL-001).</summary>
public sealed record ProviderInvoice(
    string Ref,
    string? Number,
    string Currency,
    short Exponent,
    IReadOnlyList<InvoiceLineSnapshot> Lines,
    long Subtotal,
    long Discount,
    long Tax,
    long Total,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    InvoiceStatus Status,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? PaidAt,
    long Revision,
    bool Initial);

public sealed record ProviderPayment(string Ref, PaymentStatus Status, long Amount, DateTimeOffset OccurredAt, string? RefundOfRef);

public sealed record ProviderDispute(string Ref, string PaymentRef, long Amount, string Reason);

/// <summary>
/// A verified event in provider-neutral form (DEC-064). References are the provider's; the tenant is resolved from our own
/// records, never from the payload.
/// </summary>
public sealed record BillingEvent(
    string EventId,
    string EventType,
    BillingEventKind Kind,
    DateTimeOffset? CreatedAt,
    string? CustomerRef,
    string? SubscriptionRef,
    long? SubscriptionRevision,
    string? CheckoutSessionRef,
    ProviderInvoice? Invoice,
    ProviderPayment? Payment,
    ProviderDispute? Dispute,
    string? PriceRef = null);

/// <summary>Provider-specific signature checking and event parsing (docs/billing-adapter.md, VerifyWebhook).</summary>
public interface IBillingWebhooks
{
    /// <summary>Provider key in the route, e.g. "simulator".</summary>
    string Code { get; }

    /// <summary>Verifies the signature over the raw bytes, then reads the envelope. Throws the exceptions above.</summary>
    WebhookEnvelope Verify(ReadOnlySpan<byte> rawBody, Func<string, string?> header, string? remoteAddress);

    /// <summary>Parses a stored, previously verified body into the neutral form.</summary>
    BillingEvent Parse(ReadOnlySpan<byte> rawBody);

    /// <summary>
    /// Completes a parsed event from the provider's authoritative API where the event alone is not enough (BIL-005, DEC-068). Called by the
    /// processor before it locks the inbox row; a provider failure is a processing failure with backoff.
    /// </summary>
    Task<BillingEvent> ResolveAsync(BillingEvent parsed, CancellationToken ct);
}

public interface IWebhookInbox
{
    /// <summary>Stores the event durably; returns false when (provider, event id) was already stored.</summary>
    Task<bool> StoreAsync(string provider, WebhookEnvelope envelope, byte[] rawBody, CancellationToken ct);
}

public sealed record WebhookAck(bool Received, bool Duplicate);

/// <summary>
/// POST /webhooks/billing/{provider} (BIL-004, DEC-064): verify over the raw body, store, then acknowledge. Effects happen
/// later in the worker, so a 200 means only "stored".
/// </summary>
public sealed class WebhookService(IBillingWebhooks webhooks, IWebhookInbox inbox)
{
    public const int MaxBodyBytes = 256 * 1024;

    public async Task<WebhookAck> ReceiveAsync(string provider, byte[] rawBody, Func<string, string?> header, string? remoteAddress, CancellationToken ct)
    {
        if (!string.Equals(provider, webhooks.Code, StringComparison.Ordinal))
            throw new AppException(ErrorCode.Unauthenticated, "No verifier is configured for this provider.");
        if (rawBody.Length > MaxBodyBytes)
            throw new AppException(ErrorCode.MalformedRequest, "The event body is too large.");

        WebhookEnvelope envelope;
        try
        {
            envelope = webhooks.Verify(rawBody, header, remoteAddress);
        }
        catch (WebhookSignatureException e)
        {
            throw new AppException(ErrorCode.Unauthenticated, e.Message);
        }
        catch (WebhookPayloadException e)
        {
            throw new AppException(ErrorCode.MalformedRequest, e.Message);
        }

        try
        {
            var stored = await inbox.StoreAsync(provider, envelope, rawBody, ct);
            return new WebhookAck(true, Duplicate: !stored);
        }
        catch (WebhookStorageException)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, "The event could not be stored. Retry later.");
        }
    }
}
