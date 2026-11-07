namespace TrustedInventory.Application.Common;

/// <summary>A response carrying an optimistic-concurrency version; the API returns it as the quoted ETag.</summary>
public interface IVersioned
{
    int Version { get; }
}

public static class Versioning
{
    /// <summary>Compares the If-Match version with the loaded row (412 VERSION_CONFLICT on mismatch).</summary>
    public static void EnsureVersion(int actual, int expected)
    {
        if (actual != expected)
            throw Errors.AppException.VersionConflict();
    }
}
