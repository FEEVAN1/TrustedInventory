namespace TrustedInventory.Domain.Organization;

/// <summary>
/// One immutable version of tenant configuration (CFG-001). Version 1 is the built-in default and is never
/// stored, so stored versions start at 2. New rules apply prospectively from <see cref="EffectiveFrom"/>.
/// </summary>
public sealed class ConfigurationVersion
{
    public const string TenantScope = "Tenant";
    public const int DefaultsVersion = 1;

    private ConfigurationVersion() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ScopeType { get; private set; } = TenantScope;
    public Guid? ScopeId { get; private set; }
    public int ConfigVersion { get; private set; }
    /// <summary>Settings as JSON (the contract's TenantSettings shape).</summary>
    public string Settings { get; private set; } = "{}";
    public string Reason { get; private set; } = "";
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string CreatedBy { get; private set; } = "";

    public static ConfigurationVersion CreateTenant(Guid tenantId, int version, string settingsJson, string reason,
        string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ScopeType = TenantScope,
        ConfigVersion = version > DefaultsVersion
            ? version
            : throw new ArgumentOutOfRangeException(nameof(version), "Stored versions start after the built-in defaults."),
        Settings = settingsJson,
        Reason = reason,
        EffectiveFrom = now,
        CreatedAt = now,
        CreatedBy = actor,
    };
}
