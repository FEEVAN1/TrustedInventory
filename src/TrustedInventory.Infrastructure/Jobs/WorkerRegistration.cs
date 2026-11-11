using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Application.Jobs;

namespace TrustedInventory.Infrastructure.Jobs;

public static class WorkerRegistration
{
    /// <summary>
    /// Registers background jobs and their adapters. Development uses the clearly simulated sink and retention
    /// store; missing signing or storage settings stop startup (fail closed, section 18).
    /// </summary>
    public static IServiceCollection AddWorkerInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(WorkerOptions.From(config));
        services.AddSingleton(CheckpointSigningOptions.From(config));
        var retentionDirectory = DependencyInjection.Required(config, "Audit:RetentionDirectory");

        services.AddSingleton<TenantJobRunner>();
        services.AddSingleton<IOutboxSink, SimulatedLoggingOutboxSink>();
        services.AddSingleton<IRetentionStore>(new SimulatedFileRetentionStore(retentionDirectory));
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<IdempotencyCleanupJob>();
        services.AddSingleton<AuditCheckpointJob>();
        services.AddSingleton<LedgerReconciliationJob>();
        services.AddSingleton<ReservationExpiryJob>();
        services.AddSingleton<ExportGenerationJob>();
        services.AddSingleton<BillingNoticeRelayJob>();
        services.AddSingleton<Billing.PlanChangeJob>();
        return services;
    }
}
