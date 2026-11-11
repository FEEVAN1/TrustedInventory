using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Application.Billing;

namespace TrustedInventory.Infrastructure.Billing;

public static class BillingRegistration
{
    /// <summary>
    /// Billing on the control-plane pool (DEC-039, DEC-062, DEC-068): the labelled simulator, or Paystack (owner decision D09) with a
    /// test key only. Any other provider, a live key, or live checkout switched on stops startup: nothing can charge.
    /// </summary>
    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Billing:Provider"] is { Length: > 0 } p ? p : BillingOptions.Simulator;
        if (provider is not (BillingOptions.Simulator or PaystackOptions.Code))
            throw new InvalidOperationException(
                $"Billing:Provider '{provider}' is not supported; use '{BillingOptions.Simulator}' or '{PaystackOptions.Code}' (D09, DEC-067).");
        if (bool.TryParse(config["Billing:LiveCheckoutEnabled"], out var live) && live)
            throw new InvalidOperationException("Billing:LiveCheckoutEnabled must stay false until a live configuration is approved (DEC-040, DEC-068).");

        var returnUrl = config["Billing:ReturnUrl"] is { Length: > 0 } u ? u : "http://localhost:5173/billing/return";
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Billing:ReturnUrl must be an absolute http(s) URL.");

        // Secrets and the inbox key come from the vault in production; startup fails without them (DEC-064, DEC-068).
        byte[] inboxKey;
        try
        {
            inboxKey = Convert.FromBase64String(DependencyInjection.Required(config, "Billing:InboxKey"));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Billing:InboxKey must be base64 of exactly 32 bytes.");
        }

        services.AddSingleton(new BillingOptions(provider, LiveCheckoutEnabled: false) { ReturnUrl = returnUrl });
        services.AddSingleton(new InboxCipher(inboxKey));
        if (provider == PaystackOptions.Code)
        {
            var key = DependencyInjection.Required(config, "Billing:Paystack:SecretKey");
            if (key.StartsWith("sk_live_", StringComparison.Ordinal))
                throw new InvalidOperationException("A live Paystack key needs an approved production configuration; use a sk_test_ key (DEC-040, DEC-068).");
            if (!key.StartsWith("sk_test_", StringComparison.Ordinal) || key.Length < 16)
                throw new InvalidOperationException("Billing:Paystack:SecretKey must be a Paystack test secret key (sk_test_...).");
            var baseUrl = config["Billing:Paystack:BaseUrl"] is { Length: > 0 } b ? new Uri(b.EndsWith('/') ? b : b + "/") : PaystackOptions.DefaultBaseUrl;
            if (baseUrl.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Billing:Paystack:BaseUrl must be https.");
            var ips = (config["Billing:Paystack:WebhookSourceIps"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            services.AddSingleton(new PaystackOptions(key, baseUrl, ips));
            services.AddSingleton(sp => new PaystackBillingProvider(sp.GetRequiredService<PaystackOptions>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PaystackBillingProvider>>(), sp.GetService<PaystackTransport>()));
            services.AddSingleton<IBillingProvider>(sp => sp.GetRequiredService<PaystackBillingProvider>());
            services.AddSingleton<IBillingWebhooks>(sp => sp.GetRequiredService<PaystackBillingProvider>());
        }
        else
        {
            var secret = DependencyInjection.Required(config, "Billing:Simulator:SigningSecret");
            var previous = config["Billing:Simulator:PreviousSigningSecret"] is { Length: > 0 } prev ? prev : null;
            if (secret.Length < SimulatorSigning.MinLength || previous is { Length: < SimulatorSigning.MinLength })
                throw new InvalidOperationException($"Billing:Simulator signing secrets need at least {SimulatorSigning.MinLength} characters.");
            services.AddSingleton(new SimulatorSigning(secret, previous));
            // One simulator per process: the adapter and its test console are the same instance (DEC-063).
            services.AddSingleton<SimulatedBillingProvider>();
            services.AddSingleton<IBillingProvider>(sp => sp.GetRequiredService<SimulatedBillingProvider>());
            services.AddSingleton<IBillingWebhooks>(sp => sp.GetRequiredService<SimulatedBillingProvider>());
        }
        services.AddSingleton<IBillingCatalogStore, BillingCatalogStore>();
        services.AddSingleton<WebhookInbox>();
        services.AddSingleton<IWebhookInbox>(sp => sp.GetRequiredService<WebhookInbox>());
        services.AddSingleton<IBillingConsoleStore, BillingConsoleStore>();
        services.AddSingleton<BillingReconciliationJob>();
        services.AddSingleton<BillingReminderJob>();
        services.AddSingleton<SubscriptionPeriodEndJob>();
        services.AddSingleton<BillingEventJob>();
        services.AddSingleton<ICheckoutStore, CheckoutStore>();
        services.AddSingleton<IPlanChangeStore, PlanChangeStore>();
        return services;
    }
}
