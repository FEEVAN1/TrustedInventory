using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TrustedInventory.Infrastructure.Billing;

namespace TrustedInventory.IntegrationTests.Support;

/// <summary>A signed Paystack-format webhook: the exact bytes and the x-paystack-signature value.</summary>
public sealed record PaystackEvent(string Event, byte[] Body, string Signature);

/// <summary>
/// [SIMULATED PAYSTACK] An in-process stand-in for the Paystack API endpoints the adapter uses (DEC-068), with a console that produces
/// Paystack-format webhooks signed with HMAC-SHA512. Synthetic data only; it lives in the test project so no stack can use it. Its
/// behaviour follows Paystack's documented responses; the real test-mode run (docs/paystack-sandbox.md) is the check that it does.
/// </summary>
public sealed class SimulatedPaystackApi(string secretKey) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, JsonObject> _customers = new();
    private readonly ConcurrentDictionary<string, JsonObject> _plans = new();
    private readonly ConcurrentDictionary<string, JsonObject> _transactions = new();
    private readonly ConcurrentDictionary<string, JsonObject> _subscriptions = new();
    private readonly object _gate = new();
    private long _ids = Random.Shared.NextInt64(1, 1_000_000) * 1_000_000; // distinct codes per instance: tests share one database

    public string SecretKey { get; } = secretKey;

    /// <summary>"METHOD /path" of every call, for evidence.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>While set, every call fails as if Paystack were unreachable.</summary>
    public bool Outage { get; set; }

    public JsonObject? Subscription(string code) => _subscriptions.GetValueOrDefault(code);

    public IEnumerable<JsonObject> SubscriptionsOf(string customerCode) =>
        _subscriptions.Values.Where(s => (string?)s["customer"]!["customer_code"] == customerCode);

    public JsonObject? Transaction(string reference) => _transactions.GetValueOrDefault(reference);

    // ---- Console ----

    /// <summary>The customer pays the hosted page: the charge succeeds and Paystack starts the plan's subscription.</summary>
    public PaystackEvent Pay(string reference)
    {
        var tx = _transactions[reference];
        var now = DateTimeOffset.UtcNow;
        tx["status"] = "success";
        tx["paid_at"] = Iso(now);
        var plan = _plans[(string)tx["plan"]!];
        var customer = _customers.Values.Single(c => (string?)c["email"] == (string?)tx["email"]);
        NewSubscription(customer, plan, now, "active");
        return Sign("charge.success", new JsonObject
        {
            ["id"] = (long)tx["id"]!, ["reference"] = reference, ["amount"] = (long)tx["amount"]!, ["currency"] = "NGN", ["status"] = "success",
            ["paid_at"] = Iso(now), ["created_at"] = tx["created_at"]!.DeepClone(),
            ["customer"] = Customer(customer), ["plan"] = new JsonObject { ["plan_code"] = plan["plan_code"]!.DeepClone(), ["name"] = plan["name"]!.DeepClone() },
            ["authorization"] = new JsonObject { ["authorization_code"] = "AUTH_sim", ["reusable"] = true },
            ["metadata"] = tx["metadata"]!.DeepClone(),
        });
    }

    /// <summary>A renewal charge: invoice.update (paid) on success, invoice.payment_failed otherwise.</summary>
    public PaystackEvent Renew(string subscriptionCode, bool success)
    {
        var sub = _subscriptions[subscriptionCode];
        var plan = _plans.Values.Single(p => (string?)p["plan_code"] == (string?)sub["plan"]!["plan_code"]);
        var start = Time(sub["next_payment_date"]!);
        var end = Advance(start, (string)plan["interval"]!);
        var invoiceCode = $"INV_{subscriptionCode}_{start.ToUnixTimeSeconds()}";
        var now = DateTimeOffset.UtcNow.AddMilliseconds(Interlocked.Increment(ref _ids));
        if (success)
        {
            sub["next_payment_date"] = Iso(end);
            sub["status"] = "active";
        }
        else
            sub["status"] = "attention";
        var data = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _ids), ["invoice_code"] = invoiceCode, ["amount"] = (long)plan["amount"]!, ["currency"] = "NGN",
            ["period_start"] = Iso(start), ["period_end"] = Iso(end), ["status"] = success ? "success" : "failed", ["paid"] = success,
            ["paid_at"] = success ? Iso(now) : null, ["updatedAt"] = Iso(now), ["createdAt"] = Iso(now),
            ["subscription"] = new JsonObject { ["subscription_code"] = subscriptionCode, ["status"] = sub["status"]!.DeepClone() },
            ["customer"] = sub["customer"]!.DeepClone(),
            ["transaction"] = success ? new JsonObject { ["reference"] = $"T{Interlocked.Increment(ref _ids)}", ["status"] = "success" } : null,
        };
        return Sign(success ? "invoice.update" : "invoice.payment_failed", data);
    }

    /// <summary>Paystack notifies that a subscription was disabled (after our cancel or replacement).</summary>
    public PaystackEvent Disabled(string subscriptionCode)
    {
        var sub = _subscriptions[subscriptionCode];
        return Sign("subscription.disable", new JsonObject
        {
            ["id"] = (long)sub["id"]!, ["subscription_code"] = subscriptionCode, ["status"] = sub["status"]!.DeepClone(),
            ["updatedAt"] = Iso(DateTimeOffset.UtcNow.AddMilliseconds(Interlocked.Increment(ref _ids))), ["customer"] = sub["customer"]!.DeepClone(),
        });
    }

    public PaystackEvent Sign(string eventName, JsonObject data)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["event"] = eventName, ["data"] = data }.ToJsonString());
        return new PaystackEvent(eventName, body, Convert.ToHexStringLower(PaystackBillingProvider.Sign(SecretKey, body)));
    }

    // ---- API ----

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        Calls.Enqueue($"{request.Method} /{path}");
        if (Outage)
            throw new HttpRequestException("[SIMULATED PAYSTACK] outage");
        if (request.Headers.Authorization?.Parameter != SecretKey)
            return Reply(HttpStatusCode.Unauthorized, false, "Invalid key", null);
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var parts = path.Split('/');
        lock (_gate)
        {
            return (request.Method.Method, parts) switch
            {
                ("POST", ["customer"]) => Ok(CreateCustomer(body!)),
                ("GET", ["customer", var code]) => Found(_customers.GetValueOrDefault(code)),
                ("GET", ["plan"]) => Ok(new JsonArray(_plans.Values.Where(p => query["amount"] is null || (long)p["amount"]! == long.Parse(query["amount"]!, CultureInfo.InvariantCulture))
                    .Select(p => (JsonNode)p.DeepClone()).ToArray())),
                ("POST", ["plan"]) => Ok(CreatePlan(body!)),
                ("GET", ["plan", var code]) => Found(_plans.GetValueOrDefault(code)),
                ("POST", ["transaction", "initialize"]) => Initialize(body!),
                ("GET", ["subscription"]) => Ok(new JsonArray(_subscriptions.Values
                    .Where(s => (long)s["customer"]!["id"]! == long.Parse(query["customer"]!, CultureInfo.InvariantCulture))
                    .Where(s => query["plan"] is null || (long)s["plan"]!["id"]! == long.Parse(query["plan"]!, CultureInfo.InvariantCulture))
                    .Select(s => (JsonNode)s.DeepClone()).ToArray())),
                ("POST", ["subscription"]) => Ok(CreateSubscription(body!)),
                ("GET", ["subscription", var code, "manage", "link"]) when _subscriptions.ContainsKey(code) =>
                    Ok(new JsonObject { ["link"] = $"https://paystack-simulated.invalid/manage/{code}" }),
                ("GET", ["subscription", var code]) => Found(_subscriptions.GetValueOrDefault(code)),
                ("POST", ["subscription", "disable"]) => Toggle(body!, enable: false),
                ("POST", ["subscription", "enable"]) => Toggle(body!, enable: true),
                _ => Reply(HttpStatusCode.NotFound, false, "Not found", null),
            };
        }
    }

    private JsonObject CreateCustomer(JsonObject body)
    {
        var email = (string)body["email"]!;
        var existing = _customers.Values.FirstOrDefault(c => (string?)c["email"] == email);
        if (existing is not null)
            return (JsonObject)existing.DeepClone();
        var id = Interlocked.Increment(ref _ids);
        var customer = new JsonObject { ["id"] = id, ["customer_code"] = $"CUS_sim{id}", ["email"] = email };
        _customers[(string)customer["customer_code"]!] = customer;
        return (JsonObject)customer.DeepClone();
    }

    private JsonObject CreatePlan(JsonObject body)
    {
        if ((string?)body["currency"] != "NGN")
            throw new InvalidOperationException("[SIMULATED PAYSTACK] only NGN plans");
        var id = Interlocked.Increment(ref _ids);
        var plan = new JsonObject
        {
            ["id"] = id, ["plan_code"] = $"PLN_sim{id}", ["name"] = body["name"]!.DeepClone(), ["amount"] = body["amount"]!.DeepClone(),
            ["interval"] = body["interval"]!.DeepClone(), ["currency"] = "NGN",
        };
        _plans[(string)plan["plan_code"]!] = plan;
        return (JsonObject)plan.DeepClone();
    }

    private HttpResponseMessage Initialize(JsonObject body)
    {
        var reference = (string)body["reference"]!;
        if (_transactions.ContainsKey(reference))
            return Reply(HttpStatusCode.BadRequest, false, "Duplicate Transaction Reference", null);
        if (!_plans.ContainsKey((string)body["plan"]!))
            return Reply(HttpStatusCode.BadRequest, false, "Plan not found", null);
        var id = Interlocked.Increment(ref _ids);
        var access = $"acc{id}";
        _transactions[reference] = new JsonObject
        {
            ["id"] = id, ["reference"] = reference, ["email"] = body["email"]!.DeepClone(), ["amount"] = _plans[(string)body["plan"]!]["amount"]!.DeepClone(),
            ["plan"] = body["plan"]!.DeepClone(), ["status"] = "pending", ["metadata"] = body["metadata"]?.DeepClone() ?? new JsonObject(),
            ["created_at"] = Iso(DateTimeOffset.UtcNow), ["callback_url"] = body["callback_url"]!.DeepClone(),
        };
        return Ok(new JsonObject { ["authorization_url"] = $"https://checkout.paystack-simulated.invalid/{access}", ["access_code"] = access, ["reference"] = reference });
    }

    private JsonObject CreateSubscription(JsonObject body)
    {
        var customer = _customers[(string)body["customer"]!];
        var plan = _plans[(string)body["plan"]!];
        var start = body["start_date"] is { } s ? Time(s) : DateTimeOffset.UtcNow;
        return (JsonObject)NewSubscription(customer, plan, start, start > DateTimeOffset.UtcNow ? "pending" : "active", firstPaymentAt: start).DeepClone();
    }

    private JsonObject NewSubscription(JsonObject customer, JsonObject plan, DateTimeOffset start, string status, DateTimeOffset? firstPaymentAt = null)
    {
        var id = Interlocked.Increment(ref _ids);
        var sub = new JsonObject
        {
            ["id"] = id, ["subscription_code"] = $"SUB_sim{id}", ["email_token"] = $"tok{id}", ["status"] = status,
            ["next_payment_date"] = Iso(firstPaymentAt ?? Advance(start, (string)plan["interval"]!)), ["createdAt"] = Iso(DateTimeOffset.UtcNow.AddTicks(id)),
            ["customer"] = Customer(customer), ["plan"] = new JsonObject { ["id"] = plan["id"]!.DeepClone(), ["plan_code"] = plan["plan_code"]!.DeepClone() },
        };
        _subscriptions[(string)sub["subscription_code"]!] = sub;
        return sub;
    }

    private HttpResponseMessage Toggle(JsonObject body, bool enable)
    {
        var sub = _subscriptions.GetValueOrDefault((string)body["code"]!);
        if (sub is null || (string?)sub["email_token"] != (string?)body["token"])
            return Reply(HttpStatusCode.BadRequest, false, "Subscription not found or token invalid", null);
        var active = (string?)sub["status"] is "active" or "pending" or "attention";
        if (enable == active)
            return Reply(HttpStatusCode.BadRequest, false, enable ? "Subscription is already active" : "Subscription is already inactive", null);
        sub["status"] = enable ? "active" : "cancelled";
        return Reply(HttpStatusCode.OK, true, enable ? "Subscription enabled successfully" : "Subscription disabled successfully", null);
    }

    private static JsonObject Customer(JsonObject customer) =>
        new() { ["id"] = customer["id"]!.DeepClone(), ["customer_code"] = customer["customer_code"]!.DeepClone(), ["email"] = customer["email"]!.DeepClone() };

    private static HttpResponseMessage Ok(JsonNode data) => Reply(HttpStatusCode.OK, true, "OK", data);

    private static HttpResponseMessage Found(JsonObject? data) =>
        data is null ? Reply(HttpStatusCode.NotFound, false, "Not found", null) : Ok(data.DeepClone());

    private static HttpResponseMessage Reply(HttpStatusCode status, bool ok, string message, JsonNode? data) =>
        new(status)
        {
            Content = new StringContent(new JsonObject { ["status"] = ok, ["message"] = message, ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static DateTimeOffset Advance(DateTimeOffset from, string interval) => interval == "annually" ? from.AddYears(1) : from.AddMonths(1);

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(JsonNode node) => DateTimeOffset.Parse((string)node!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
}
