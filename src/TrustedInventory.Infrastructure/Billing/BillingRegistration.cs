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

        services.AddSingleton(new BillingOptions(provider, LiveCheckoutEnabled: false) { ReturnUrl = returnUrl });
        services.AddSingleton<IBillingCatalogStore, BillingCatalogStore>();
        // One simulator per process: the adapter and its test console are the same instance (DEC-063).
        services.AddSingleton<SimulatedBillingProvider>();
        services.AddSingleton<IBillingProvider>(sp => sp.GetRequiredService<SimulatedBillingProvider>());
        services.AddSingleton<ICheckoutStore, CheckoutStore>();
        return services;
    }
}
