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
