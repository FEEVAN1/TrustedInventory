namespace TrustedInventory.Domain.Saas;

// Values match docs/enums.md (SaaS section) and the PostgreSQL enum labels; never reorder or rename.

public enum TenantLifecycle
{
    Provisioning,
    Ready,
    Archived,
    DeletionPending,
    Deleted,
}

public enum SubscriptionState
{
    Trialing,
    Active,
    PastDue,
    Cancelled,
}

/// <summary>Effective access, computed per request (DEC-033, docs/saas-state-matrix.md).</summary>
public enum AccessMode
{
    Normal,
    Provisioning,
    BillingRestricted,
    SecuritySuspended,
    Closed,
    Deleted,
}

/// <summary>Active restriction reasons, listed highest precedence first (LFC-001).</summary>
public enum RestrictionReason
{
    Provisioning,
    TrialEnded,
    PaymentOverdue,
    SubscriptionEnded,
    SecuritySuspended,
    Archived,
    DeletionPending,
    Deleted,
}

/// <summary>The `x-access` category of an operation (DEC-034). Wire values are kebab-case.</summary>
public enum AccessCategory
{
    Identity,
    SetupStatus,
    Read,
    Export,
    RoutineWrite,
    Safety,
    TeamAdmin,
    ProtectiveAdmin,
    Billing,
    Ownership,
    TelemetryIngest,
}

/// <summary>Quota keys (ENT-002). Database labels are the snake_case wire values.</summary>
public enum QuotaKey
{
    ActiveSeats,
    ActiveWarehouses,
    ActiveSkus,
    ActiveDevices,
    AttachmentBytes,
}

/// <summary>Feature keys (ENT-002), wire values as written in the spec.</summary>
public static class FeatureKeys
{
    public const string CoreInventory = "core_inventory";
    public const string ServiceApi = "service_api";
    public const string AdvancedReporting = "advanced_reporting";
    public const string MedicalStorage = "medical_storage";
    public const string Telemetry = "telemetry";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { CoreInventory, ServiceApi, AdvancedReporting, MedicalStorage, Telemetry };
}

public static class SaasWire
{
    public static string Category(AccessCategory c) => c switch
    {
        AccessCategory.Identity => "identity",
        AccessCategory.SetupStatus => "setup-status",
        AccessCategory.Read => "read",
        AccessCategory.Export => "export",
        AccessCategory.RoutineWrite => "routine-write",
        AccessCategory.Safety => "safety",
        AccessCategory.TeamAdmin => "team-admin",
        AccessCategory.ProtectiveAdmin => "protective-admin",
        AccessCategory.Billing => "billing",
        AccessCategory.Ownership => "ownership",
        AccessCategory.TelemetryIngest => "telemetry-ingest",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static AccessCategory ParseCategory(string wire) =>
        Enum.GetValues<AccessCategory>().FirstOrDefault(c => Category(c) == wire) is var c && Category(c) == wire
            ? c
            : throw new ArgumentException($"Unknown access category '{wire}'.", nameof(wire));

    public static string Quota(QuotaKey q) => q switch
    {
        QuotaKey.ActiveSeats => "active_seats",
        QuotaKey.ActiveWarehouses => "active_warehouses",
        QuotaKey.ActiveSkus => "active_skus",
        QuotaKey.ActiveDevices => "active_devices",
        QuotaKey.AttachmentBytes => "attachment_bytes",
        _ => throw new ArgumentOutOfRangeException(nameof(q)),
    };

    public static QuotaKey ParseQuota(string wire) =>
        Enum.GetValues<QuotaKey>().FirstOrDefault(q => Quota(q) == wire) is var q && Quota(q) == wire
            ? q
            : throw new ArgumentException($"Unknown quota key '{wire}'.", nameof(wire));
}
