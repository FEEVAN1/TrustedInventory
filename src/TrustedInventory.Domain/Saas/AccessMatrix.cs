namespace TrustedInventory.Domain.Saas;

/// <summary>
/// The table in docs/saas-state-matrix.md, as code. Keep the two identical: a unit test walks every cell.
/// </summary>
public static class AccessMatrix
{
    public static bool Allows(AccessMode mode, AccessCategory category, bool isOwner) => mode switch
    {
        AccessMode.Normal => true,
        AccessMode.Provisioning => category is AccessCategory.Identity || (category is AccessCategory.SetupStatus && isOwner),
        AccessMode.BillingRestricted => category is not (AccessCategory.RoutineWrite or AccessCategory.TeamAdmin),
        // Humans see only their restriction; device ingestion and safety workers continue (footnote 4).
        AccessMode.SecuritySuspended => category is AccessCategory.Identity or AccessCategory.TelemetryIngest,
        AccessMode.Closed => category switch
        {
            AccessCategory.Identity or AccessCategory.Safety or AccessCategory.ProtectiveAdmin
                or AccessCategory.Billing or AccessCategory.TelemetryIngest => true,
            AccessCategory.SetupStatus or AccessCategory.Export => isOwner,
            _ => false,
        },
        AccessMode.Deleted => false,
        _ => false,
    };

    /// <summary>Whether entitlement (feature) gating applies; safety never loses access (ENT-006, SUI-003).</summary>
    public static bool UsesEntitlements(AccessCategory category) => category != AccessCategory.Safety;
}
