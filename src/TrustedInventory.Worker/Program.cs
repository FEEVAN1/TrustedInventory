using TrustedInventory.Application;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Missing connection strings, cursor or checkpoint keys, or storage settings stop startup (fail closed, section 18).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWorkerInfrastructure(builder.Configuration);
builder.Services.AddScoped<ICallerAccessor, NoRequestCallerAccessor>();

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
// Resumes signups whose inline provisioning stopped part-way (ONB-002).
builder.Services.AddHostedService(sp => new ScheduledJob<TrustedInventory.Infrastructure.Saas.ProvisioningSweepJob>(sp,
    builder.Configuration.GetValue("Worker:ProvisioningInterval", TimeSpan.FromSeconds(30))));

var host = builder.Build();
host.Run();
