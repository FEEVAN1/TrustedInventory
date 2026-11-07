using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Administration;

public sealed record TelemetrySettings(int? ReadingIntervalSeconds, int? StaleAfterSeconds, int? FutureSkewSeconds);

/// <summary>Tenant settings (contract TenantSettings). Nullable only so a missing field is a 422, not a silent 0.</summary>
public sealed record TenantSettings(
    int? ReservationExpiryHours,
    int? DefaultMinRemainingShelfLifeDays,
    int? OverReceiptTolerancePercent,
    int? ApprovalExpiryMinutes,
    int? IdempotencyRetentionDays,
    TelemetrySettings? Telemetry)
{
    /// <summary>Section 03 / section 21 defaults: configuration version 1, never stored.</summary>
    public static readonly TenantSettings Defaults = new(24, 0, 0, 30, 7, new TelemetrySettings(60, 300, 60));
}

public sealed record TenantConfigurationDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    TenantSettings Settings, DateTimeOffset EffectiveFrom, string Reason) : IVersioned;

public sealed record CreateConfigurationVersion(TenantSettings Settings, string Reason);

public sealed class CreateConfigurationVersionValidator : AbstractValidator<CreateConfigurationVersion>
{
    public CreateConfigurationVersionValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Settings).NotNull();
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings.ReservationExpiryHours).NotNull().InclusiveBetween(1, 720).OverridePropertyName("settings.reservationExpiryHours");
            RuleFor(x => x.Settings.DefaultMinRemainingShelfLifeDays).NotNull().InclusiveBetween(0, 3650).OverridePropertyName("settings.defaultMinRemainingShelfLifeDays");
            // DEC-023: zero over-receipt is a database rule in R1; a tolerance needs a decision (D08) first.
            RuleFor(x => x.Settings.OverReceiptTolerancePercent).NotNull().Equal(0)
                .WithMessage("Over-receipt tolerance is fixed at 0 in this release.").OverridePropertyName("settings.overReceiptTolerancePercent");
            RuleFor(x => x.Settings.ApprovalExpiryMinutes).NotNull().InclusiveBetween(1, 1440).OverridePropertyName("settings.approvalExpiryMinutes");
            RuleFor(x => x.Settings.IdempotencyRetentionDays).NotNull().InclusiveBetween(7, 365).OverridePropertyName("settings.idempotencyRetentionDays");
            RuleFor(x => x.Settings.Telemetry).NotNull().OverridePropertyName("settings.telemetry");
            When(x => x.Settings.Telemetry is not null, () =>
            {
                RuleFor(x => x.Settings.Telemetry!.ReadingIntervalSeconds).NotNull().InclusiveBetween(1, 3600).OverridePropertyName("settings.telemetry.readingIntervalSeconds");
                RuleFor(x => x.Settings.Telemetry!.StaleAfterSeconds).NotNull().InclusiveBetween(60, 86400).OverridePropertyName("settings.telemetry.staleAfterSeconds");
                RuleFor(x => x.Settings.Telemetry!.FutureSkewSeconds).NotNull().InclusiveBetween(0, 600).OverridePropertyName("settings.telemetry.futureSkewSeconds");
            });
        });
    }
}

/// <summary>
/// Versioned tenant configuration (CFG-001): every change is a new immutable version with actor and reason,
/// applied prospectively. Version 1 is the built-in default; a create must name the current version in If-Match.
/// </summary>
public sealed class ConfigurationService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateConfigurationVersion> validator)
{
    private const string DefaultsReason = "Built-in defaults (specification section 03).";

    public Task<TenantConfigurationDto> GetCurrentAsync(CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => await CurrentAsync(s, c), ct);
    }

    public Task<Page<TenantConfigurationDto>> ListVersionsAsync(PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.TenantConfigManage);
        page.EnsureSupportedSort();
        var limit = page.EffectiveLimit;
        const string filterKey = "configuration-versions";

        return uow.ReadAsync(async (s, c) =>
        {
            var query = Stored(s).AsNoTracking();
            if (page.Cursor is { } cursor)
            {
                var p = cursors.Decode(cursor, filterKey);
                query = query.Where(v => v.CreatedAt < p.CreatedAt || (v.CreatedAt == p.CreatedAt && v.Id.CompareTo(p.Id) < 0));
            }
            var rows = await query.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).Take(limit + 1).ToListAsync(c);
            string? next = null;
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), filterKey);
            }
            return new Page<TenantConfigurationDto>(rows.Select(ToDto).ToList(), next);
        }, ct);
    }

    public Task<TenantConfigurationDto> CreateVersionAsync(int expectedVersion, CreateConfigurationVersion cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantConfigManage);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var current = await CurrentAsync(s, c);
            Versioning.EnsureVersion(current.Version, expectedVersion);

            var version = ConfigurationVersion.CreateTenant(caller.TenantId, current.Version + 1,
                JsonSerializer.Serialize(cmd.Settings, JsonDefaults.Options), cmd.Reason, caller.Subject, s.Now);
            s.Db.Add(version);

            var dto = ToDto(version);
            s.Audit(new AuditEntry("ConfigurationVersionCreated", "Configuration", version.Id, version.ConfigVersion, cmd.Reason,
                current.Settings, dto.Settings));
            return dto;
        }, ct);
    }

    private static IQueryable<ConfigurationVersion> Stored(ITenantSession s) =>
        s.Db.Set<ConfigurationVersion>().Where(v => v.ScopeType == ConfigurationVersion.TenantScope && v.ScopeId == null);

    private static async Task<TenantConfigurationDto> CurrentAsync(ITenantSession s, CancellationToken ct)
    {
        var latest = await Stored(s).AsNoTracking().OrderByDescending(v => v.ConfigVersion).FirstOrDefaultAsync(ct);
        if (latest is not null)
            return ToDto(latest);

        var tenant = await s.Db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(ct) ?? throw AppException.NotFound("Tenant");
        return new TenantConfigurationDto(tenant.Id, ConfigurationVersion.DefaultsVersion, tenant.CreatedAt, "system",
            tenant.CreatedAt, TenantSettings.Defaults, tenant.CreatedAt, DefaultsReason);
    }

    private static TenantConfigurationDto ToDto(ConfigurationVersion v) =>
        new(v.Id, v.ConfigVersion, v.CreatedAt, v.CreatedBy, v.CreatedAt,
            JsonSerializer.Deserialize<TenantSettings>(v.Settings, JsonDefaults.Options)!, v.EffectiveFrom, v.Reason);
}
