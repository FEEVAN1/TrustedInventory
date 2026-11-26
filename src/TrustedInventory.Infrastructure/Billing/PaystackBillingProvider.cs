using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>Paystack settings (DEC-068). The secret key comes from the vault or environment, never the repository.</summary>
public sealed record PaystackOptions(string SecretKey, Uri BaseUrl, IReadOnlySet<string> WebhookSourceIps)
{
    public const string Code = "paystack";
    public static readonly Uri DefaultBaseUrl = new("https://api.paystack.co/");
}

/// <summary>Lets tests and the labelled Paystack simulator replace the HTTP transport; production uses the default handler.</summary>
public sealed record PaystackTransport(HttpMessageHandler Handler);

/// <summary>
/// Paystack adapter (owner decision D09, DEC-068), test mode only until live keys are separately approved. Amounts are kobo (NGN,
/// exponent 2). Card data stays with Paystack (BIL-002). Paystack cannot reprice a subscription, so a plan change replaces it.
/// </summary>
public sealed partial class PaystackBillingProvider : IBillingProvider, IBillingWebhooks, IDisposable
{
    public const string SignatureHeader = "x-paystack-signature";
    public const string CheckoutReferencePrefix = "ti-cs-";
    public static readonly TimeSpan PortalLifetime = TimeSpan.FromMinutes(5);

    private readonly PaystackOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaystackBillingProvider> _log;
    private readonly HttpClient _http;

    public PaystackBillingProvider(PaystackOptions options, TimeProvider clock, ILogger<PaystackBillingProvider> log, PaystackTransport? transport = null)
    {
        _options = options;
        _clock = clock;
        _log = log;
        _http = transport is null ? new HttpClient() : new HttpClient(transport.Handler, disposeHandler: false);
        _http.BaseAddress = options.BaseUrl;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
    }

    public string Code => PaystackOptions.Code;

    public bool Supports(string currency) => currency == Money.Naira;

    public void Dispose() => _http.Dispose();

    // ---- Adapter ----

    public async Task<string> EnsureCustomerAsync(ProviderCustomerRequest request, CancellationToken ct)
    {
        // Paystack keys customers by email: creating the same email again returns the same customer.
        var data = await SendAsync(HttpMethod.Post, "customer", new JsonObject
        {
            ["email"] = request.ContactEmail,
            ["first_name"] = request.LegalName,
            ["metadata"] = new JsonObject { ["billing_account_id"] = request.BillingAccountId.ToString() },
        }, ct);
        return Str(data, "customer_code");
    }

    public async Task<string> EnsurePriceAsync(Guid priceVersionId, MoneyDto amount, BillingInterval interval, CancellationToken ct)
    {
        if (!Supports(amount.Currency) || amount.Exponent != 2)
            throw new BillingProviderUnavailableException("[PAYSTACK] Only NGN prices in kobo can be charged.");
        var name = $"TI {priceVersionId}";
        var wire = interval == BillingInterval.Year ? "annually" : "monthly";
        // A plan created by a lost earlier call is found by its name, so a retry never creates a second plan.
        var existing = await SendAsync(HttpMethod.Get, $"plan?perPage=100&interval={wire}&amount={amount.MinorUnits.ToString(CultureInfo.InvariantCulture)}",
            null, ct);
        foreach (var plan in existing.AsArray())
            if ((string?)plan?["name"] == name)
                return Str(plan!, "plan_code");
        var created = await SendAsync(HttpMethod.Post, "plan", new JsonObject
        {
            ["name"] = name, ["amount"] = amount.MinorUnits, ["interval"] = wire, ["currency"] = Money.Naira,
        }, ct);
        return Str(created, "plan_code");
    }

    public async Task<ProviderCheckout> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken ct)
    {
        var reference = CheckoutReferencePrefix + request.CheckoutSessionId.ToString("N");
        JsonNode data;
        try
        {
            data = await SendAsync(HttpMethod.Post, "transaction/initialize", new JsonObject
            {
                ["email"] = request.CustomerEmail,
                ["amount"] = request.Amount.MinorUnits,
                ["currency"] = Money.Naira,
                ["plan"] = request.ProviderPriceRef,
                ["reference"] = reference,
                ["callback_url"] = request.ReturnUrl,
                ["metadata"] = new JsonObject { ["checkout_session_id"] = request.CheckoutSessionId.ToString() },
            }, ct);
        }
        catch (PaystackRefusedException e) when (e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
        {
            // Paystack never reissues a reference, so a checkout whose first response was lost cannot be resumed (DEC-068).
            throw new BillingProviderUnavailableException("[PAYSTACK] This checkout was already started at Paystack; start a new checkout.");
        }
        return new ProviderCheckout(Str(data, "reference"), Str(data, "authorization_url"), request.ExpiresAt);
    }

    public async Task<ProviderPortal> CreatePortalSessionAsync(Guid billingAccountId, string providerCustomerId, string returnUrl, CancellationToken ct)
    {
        var subscription = await CurrentSubscriptionAsync(providerCustomerId, null, ct)
            ?? throw new BillingProviderUnavailableException("[PAYSTACK] There is no Paystack subscription to manage yet.");
        var link = await SendAsync(HttpMethod.Get, $"subscription/{Uri.EscapeDataString(Str(subscription, "subscription_code"))}/manage/link", null, ct);
        return new ProviderPortal(Str(link, "link"), _clock.GetUtcNow() + PortalLifetime);
    }

    public async Task<ProviderChangeSchedule> ScheduleChangeAsync(Guid changeId, string providerSubscriptionRef, string targetPriceRef,
        DateTimeOffset effectiveAt, CancellationToken ct)
    {
        var current = await SendAsync(HttpMethod.Get, $"subscription/{Uri.EscapeDataString(providerSubscriptionRef)}", null, ct);
        var customer = Str(current["customer"]!, "customer_code");
        // A replacement created by a lost earlier call is reused, so a retry never creates a second one.
        var replacement = await CurrentSubscriptionAsync(customer, targetPriceRef, ct, includeFuture: true);
        var code = replacement is null
            ? Str(await SendAsync(HttpMethod.Post, "subscription", new JsonObject
            {
                ["customer"] = customer,
                ["plan"] = targetPriceRef,
                ["start_date"] = effectiveAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            }, ct), "subscription_code")
            : Str(replacement, "subscription_code");
        await ToggleAsync(providerSubscriptionRef, enable: false, ct);
        return new ProviderChangeSchedule(code, code);
    }

    public async Task CancelScheduledChangeAsync(Guid changeId, string providerSubscriptionRef, string? newSubscriptionRef, CancellationToken ct)
    {
        if (newSubscriptionRef is not null)
            await ToggleAsync(newSubscriptionRef, enable: false, ct);
        await ToggleAsync(providerSubscriptionRef, enable: true, ct);
    }

    public Task CancelAtPeriodEndAsync(Guid subscriptionId, long revision, string providerSubscriptionRef, CancellationToken ct) =>
        ToggleAsync(providerSubscriptionRef, enable: false, ct);

    public Task ResumeAsync(Guid subscriptionId, long revision, string providerSubscriptionRef, CancellationToken ct) =>
        ToggleAsync(providerSubscriptionRef, enable: true, ct);

    /// <summary>Paystack has no event-list API; convergence relies on its webhook retries plus the comparison (DEC-068).</summary>
    public Task<ProviderEventPage> ListEventsSinceAsync(string? cursor, int limit, CancellationToken ct) =>
        Task.FromResult(new ProviderEventPage([], cursor));

    public async Task<ProviderSubscriptionState?> FetchSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct)
    {
        JsonNode data;
        try
        {
            data = await SendAsync(HttpMethod.Get, $"subscription/{Uri.EscapeDataString(providerSubscriptionRef)}", null, ct);
        }
        catch (PaystackRefusedException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
        var status = (string?)data["status"];
        var state = status switch
        {
            "active" or "non-renewing" => SubscriptionState.Active,
            "attention" => SubscriptionState.PastDue,
            _ => SubscriptionState.Cancelled,
        };
        return new ProviderSubscriptionState(providerSubscriptionRef, state, Time(data["next_payment_date"]), status == "non-renewing");
    }

    // ---- Webhooks (BIL-004) ----

    public WebhookEnvelope Verify(ReadOnlySpan<byte> rawBody, Func<string, string?> header, string? remoteAddress)
    {
        if (_options.WebhookSourceIps.Count > 0 && (remoteAddress is null || !_options.WebhookSourceIps.Contains(remoteAddress)))
            throw new WebhookSignatureException("The event did not come from a Paystack source address.");
        var signature = header(SignatureHeader);
        if (signature is null || signature.Length != 128)
            throw new WebhookSignatureException("The event is not signed.");
        byte[] given;
        try
        {
            given = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            throw new WebhookSignatureException("The signature is malformed.");
        }
        if (!CryptographicOperations.FixedTimeEquals(Sign(_options.SecretKey, rawBody), given))
            throw new WebhookSignatureException("The signature does not match.");
        var parsed = Parse(rawBody);
        return new WebhookEnvelope(parsed.EventId, parsed.EventType, parsed.CreatedAt);
    }

    /// <summary>HMAC-SHA512 of the raw body with the secret key, as Paystack signs webhooks.</summary>
    public static byte[] Sign(string secretKey, ReadOnlySpan<byte> rawBody) => HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey), rawBody);

    public BillingEvent Parse(ReadOnlySpan<byte> rawBody)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(rawBody) ?? throw new WebhookPayloadException("The event is empty.");
        }
        catch (JsonException)
        {
            throw new WebhookPayloadException("The event is not valid JSON.");
        }
        if (root is not JsonObject || (string?)root["event"] is not { Length: > 0 and <= 100 } type || root["data"] is not JsonObject data
            || data["id"] is null)
            throw new WebhookPayloadException("A Paystack event needs an event name and data with an id.");
        var eventId = $"{type}:{data["id"]}";
        if (eventId.Length > 200)
            throw new WebhookPayloadException("The event id is too long.");
        var created = Time(data["paid_at"]) ?? Time(data["updatedAt"]) ?? Time(data["updated_at"]) ?? Time(data["createdAt"]) ?? Time(data["created_at"]);
        var revision = (created ?? DateTimeOffset.UnixEpoch).ToUnixTimeMilliseconds();
        var customer = (string?)data["customer"]?["customer_code"];
        try
        {
            switch (type)
            {
                case "charge.success" when ((string?)data["reference"])?.StartsWith(CheckoutReferencePrefix, StringComparison.Ordinal) == true:
                {
                    var reference = (string)data["reference"]!;
                    var amount = Long(data, "amount");
                    var paidAt = Time(data["paid_at"]) ?? throw new WebhookPayloadException("A successful charge needs paid_at.");
                    var invoice = new ProviderInvoice(reference, reference, Currency(data), 2, [Line((string?)data["plan"]?["name"], amount)], amount, 0, 0, amount,
                        paidAt, null, InvoiceStatus.Paid, paidAt, paidAt, paidAt, revision, Initial: true);
                    return new BillingEvent(eventId, type, BillingEventKind.InvoicePaid, created, Need(customer), null, revision, reference, invoice,
                        new ProviderPayment(reference, PaymentStatus.Succeeded, amount, paidAt, null), null,
                        Need((string?)data["plan"]?["plan_code"]));
                }
                case "invoice.update" when (bool?)data["paid"] == true || (string?)data["status"] == "success":
                case "invoice.payment_failed":
                {
                    var paid = type == "invoice.update";
                    var amount = Long(data, "amount");
                    var code = Need((string?)data["invoice_code"]);
                    var start = Time(data["period_start"]);
                    var paidAt = paid ? Time(data["paid_at"]) ?? created : null;
                    var invoice = new ProviderInvoice(code, code, Currency(data), 2, [Line(null, amount)], amount, 0, 0, amount, start, Time(data["period_end"]),
                        paid ? InvoiceStatus.Paid : InvoiceStatus.Open, Time(data["createdAt"]) ?? created, start, paidAt, revision, Initial: false);
                    var paymentRef = paid ? (string?)data["transaction"]?["reference"] ?? $"{code}:paid" : $"{code}:failed:{revision}";
                    return new BillingEvent(eventId, type, paid ? BillingEventKind.InvoicePaid : BillingEventKind.InvoicePaymentFailed, created, Need(customer),
                        Need((string?)data["subscription"]?["subscription_code"]), revision, null, invoice,
                        new ProviderPayment(paymentRef, paid ? PaymentStatus.Succeeded : PaymentStatus.Failed, amount, created ?? _clock.GetUtcNow(), null), null);
                }
                case "subscription.disable":
                    return new BillingEvent(eventId, type, BillingEventKind.SubscriptionEnded, created, Need(customer), Need((string?)data["subscription_code"]),
                        revision, null, null, null, null);
                case "charge.dispute.create":
                    return new BillingEvent(eventId, type, BillingEventKind.DisputeOpened, created, Need(customer), null, null, null, null, null,
                        new ProviderDispute($"dispute-{data["id"]}", Need((string?)data["transaction"]?["reference"]), Long(data, "refund_amount", "amount"),
                            (string?)data["category"] ?? "dispute"));
                case "refund.processed":
                    return new BillingEvent(eventId, type, BillingEventKind.PaymentRefunded, created, Need(customer), null, null, null, null,
                        new ProviderPayment($"refund-{data["id"]}", PaymentStatus.Refunded, Long(data, "amount"), created ?? _clock.GetUtcNow(),
                            Need((string?)data["transaction_reference"])), null);
                default:
                    return new BillingEvent(eventId, type, BillingEventKind.Unsupported, created, customer, null, null, null, null, null, null);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException)
        {
            throw new WebhookPayloadException($"The {type} event is missing required fields.");
        }
    }

    /// <summary>
    /// The initial charge does not name the subscription Paystack creates for it: fetch it (code and next payment date) from the
    /// customer and plan (BIL-005, DEC-068). Until Paystack has created it, processing retries with backoff.
    /// </summary>
    public async Task<BillingEvent> ResolveAsync(BillingEvent parsed, CancellationToken ct)
    {
        if (parsed is not { Kind: BillingEventKind.InvoicePaid, Invoice.Initial: true, SubscriptionRef: null, PriceRef: { } plan, CustomerRef: { } customer })
            return parsed;
        var subscription = await CurrentSubscriptionAsync(customer, plan, ct)
            ?? throw new InvalidOperationException("Paystack has not created the subscription for this charge yet.");
        var periodEnd = Time(subscription["next_payment_date"]) ?? throw new InvalidOperationException("The Paystack subscription has no next payment date.");
        return parsed with
        {
            SubscriptionRef = Str(subscription, "subscription_code"),
            Invoice = parsed.Invoice with { PeriodEnd = periodEnd },
        };
    }

    // ---- HTTP ----

    private async Task<JsonObject?> CurrentSubscriptionAsync(string customerCode, string? planCode, CancellationToken ct, bool includeFuture = false)
    {
        var customer = await SendAsync(HttpMethod.Get, $"customer/{Uri.EscapeDataString(customerCode)}", null, ct);
        var query = $"subscription?perPage=100&customer={Long(customer, "id").ToString(CultureInfo.InvariantCulture)}";
        if (planCode is not null)
            query += $"&plan={Long(await SendAsync(HttpMethod.Get, $"plan/{Uri.EscapeDataString(planCode)}", null, ct), "id").ToString(CultureInfo.InvariantCulture)}";
        var list = await SendAsync(HttpMethod.Get, query, null, ct);
        return list.AsArray().OfType<JsonObject>()
            .Where(s => (string?)s["status"] is "active" or "non-renewing" or "attention" || (includeFuture && (string?)s["status"] == "pending"))
            .OrderByDescending(s => Time(s["createdAt"]) ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private async Task ToggleAsync(string subscriptionCode, bool enable, CancellationToken ct)
    {
        var subscription = await SendAsync(HttpMethod.Get, $"subscription/{Uri.EscapeDataString(subscriptionCode)}", null, ct);
        try
        {
            await SendAsync(HttpMethod.Post, enable ? "subscription/enable" : "subscription/disable",
                new JsonObject { ["code"] = subscriptionCode, ["token"] = Str(subscription, "email_token") }, ct);
        }
        catch (PaystackRefusedException e) when (e.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // Repeating an enable or disable is a no-op, which keeps our retries idempotent.
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        var logPath = PathOnly(path); // never the query: it can name customers
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogCall(_log, method.Method, logPath, 0);
            throw new BillingProviderUnavailableException("[PAYSTACK] unreachable");
        }
        using (response)
        {
            LogCall(_log, method.Method, logPath, (int)response.StatusCode);
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                // handled below
            }
            if ((int)response.StatusCode >= 500 || json is null)
                throw new BillingProviderUnavailableException($"[PAYSTACK] {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode || (bool?)json["status"] != true)
                throw new PaystackRefusedException(response.StatusCode, (string?)json["message"] ?? "refused");
            return json["data"] ?? new JsonObject();
        }
    }

    private static string PathOnly(string path) => path.Split('?')[0];

    private static string Str(JsonNode node, string name) =>
        (string?)node[name] is { Length: > 0 } value ? value : throw new BillingProviderUnavailableException($"[PAYSTACK] response without {name}");

    private static string Need(string? value) => value is { Length: > 0 } v ? v : throw new InvalidOperationException("missing field");

    private static long Long(JsonNode node, params string[] names)
    {
        foreach (var name in names)
            if (node[name] is JsonValue v && v.TryGetValue<long>(out var l))
                return l;
        throw new InvalidOperationException("missing amount");
    }

    private static string Currency(JsonNode data) =>
        (string?)data["currency"] is { Length: 3 } c ? c : Money.Naira;

    private static InvoiceLineSnapshot Line(string? planName, long amount) =>
        new(string.IsNullOrWhiteSpace(planName) ? "Subscription" : $"Subscription ({planName})", 1, amount, 0, 0, amount, amount);

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "[PAYSTACK TEST MODE] {Method} {Path} -> {Status}")]
    private static partial void LogCall(ILogger logger, string method, string path, int status);
}

/// <summary>Paystack answered but refused the request (4xx or status false). The message is Paystack's, never a secret.</summary>
public sealed class PaystackRefusedException(HttpStatusCode status, string message) : BillingProviderUnavailableException(message)
{
    public HttpStatusCode Status { get; } = status;
}
