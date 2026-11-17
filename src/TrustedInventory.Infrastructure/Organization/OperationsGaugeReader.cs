using TrustedInventory.Application.Common;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Organization;

/// <summary>Reads ti.operations_metrics() on the ti_control pool: aggregates only (DEC-079).</summary>
public sealed class OperationsGaugeReader(ControlDataSource control) : IOperationsGaugeReader
{
    public async Task<OperationsGauges> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = control.Source.CreateCommand("SELECT * FROM ti.operations_metrics(@now)");
        cmd.Parameters.AddWithValue("now", now);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new OperationsGauges(r.GetInt64(0), r.GetDouble(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetDecimal(5), r.GetInt64(6));
    }
}
