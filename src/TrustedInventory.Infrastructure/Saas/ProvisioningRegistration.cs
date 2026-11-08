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

        var trialDays = int.TryParse(config["Saas:TrialDays"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) && d is > 0 and <= 90 ? d : 14;
        services.AddSingleton(new SaasOptions(trialDays, region));
        services.AddSingleton<IProvisioningFaults, NoProvisioningFaults>();
        services.AddSingleton<IProvisioningEngine, ProvisioningEngine>();
        services.AddSingleton<IPlanCatalog, PlanCatalog>();
        services.AddSingleton<ProvisioningSweepJob>();
        return services;
    }
}

/// <summary>Resumes provisioning jobs left part-way (ONB-002): due retries and lapsed leases.</summary>
public sealed class ProvisioningSweepJob(IProvisioningEngine engine) : IJob
{
    public Task RunOnceAsync(CancellationToken ct) => engine.RunDueAsync(ct);
}
