using System.Security.Cryptography;
using System.Text;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;

namespace TrustedInventory.Application.Administration;

/// <summary>
/// Invitation tokens (ONB-005). A token is derived from the invitation id with a server-held key, so neither the
/// database nor the outbox ever holds a usable token: only its SHA-256 is stored, and the email sender recomputes
/// the token at delivery time from the invitation id.
/// </summary>
public interface IInvitationTokens
{
    string Issue(Guid invitationId);

    static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>Finds which tenant an invitation token belongs to, before the caller is a member (control plane).</summary>
public interface IInvitationLookup
{
    Task<Guid?> FindTenantAsync(byte[] tokenHash, CancellationToken ct);
}

/// <summary>A tenant unit of work for an explicit caller, used where the caller is not yet a member (acceptance).</summary>
public interface ITenantUnitOfWorkFactory
{
    ITenantUnitOfWork For(CallerContext caller);
}

/// <summary>
/// Verifies a fresh ID token for signing-strength actions (DEC-009, SOD-7): signature and issuer from the OIDC
/// configuration, subject equal to the caller, <c>auth_time</c> within 300 seconds, MFA. Returns the SHA-256 of
/// the token to store as reauth_reference; throws 403 REAUTH_REQUIRED otherwise.
/// </summary>
public interface IReauthVerifier
{
    Task<byte[]> VerifyAsync(string reauthToken, string expectedSubject, CancellationToken ct);
}
