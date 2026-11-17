using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace TrustedInventory.Application.Common;

/// <summary>Database gauges, aggregated across tenants (ti.operations_metrics, DEC-079).</summary>
public sealed record OperationsGauges(long LedgerMismatchAlerts, double OutboxMaxAgeSeconds, long OutboxDeadLettered, long StaleDevices, long HeldBuckets,
    decimal HeldQuantity, long OpenExcursions);

public interface IOperationsGaugeReader
{
    Task<OperationsGauges> ReadAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Process counters for operations (NFR-MONITOR, DEC-079), published on the "TrustedInventory" meter for any exporter and rendered in
/// Prometheus text format by the platform metrics route. Counters never carry a tenant id.
/// </summary>
public static class OperationsMetrics
{
    public const string MeterName = "TrustedInventory";
    private static long _allocationFailures;
    private static long _transactionRetries;
    private static long _rateLimited;

    private static readonly Meter Meter = new(MeterName);

    static OperationsMetrics()
    {
        Meter.CreateObservableCounter("ti_allocation_failures_total", () => Interlocked.Read(ref _allocationFailures), description: "Allocations refused for lack of eligible stock");
        Meter.CreateObservableCounter("ti_transaction_retries_total", () => Interlocked.Read(ref _transactionRetries), description: "Deadlock or serialization retries");
        Meter.CreateObservableCounter("ti_rate_limited_total", () => Interlocked.Read(ref _rateLimited), description: "Requests refused by a rate budget");
    }

    public static void AllocationFailed() => Interlocked.Increment(ref _allocationFailures);

    public static void TransactionRetried() => Interlocked.Increment(ref _transactionRetries);

    public static void RateLimited() => Interlocked.Increment(ref _rateLimited);

    /// <summary>Prometheus text exposition (version 0.0.4) of the counters and the database gauges.</summary>
    public static string Render(OperationsGauges g)
    {
        var sb = new StringBuilder();
        void Metric(string name, string type, string help, string value)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            sb.Append(name).Append(' ').Append(value).Append('\n');
        }
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        Metric("ti_allocation_failures_total", "counter", "Allocations refused for lack of eligible stock.", Interlocked.Read(ref _allocationFailures).ToString(CultureInfo.InvariantCulture));
        Metric("ti_transaction_retries_total", "counter", "Deadlock or serialization retries in tenant transactions.", Interlocked.Read(ref _transactionRetries).ToString(CultureInfo.InvariantCulture));
        Metric("ti_rate_limited_total", "counter", "Requests refused by a rate budget.", Interlocked.Read(ref _rateLimited).ToString(CultureInfo.InvariantCulture));
        Metric("ti_ledger_mismatch_alerts", "gauge", "Open ledger reconciliation mismatch alerts.", g.LedgerMismatchAlerts.ToString(CultureInfo.InvariantCulture));
        Metric("ti_outbox_max_age_seconds", "gauge", "Age of the oldest undelivered outbox message.", N(g.OutboxMaxAgeSeconds));
        Metric("ti_outbox_dead_lettered", "gauge", "Dead-lettered outbox messages awaiting operator replay.", g.OutboxDeadLettered.ToString(CultureInfo.InvariantCulture));
        Metric("ti_stale_devices", "gauge", "Placed active devices without a reading for 300 seconds.", g.StaleDevices.ToString(CultureInfo.InvariantCulture));
        Metric("ti_hold_buckets", "gauge", "Stock buckets on Hold with stock.", g.HeldBuckets.ToString(CultureInfo.InvariantCulture));
        Metric("ti_hold_quantity", "gauge", "Base quantity on Hold.", N((double)g.HeldQuantity));
        Metric("ti_open_excursions", "gauge", "Unresolved temperature excursions.", g.OpenExcursions.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
