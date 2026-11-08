using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Organization;

/// <summary>
/// Token = base64url(HMAC-SHA256(Invitations:TokenKey, invitation id)): 32 random-looking bytes that only a holder
/// of the key can produce (ONB-005). The database stores SHA-256(token); the outbox carries only the id.
/// </summary>
public sealed class HmacInvitationTokens : IInvitationTokens
{
    private readonly byte[] _key;

    public HmacInvitationTokens(byte[] key)
    {
        if (key.Length < 32)
            throw new ArgumentException("Invitations:TokenKey must be at least 32 bytes.", nameof(key));
        _key = key;
    }

    public string Issue(Guid invitationId) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_key, invitationId.ToByteArray(bigEndian: true)));
}

/// <summary>
/// Looks up an invitation's tenant by token hash as ti_control, the only runtime role allowed to read invitations
/// across tenants (DEC-035, policy control_plane on ti.invitation). It returns the tenant id and nothing else.
/// </summary>
public sealed class ControlInvitationLookup(ControlDataSource dataSource) : IInvitationLookup
{
    public async Task<Guid?> FindTenantAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var cmd = dataSource.Source.CreateCommand("SELECT tenant_id FROM ti.invitation WHERE token_hash = @h");
        cmd.Parameters.Add(new NpgsqlParameter("h", tokenHash));
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }
}

public sealed class TenantUnitOfWorkFactory(AppDataSource dataSource, TimeProvider clock) : ITenantUnitOfWorkFactory
{
    public ITenantUnitOfWork For(CallerContext caller) => new TenantUnitOfWork(dataSource, new FixedCaller(caller), clock);

    private sealed class FixedCaller(CallerContext caller) : ICallerAccessor
    {
        public CallerContext Caller { get; } = caller;
    }
}

public static class TeamRegistration
{
    /// <summary>Invitations and ownership transfer adapters. The token key is required (fail closed, section 18).</summary>
    public static IServiceCollection AddTeamInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var key = Convert.FromBase64String(DependencyInjection.Required(configuration, "Invitations:TokenKey"));
        services.AddSingleton<IInvitationTokens>(new HmacInvitationTokens(key));
        services.AddSingleton<IInvitationLookup, ControlInvitationLookup>();
        services.AddSingleton<ITenantUnitOfWorkFactory, TenantUnitOfWorkFactory>();
        return services;
    }
}
