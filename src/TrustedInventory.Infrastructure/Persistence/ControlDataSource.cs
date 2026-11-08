using Npgsql;

namespace TrustedInventory.Infrastructure.Persistence;

/// <summary>
/// Connection pool for ti_control (DEC-035): control-plane schema cp plus tenant provisioning rows; no stock or
/// catalog privileges (SAS-004). Used by signup/provisioning, billing and the platform console's read side.
/// </summary>
public sealed class ControlDataSource(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;
}
