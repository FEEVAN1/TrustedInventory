using System.Buffers.Text;
using System.Security.Cryptography;
using Npgsql;
using TrustedInventory.Application.Inbound;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Saas;

/// <summary>
/// Device credential authentication (TEL-001, DEC-077). The credential id is looked up through ti.device_for_credential(), the only
/// query that runs without tenant context; the secret's SHA-256 is compared in constant time. The device's own tenant and id come from
/// that row, never from the request.
/// </summary>
public sealed class DeviceGateway(AppDataSource app, TimeProvider clock) : IDeviceGateway
{
    public const string Prefix = "ti_dev_";

    public async Task<DeviceIdentity?> AuthenticateAsync(string? bearer, CancellationToken ct)
    {
        if (bearer is null || !bearer.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var parts = bearer[Prefix.Length..].Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var credentialId) || parts[1].Length is < 20 or > 100)
            return null;
        byte[] secret;
        try
        {
            secret = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        await using var conn = await app.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id, device_id, secret_sha256, expires_at, device_active FROM ti.device_for_credential(@c)", conn);
        cmd.Parameters.AddWithValue("c", credentialId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        var stored = (byte[])r[2];
        var expires = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), stored) || !r.GetBoolean(4)
            || (expires is { } e && e <= clock.GetUtcNow()))
            return null;
        return new DeviceIdentity(r.GetGuid(0), r.GetGuid(1), credentialId);
    }

    public ITenantUnitOfWork UnitOfWorkFor(DeviceIdentity device) =>
        new TenantUnitOfWork(app, new FixedCaller(new CallerContext(Subject(device.DeviceId), ActorType.Device, device.TenantId, TenantStatus.Active,
            new Dictionary<string, PermissionGrant> { [Permissions.TelemetryIngest] = new(true, new HashSet<Guid>()) })), clock);

    public static string Subject(Guid deviceId) => $"device:{deviceId:D}";

    public static (Guid CredentialId, string Token, byte[] SecretHash) NewCredential()
    {
        var id = Guid.CreateVersion7();
        var secret = RandomNumberGenerator.GetBytes(32);
        return (id, $"{Prefix}{id:N}.{Base64Url.EncodeToString(secret)}", SHA256.HashData(secret));
    }

    private sealed class FixedCaller(CallerContext caller) : ICallerAccessor
    {
        public CallerContext Caller { get; } = caller;
    }
}

/// <summary>Device secrets from the cryptographic random source (TEL-001).</summary>
public sealed class DeviceCredentials : IDeviceCredentials
{
    public (Guid CredentialId, string Token, byte[] SecretHash) New() => DeviceGateway.NewCredential();
}
