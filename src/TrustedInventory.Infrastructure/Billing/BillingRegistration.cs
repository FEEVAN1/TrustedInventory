using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Application.Billing;

namespace TrustedInventory.Infrastructure.Billing;

public static class BillingRegistration
{
    /// <summary>
    /// Billing on the control-plane pool (DEC-039, DEC-062). The simulator is the only provider until the owner chooses one
    /// (D09), so any other <c>Billing:Provider</c>, or live checkout switched on, stops startup: nothing can charge.
    /// </summary>
    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Billing:Provider"] is { Length: > 0 } p ? p : BillingOptions.Simulator;
        if (provider != BillingOptions.Simulator)
            throw new InvalidOperationException(
                $"Billing:Provider '{provider}' has no owner-approved configuration; only '{BillingOptions.Simulator}' is available (D09, DEC-039).");
        if (bool.TryParse(config["Billing:LiveCheckoutEnabled"], out var live) && live)
            throw new InvalidOperationException("Billing:LiveCheckoutEnabled must stay false until the owner approves a provider (DEC-039, DEC-040).");

        var returnUrl = config["Billing:ReturnUrl"] is { Length: > 0 } u ? u : "http://localhost:5173/billing/return";
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("Billing:ReturnUrl must be an absolute http(s) URL.");

        // Webhook secrets and the inbox key come from the vault in production; startup fails without them (DEC-064).
        var secret = DependencyInjection.Required(config, "Billing:Simulator:SigningSecret");
        var previous = config["Billing:Simulator:PreviousSigningSecret"] is { Length: > 0 } prev ? prev : null;
        if (secret.Length < SimulatorSigning.MinLength || previous is { Length: < SimulatorSigning.MinLength })
            throw new InvalidOperationException($"Billing:Simulator signing secrets need at least {SimulatorSigning.MinLength} characters.");
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
        services.AddSingleton(new SimulatorSigning(secret, previous));
        services.AddSingleton(new InboxCipher(inboxKey));
        services.AddSingleton<IBillingCatalogStore, BillingCatalogStore>();
        // One simulator per process: the adapter and its test console are the same instance (DEC-063).
        services.AddSingleton<SimulatedBillingProvider>();
        services.AddSingleton<IBillingProvider>(sp => sp.GetRequiredService<SimulatedBillingProvider>());
        services.AddSingleton<IBillingWebhooks>(sp => sp.GetRequiredService<SimulatedBillingProvider>());
        services.AddSingleton<IWebhookInbox, WebhookInbox>();
        services.AddSingleton<BillingEventJob>();
        services.AddSingleton<ICheckoutStore, CheckoutStore>();
        return services;
    }
}
