using TrustedInventory.Application.Administration;

namespace TrustedInventory.Worker;

/// <summary>
/// The worker serves no people, so it can never accept a step-up re-authentication (IAM-005). Application services that need one are
/// registered for dependency validation only; reaching this verifier from a job refuses instead of skipping the check.
/// </summary>
public sealed class NoReauthVerifier : IReauthVerifier
{
    public Task<byte[]> VerifyAsync(string reauthToken, string expectedSubject, CancellationToken ct) =>
        throw new InvalidOperationException("The worker cannot verify re-authentication; step-up actions run only through the API.");
}
