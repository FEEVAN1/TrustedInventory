using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>Worker tuning (section "Worker" in configuration). Defaults suit development.</summary>
public sealed record WorkerOptions
{
    public string WorkerId { get; init; } = $"{Environment.MachineName}-{Environment.ProcessId}";
    public TimeSpan OutboxInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int OutboxBatchSize { get; init; } = 50;
    public TimeSpan OutboxLease { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>NFR-JOBS: ten attempts, then dead letter (schema CHECK allows 0..10).</summary>
    public int OutboxMaxAttempts { get; init; } = 10;
    public TimeSpan OutboxRetryBase { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan OutboxRetryMax { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan IdempotencyCleanupInterval { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Ledger reconciliation (STK-004, DEC-049).</summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Reservation expiry (STK-008, DEC-055).</summary>
    public TimeSpan ReservationExpiryInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Export generation (AUD-004, DEC-061).</summary>
    public TimeSpan ExportInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Billing event processing (BIL-005, DEC-064); webhook age alerts at five minutes (SOP-003).</summary>
    public TimeSpan BillingEventInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Billing notice relay to tenant outboxes (DEC-064).</summary>
    public TimeSpan BillingNoticeInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Scheduled plan changes (ENT-006, DEC-065).</summary>
    public TimeSpan PlanChangeInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Provider reconciliation (BIL-006, DEC-066): daily; run sooner after an outage by restarting or lowering it.</summary>
    public TimeSpan BillingReconciliationInterval { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Payment reminders on days 3 and 6 (BIL-007, DEC-066).</summary>
    public TimeSpan BillingReminderInterval { get; init; } = TimeSpan.FromHours(1);

    public static WorkerOptions From(IConfiguration config)
    {
        var d = new WorkerOptions();
        return new WorkerOptions
        {
            WorkerId = config["Worker:WorkerId"] is { Length: > 0 } id ? id : d.WorkerId,
            OutboxInterval = Seconds(config, "Worker:OutboxIntervalSeconds", d.OutboxInterval),
            OutboxBatchSize = Int(config, "Worker:OutboxBatchSize", d.OutboxBatchSize),
            OutboxLease = Seconds(config, "Worker:OutboxLeaseSeconds", d.OutboxLease),
            OutboxMaxAttempts = Math.Clamp(Int(config, "Worker:OutboxMaxAttempts", d.OutboxMaxAttempts), 1, 10),
            OutboxRetryBase = Seconds(config, "Worker:OutboxRetryBaseSeconds", d.OutboxRetryBase),
            OutboxRetryMax = Seconds(config, "Worker:OutboxRetryMaxSeconds", d.OutboxRetryMax),
            IdempotencyCleanupInterval = Seconds(config, "Worker:IdempotencyCleanupIntervalSeconds", d.IdempotencyCleanupInterval),
            CheckpointInterval = Seconds(config, "Worker:CheckpointIntervalSeconds", d.CheckpointInterval),
            ReconciliationInterval = Seconds(config, "Worker:ReconciliationIntervalSeconds", d.ReconciliationInterval),
            ReservationExpiryInterval = Seconds(config, "Worker:ReservationExpiryIntervalSeconds", d.ReservationExpiryInterval),
            ExportInterval = Seconds(config, "Worker:ExportIntervalSeconds", d.ExportInterval),
            BillingEventInterval = Seconds(config, "Worker:BillingEventIntervalSeconds", d.BillingEventInterval),
            BillingNoticeInterval = Seconds(config, "Worker:BillingNoticeIntervalSeconds", d.BillingNoticeInterval),
            PlanChangeInterval = Seconds(config, "Worker:PlanChangeIntervalSeconds", d.PlanChangeInterval),
            BillingReconciliationInterval = Seconds(config, "Worker:BillingReconciliationIntervalSeconds", d.BillingReconciliationInterval),
            BillingReminderInterval = Seconds(config, "Worker:BillingReminderIntervalSeconds", d.BillingReminderInterval),
        };
    }

    private static int Int(IConfiguration c, string key, int fallback) =>
        c[key] is { Length: > 0 } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static TimeSpan Seconds(IConfiguration c, string key, TimeSpan fallback) =>
        c[key] is { Length: > 0 } v ? TimeSpan.FromSeconds(int.Parse(v, CultureInfo.InvariantCulture)) : fallback;
}

/// <summary>Checkpoint signing key (AUD-003). Held in the deployment secret store; required at startup.</summary>
public sealed record CheckpointSigningOptions(byte[] Key, string KeyId)
{
    public static CheckpointSigningOptions From(IConfiguration config)
    {
        var key = Convert.FromBase64String(DependencyInjection.Required(config, "Audit:CheckpointKey"));
        if (key.Length < 32)
            throw new InvalidOperationException("Audit:CheckpointKey must be at least 32 bytes.");
        return new CheckpointSigningOptions(key, DependencyInjection.Required(config, "Audit:CheckpointKeyId"));
    }
}
