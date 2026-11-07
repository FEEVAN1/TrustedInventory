using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;

namespace TrustedInventory.Application.Organization;

/// <summary>
/// A platform-level caller (DEC-026). Platform administrators manage tenants only; they hold no tenant
/// membership and therefore no stock access (section 05, TEN-004).
/// </summary>
public sealed record PlatformCaller(string Subject, bool IsAdministrator, string? RequestId = null)
{
    public void RequireAdministrator()
    {
        if (!IsAdministrator)
            throw AppException.Forbidden("Platform administration is restricted to platform administrators.");
    }
}

/// <summary>
/// One ti_platform transaction (no tenant context). Audit entries are appended to the named tenant's own
/// hash chain before commit, so tenant history includes provisioning and suspension (AUD-001).
/// </summary>
public interface IPlatformSession
{
    DbContext Db { get; }
    DateTimeOffset Now { get; }

    void Audit(Guid tenantId, AuditEntry entry);

    /// <summary>Creates the per-tenant audit stream row (genesis hash) for a newly provisioned tenant.</summary>
    Task CreateAuditStreamAsync(Guid tenantId, CancellationToken ct);
}

public interface IPlatformUnitOfWork
{
    Task<T> ReadAsync<T>(PlatformCaller caller, Func<IPlatformSession, CancellationToken, Task<T>> work, CancellationToken ct = default);

    Task<T> WriteAsync<T>(PlatformCaller caller, Func<IPlatformSession, CancellationToken, Task<T>> work, CancellationToken ct = default);
}
