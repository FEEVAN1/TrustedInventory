using TrustedInventory.Application.Security;

namespace TrustedInventory.Worker;

/// <summary>
/// The worker has no request caller. Jobs obtain an explicit tenant context from TenantJobRunner (TEN-004);
/// anything resolving the ambient caller instead fails loudly rather than running without tenant context.
/// </summary>
public sealed class NoRequestCallerAccessor : ICallerAccessor
{
    public CallerContext Caller =>
        throw new InvalidOperationException("The worker has no ambient caller; use TenantJobRunner for tenant context.");
}
