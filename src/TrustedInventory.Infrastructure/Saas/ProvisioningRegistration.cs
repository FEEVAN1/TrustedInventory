using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Application.Saas;

namespace TrustedInventory.Infrastructure.Saas;

public static class ProvisioningRegistration
{
    /// <summary>
    /// Signup and provisioning services on the control-plane pool (registered by AddInfrastructure).
    /// Trial length and home region default to the spec's proposals until the owner approves them (DEC-040).
    /// </summary>
    public static IServiceCollection AddProvisioning(this IServiceCollection services, IConfiguration config)
    {
        var region = config["Saas:HomeRegion"] is { Length: > 0 } r ? r : "default";
        if (!System.Text.RegularExpressions.Regex.IsMatch(region, "^[a-z0-9][a-z0-9-]{1,31}$"))
            throw new InvalidOperationException("Saas:HomeRegion must be a lowercase region code.");

        var trialDays = Bounded(config, "Saas:TrialDays", 1, 90, 14);
        services.AddSingleton(new SaasOptions(trialDays, region)
        {
            // Proposed abuse limits until the owner approves them (DEC-044).
            SignupsPerIdentityPerDay = Bounded(config, "Saas:Abuse:SignupsPerIdentityPerDay", 1, 1000, 3),
            InvitationsPerTenantPerHour = Bounded(config, "Saas:Abuse:InvitationsPerTenantPerHour", 1, 10000, 30),
        });
        services.AddSingleton<IProvisioningFaults, NoProvisioningFaults>();
        services.AddSingleton<IProvisioningEngine, ProvisioningEngine>();
        services.AddSingleton<IPlanCatalog, PlanCatalog>();
        services.AddSingleton<ProvisioningSweepJob>();
        return services;
    }

    /// <summary>An integer setting within bounds, else the default. A malformed value falls back rather than disabling a limit.</summary>
    private static int Bounded(IConfiguration config, string key, int min, int max, int fallback) =>
        int.TryParse(config[key], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v >= min && v <= max ? v : fallback;
}

/// <summary>Resumes provisioning jobs left part-way (ONB-002): due retries and lapsed leases.</summary>
public sealed class ProvisioningSweepJob(IProvisioningEngine engine) : IJob
{
    public Task RunOnceAsync(CancellationToken ct) => engine.RunDueAsync(ct);
}
