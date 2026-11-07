using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TrustedInventory.Application.Jobs;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// SIMULATED integration (AGT-004): logs outbox events instead of delivering them anywhere. No real external
/// integration exists in R1; replace with a real sink only after the integration is approved (D07).
/// </summary>
public sealed partial class SimulatedLoggingOutboxSink(ILogger<SimulatedLoggingOutboxSink> log) : IOutboxSink
{
    public Task DeliverAsync(OutboxMessage message, CancellationToken ct)
    {
        LogDelivered(log, message.EventType, message.EventId, message.TenantId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[SIMULATED SINK] {EventType} {EventId} for tenant {TenantId}")]
    private static partial void LogDelivered(ILogger logger, string eventType, Guid eventId, Guid tenantId);
}

/// <summary>
/// SIMULATED retention store (AGT-004): writes checkpoints as files under a local directory, one per tenant and
/// day, and never overwrites a different existing record. Production needs separately administered,
/// retention-locked storage (AUD-003, D06); this is not that.
/// </summary>
public sealed class SimulatedFileRetentionStore(string directory) : IRetentionStore
{
    public async Task<string> WriteCheckpointAsync(AuditCheckpointRecord record, CancellationToken ct)
    {
        var date = record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var folder = Path.Combine(directory, record.TenantId.ToString());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, date + ".json");
        var content = JsonSerializer.Serialize(record);

        if (File.Exists(path))
        {
            if (await File.ReadAllTextAsync(path, ct) != content)
                throw new InvalidOperationException($"A different checkpoint already exists for {record.TenantId} on {date}.");
        }
        else
        {
            await File.WriteAllTextAsync(path, content, ct);
        }
        return $"simulated-file://{record.TenantId}/{date}.json";
    }
}
