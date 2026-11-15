using Npgsql;

namespace TrustedInventory.Infrastructure.Persistence;

/// <summary>
/// Connection pool for ti_control (DEC-035): control-plane schema cp plus tenant provisioning rows; no stock or
/// catalog privileges (SAS-004). Used by signup/provisioning, billing and the platform console's read side.
/// </summary>
public sealed class ControlDataSource(NpgsqlDataSource source) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    /// <summary>Closes the pool when the host stops, so pooled connections are not left open.</summary>
    public ValueTask DisposeAsync() => Source.DisposeAsync();

    public void Dispose() => Source.Dispose();
}
