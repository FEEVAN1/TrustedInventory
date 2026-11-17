using TrustedInventory.Application;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Organization;
using TrustedInventory.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "O";
}); // structured logs (DEC-079)

// Missing connection strings, cursor or checkpoint keys, or storage settings stop startup (fail closed, section 18).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWorkerInfrastructure(builder.Configuration);
builder.Services.AddScoped<ICallerAccessor, NoRequestCallerAccessor>();
// Application services shared with the API need the team adapters and a step-up verifier; the worker's verifier always refuses.
builder.Services.AddTeamInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TrustedInventory.Application.Administration.IReauthVerifier, NoReauthVerifier>();

builder.Services.AddHostedService(sp => new ScheduledJob<OutboxDispatcher>(sp, sp.GetRequiredService<WorkerOptions>().OutboxInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<IdempotencyCleanupJob>(sp, sp.GetRequiredService<WorkerOptions>().IdempotencyCleanupInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<AuditCheckpointJob>(sp, sp.GetRequiredService<WorkerOptions>().CheckpointInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<LedgerReconciliationJob>(sp, sp.GetRequiredService<WorkerOptions>().ReconciliationInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<ReservationExpiryJob>(sp, sp.GetRequiredService<WorkerOptions>().ReservationExpiryInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<ExportGenerationJob>(sp, sp.GetRequiredService<WorkerOptions>().ExportInterval));
// Verified billing events and the notices they raise (DEC-064). Simulator only (DEC-039).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Billing.BillingEventJob>(sp,
    sp.GetRequiredService<WorkerOptions>().BillingEventInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<BillingNoticeRelayJob>(sp, sp.GetRequiredService<WorkerOptions>().BillingNoticeInterval));
// Scheduled plan changes at their boundary (DEC-065).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Billing.PlanChangeJob>(sp,
    sp.GetRequiredService<WorkerOptions>().PlanChangeInterval));
// Provider reconciliation and payment reminders (DEC-066).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Billing.BillingReconciliationJob>(sp,
    sp.GetRequiredService<WorkerOptions>().BillingReconciliationInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Billing.BillingReminderJob>(sp,
    sp.GetRequiredService<WorkerOptions>().BillingReminderInterval));
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Billing.SubscriptionPeriodEndJob>(sp,
    sp.GetRequiredService<WorkerOptions>().SubscriptionPeriodEndInterval));
// Stale device alerts (TEL-005, DEC-077).
builder.Services.AddHostedService(sp => new ScheduledJob<StaleDeviceJob>(sp, builder.Configuration.GetValue("Worker:StaleDeviceInterval", TimeSpan.FromMinutes(1))));
// Platform signals and alerts (SOP-003, DEC-074).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Organization.PlatformMonitorJob>(sp,
    builder.Configuration.GetValue("Worker:MonitorInterval", TimeSpan.FromMinutes(1))));
// Approved deletions after the export window (LFC-004, DEC-072).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Saas.TenantDeletionJob>(sp,
    builder.Configuration.GetValue("Worker:DeletionInterval", TimeSpan.FromMinutes(15))));
// Resumes signups whose inline provisioning stopped part-way (ONB-002).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Saas.ProvisioningSweepJob>(sp,
    builder.Configuration.GetValue("Worker:ProvisioningInterval", TimeSpan.FromSeconds(30))));

var host = builder.Build();
host.Run();
