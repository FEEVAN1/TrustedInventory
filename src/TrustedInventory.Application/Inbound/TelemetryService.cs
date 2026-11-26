using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Alerts;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record DeviceIdentity(Guid TenantId, Guid DeviceId, Guid CredentialId);

/// <summary>Device credential authentication and the device's own unit of work (TEL-001, DEC-077).</summary>
public interface IDeviceGateway
{
    Task<DeviceIdentity?> AuthenticateAsync(string? bearer, CancellationToken ct);

    ITenantUnitOfWork UnitOfWorkFor(DeviceIdentity device);
}

/// <summary>Issues device secrets; implemented with a cryptographic random source in Infrastructure.</summary>
public interface IDeviceCredentials
{
    (Guid CredentialId, string Token, byte[] SecretHash) New();
}

public enum ReadingProcessingStatus { Stored, ExcursionOpened, LateReviewQueued }

public sealed record CreateDeviceRequest(Guid ZoneId, string ExternalId, string Name);

public sealed record UpdateDeviceRequest(string? Name, Guid? ZoneId, DateTimeOffset? ZoneEffectiveFrom);

public sealed record DeviceDto(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid? ZoneId, string ExternalId,
    string Name, bool Active, DateTimeOffset? ZoneEffectiveFrom, DateTimeOffset? LastReadingAt, bool Stale, DateTimeOffset? CredentialExpiresAt) : IVersioned;

public sealed record DeviceCredentialDto(Guid DeviceId, Guid CredentialId, string Secret, DateTimeOffset? ExpiresAt);

public sealed record ReadingRequest(Guid DeviceId, string EventId, DateTimeOffset ObservedAt, decimal TemperatureCelsius);

public sealed record ReadingResult(Guid ReadingId, bool Duplicate, ReadingProcessingStatus ProcessingStatus);

public sealed record ReadingDto(Guid Id, Guid DeviceId, Guid ZoneId, string EventId, DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt,
    decimal TemperatureCelsius, bool Late);

public sealed record DeviceFilter(bool? Active, Guid? ZoneId, bool? Stale);

public sealed record ReadingFilter(Guid? DeviceId, Guid? ZoneId, DateTimeOffset? From, DateTimeOffset? To);

public sealed class CreateDeviceRequestValidator : AbstractValidator<CreateDeviceRequest>
{
    public CreateDeviceRequestValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.ExternalId).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9][A-Za-z0-9._/-]*$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class ReadingRequestValidator : AbstractValidator<ReadingRequest>
{
    public ReadingRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.EventId).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9][A-Za-z0-9._/-]*$");
        RuleFor(x => x.TemperatureCelsius).InclusiveBetween(-273.15m, 1000m).Must(t => decimal.Round(t, 2) == t)
            .WithMessage("Give the temperature with at most two decimal places.");
    }
}

/// <summary>Telemetry defaults (TEL-002, D04 proposed): interval 60 s, stale after 300 s, 60 s future skew.</summary>
public static class TelemetryRules
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(60);

    /// <summary>Late: received long after it was observed, or older than the device's latest observation (TEL-002).</summary>
    public static bool IsLate(DateTimeOffset observedAt, DateTimeOffset receivedAt, DateTimeOffset? lastObservedAt) =>
        receivedAt - observedAt > StaleAfter || (lastObservedAt is { } last && observedAt < last);

    public static bool OutOfRange(decimal celsius, decimal minC, decimal maxC) => celsius < minC || celsius > maxC;
}

/// <summary>
/// Devices and reading ingestion (TEL-001 to TEL-004, ENT-003, DEC-077). Devices count against active_devices; a reading's tenant and
/// zone come from the authenticated device and its placement at observedAt.
/// </summary>
public sealed class TelemetryService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IQuotaGate quota,
    IDeviceGateway gateway,
    IDeviceCredentials credentials,
    ITenantAccessReader access,
    IAuditSupport support,
    TimeProvider clock,
    IValidator<CreateDeviceRequest> createValidator,
    IValidator<ReadingRequest> readingValidator)
{
    public Task<DeviceDto> CreateDeviceAsync(CreateDeviceRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.DevicesManage);
        createValidator.ValidateOrThrow(cmd);
        return uow.WriteAsync(async (s, c) =>
        {
            await RequireZoneAsync(s, cmd.ZoneId, c);
            await quota.ReserveAsync(s, QuotaKey.ActiveDevices, 1, c);
            var id = Guid.CreateVersion7();
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.device (id, tenant_id, external_id, name, created_by, created_at, updated_at)
                VALUES ({id}, {caller.TenantId}, {cmd.ExternalId}, {cmd.Name.Trim()}, {caller.Subject}, {s.Now}, {s.Now})
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.device_zone_assignment (tenant_id, device_id, zone_id, effective_from, created_by, created_at)
                VALUES ({caller.TenantId}, {id}, {cmd.ZoneId}, {s.Now}, {caller.Subject}, {s.Now})
                """, c);
            var dto = await DeviceAsync(s, id, c);
            s.Audit(new AuditEntry("DeviceRegistered", "Device", id, dto.Version, null, null, dto));
            return dto;
        }, ct);
    }

    /// <summary>Renames or moves a device; a move is effective-dated and never rewrites earlier readings (TEL-004).</summary>
    public Task<DeviceDto> UpdateDeviceAsync(Guid id, int expectedVersion, UpdateDeviceRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.DevicesManage);
        if (cmd.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 200))
            throw AppException.Validation("name", "Give a name of 1 to 200 characters.");
        return uow.WriteAsync(async (s, c) =>
        {
            var before = await LockDeviceAsync(s, id, expectedVersion, c);
            if (!before.Active)
                throw AppException.InvalidState("The device is retired.");
            if (cmd.Name is { } n)
                await s.Db.Database.ExecuteSqlAsync($"UPDATE ti.device SET name = {n.Trim()} WHERE id = {id}", c);
            if (cmd.ZoneId is { } zone && zone != before.ZoneId)
            {
                await RequireZoneAsync(s, zone, c);
                var from = cmd.ZoneEffectiveFrom ?? s.Now;
                if (before.ZoneEffectiveFrom is { } current && from <= current)
                    throw AppException.Validation("zoneEffectiveFrom", "A move takes effect after the current placement began.");
                if (from > s.Now + TimeSpan.FromDays(1))
                    throw AppException.Validation("zoneEffectiveFrom", "A move cannot be scheduled more than a day ahead.");
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.device_zone_assignment SET effective_to = {from} WHERE device_id = {id} AND effective_to IS NULL
                    """, c);
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.device_zone_assignment (tenant_id, device_id, zone_id, effective_from, created_by, created_at)
                    VALUES ({caller.TenantId}, {id}, {zone}, {from}, {caller.Subject}, {s.Now})
                    """, c);
            }
            await TouchAsync(s, id, c);
            var after = await DeviceAsync(s, id, c);
            s.Audit(new AuditEntry("DeviceUpdated", "Device", id, after.Version, null, before, after));
            return after;
        }, ct);
    }

    public Task<DeviceDto> RetireDeviceAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.DevicesManage);
        return uow.WriteAsync(async (s, c) =>
        {
            var before = await LockDeviceAsync(s, id, expectedVersion, c);
            if (!before.Active)
                throw AppException.InvalidState("The device is already retired.");
            await s.Db.Database.ExecuteSqlAsync($"UPDATE ti.device SET active = false WHERE id = {id}", c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device_zone_assignment SET effective_to = {s.Now} WHERE device_id = {id} AND effective_to IS NULL AND effective_from < {s.Now}
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device_credential SET expires_at = {s.Now} WHERE device_id = {id} AND (expires_at IS NULL OR expires_at > {s.Now})
                """, c);
            await quota.ReleaseAsync(s, QuotaKey.ActiveDevices, 1, c);
            await TouchAsync(s, id, c);
            var after = await DeviceAsync(s, id, c);
            s.Audit(new AuditEntry("DeviceRetired", "Device", id, after.Version, null, before, after));
            return after;
        }, ct);
    }

    /// <summary>A new credential, shown once; the previous one lasts until the new one is used or 24 hours pass (TEL-001).</summary>
    public Task<(DeviceCredentialDto Credential, int Version)> RotateCredentialAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.DevicesManage);
        return uow.WriteAsync(async (s, c) =>
        {
            var before = await LockDeviceAsync(s, id, expectedVersion, c);
            if (!before.Active)
                throw AppException.InvalidState("The device is retired.");
            var (credentialId, token, hash) = credentials.New();
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device_credential SET expires_at = LEAST(coalesce(expires_at, {s.Now + TimeSpan.FromHours(24)}), {s.Now + TimeSpan.FromHours(24)})
                WHERE device_id = {id} AND (expires_at IS NULL OR expires_at > {s.Now})
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.device_credential (id, tenant_id, device_id, secret_sha256, created_by, created_at)
                VALUES ({credentialId}, {caller.TenantId}, {id}, {hash}, {caller.Subject}, {s.Now})
                """, c);
            await TouchAsync(s, id, c);
            var after = await DeviceAsync(s, id, c);
            // The secret itself is never audited or stored.
            s.Audit(new AuditEntry("DeviceCredentialRotated", "Device", id, after.Version, null, null, new { credentialId }));
            return (new DeviceCredentialDto(id, credentialId, token, null), after.Version);
        }, ct);
    }

    public Task<DeviceDto> GetDeviceAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        return uow.ReadAsync((s, c) => DeviceAsync(s, id, c), ct);
    }

    public Task<Page<DeviceDto>> ListDevicesAsync(DeviceFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        page.EnsureSupportedSort();
        var key = $"devices|{filter.Active}|{filter.ZoneId}|{filter.Stale}";
        var after = page.Cursor is { } cur ? cursors.Decode(cur, key) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var ids = await s.Db.Database.SqlQuery<KeyRow>($"""
                SELECT d.id AS "Id", d.created_at AS "CreatedAt" FROM ti.device d
                WHERE ({filter.Active}::boolean IS NULL OR d.active = {filter.Active})
                  AND ({filter.ZoneId}::uuid IS NULL OR EXISTS (SELECT 1 FROM ti.device_zone_assignment a WHERE a.device_id = d.id
                        AND a.zone_id = {filter.ZoneId} AND a.effective_to IS NULL))
                  AND ({filter.Stale}::boolean IS NULL OR {filter.Stale} = (d.active AND (d.last_reading_at IS NULL OR d.last_reading_at <= {s.Now - TelemetryRules.StaleAfter})))
                  AND ({after?.CreatedAt}::timestamptz IS NULL OR d.created_at < {after?.CreatedAt} OR (d.created_at = {after?.CreatedAt} AND d.id < {after?.Id}))
                ORDER BY d.created_at DESC, d.id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            string? next = null;
            if (ids.Count > page.EffectiveLimit)
            {
                ids.RemoveAt(page.EffectiveLimit);
                next = cursors.Encode(new CursorPosition(ids[^1].CreatedAt, ids[^1].Id), key);
            }
            var items = new List<DeviceDto>();
            foreach (var r in ids)
                items.Add(await DeviceAsync(s, r.Id, c));
            return new Page<DeviceDto>(items, next);
        }, ct);
    }

    public Task<Page<ReadingDto>> ListReadingsAsync(ReadingFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.AlertsRead);
        page.EnsureSupportedSort();
        var key = $"readings|{filter.DeviceId}|{filter.ZoneId}|{filter.From:O}|{filter.To:O}";
        var after = page.Cursor is { } cur ? cursors.Decode(cur, key) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<ReadingRow>($"""
                SELECT id AS "Id", device_id AS "DeviceId", zone_id AS "ZoneId", external_event_id AS "EventId", observed_at AS "ObservedAt",
                       received_at AS "ReceivedAt", temperature_c AS "Celsius", is_late AS "Late", created_at AS "CreatedAt"
                FROM ti.telemetry_reading
                WHERE ({filter.DeviceId}::uuid IS NULL OR device_id = {filter.DeviceId}) AND ({filter.ZoneId}::uuid IS NULL OR zone_id = {filter.ZoneId})
                  AND ({filter.From}::timestamptz IS NULL OR observed_at >= {filter.From}) AND ({filter.To}::timestamptz IS NULL OR observed_at < {filter.To})
                  AND ({after?.CreatedAt}::timestamptz IS NULL OR created_at < {after?.CreatedAt} OR (created_at = {after?.CreatedAt} AND id < {after?.Id}))
                ORDER BY created_at DESC, id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            string? next = null;
            if (rows.Count > page.EffectiveLimit)
            {
                rows.RemoveAt(page.EffectiveLimit);
                next = cursors.Encode(new CursorPosition(rows[^1].CreatedAt, rows[^1].Id), key);
            }
            return new Page<ReadingDto>(rows.Select(r => new ReadingDto(r.Id, r.DeviceId, r.ZoneId, r.EventId, r.ObservedAt, r.ReceivedAt, r.Celsius, r.Late))
                .ToList(), next);
        }, ct);
    }

    /// <summary>
    /// One reading from an authenticated device (TEL-001 to TEL-004, AT-TEL-01, AT-TEL-02). The device may only report for itself; a
    /// duplicate eventId changes nothing; an abnormal reading opens or extends the zone's excursion, holds affected stock once and
    /// raises one alert; a late abnormal reading also flags shipments for QA review; a normal reading never releases anything.
    /// </summary>
    public async Task<ReadingResult> IngestAsync(DeviceIdentity device, ReadingRequest cmd, CancellationToken ct)
    {
        readingValidator.ValidateOrThrow(cmd);
        if (cmd.DeviceId != device.DeviceId)
            throw AppException.Forbidden("A device credential may only report readings for its own device (TEL-001).");
        var now = clock.GetUtcNow();
        if (cmd.ObservedAt > now + TelemetryRules.FutureSkew)
            throw AppException.Validation("observedAt", "The reading is more than 60 seconds in the future (TEL-002).");
        var state = await access.ReadAsync(device.TenantId, ct) ?? throw AppException.Forbidden();
        AccessGate.Enforce(state, now, AccessCategory.TelemetryIngest, false, "telemetry");

        return await gateway.UnitOfWorkFor(device).WriteAsync(async (s, c) =>
        {
            var zone = (await s.Db.Database.SqlQuery<Guid>($"""
                SELECT zone_id AS "Value" FROM ti.device_zone_assignment
                WHERE device_id = {device.DeviceId} AND effective_from <= {cmd.ObservedAt} AND (effective_to IS NULL OR effective_to > {cmd.ObservedAt})
                """).ToListAsync(c)).Cast<Guid?>().SingleOrDefault()
                ?? throw AppException.Validation("observedAt", "The device was not placed in any zone at that time.");
            var lastObserved = (await s.Db.Database.SqlQuery<DateTimeOffset?>($"""
                SELECT last_observed_at AS "Value" FROM ti.device WHERE id = {device.DeviceId} FOR UPDATE
                """).ToListAsync(c)).Single();
            var late = TelemetryRules.IsLate(cmd.ObservedAt, s.Now, lastObserved);
            var inserted = await s.Db.Database.SqlQuery<Guid>($"""
                INSERT INTO ti.telemetry_reading (tenant_id, device_id, zone_id, external_event_id, observed_at, received_at, temperature_c, is_late)
                VALUES ({device.TenantId}, {device.DeviceId}, {zone}, {cmd.EventId}, {cmd.ObservedAt}, {s.Now}, {cmd.TemperatureCelsius}, {late})
                ON CONFLICT (tenant_id, device_id, external_event_id) DO NOTHING RETURNING id AS "Value"
                """).ToListAsync(c);
            if (inserted.Count == 0)
            {
                var existing = await s.Db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM ti.telemetry_reading WHERE device_id = {device.DeviceId} AND external_event_id = {cmd.EventId}
                    """).SingleAsync(c);
                return new ReadingResult(existing, true, ReadingProcessingStatus.Stored);
            }
            var readingId = inserted[0];
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device SET last_reading_at = {s.Now}, last_observed_at = GREATEST(coalesce(last_observed_at, {cmd.ObservedAt}), {cmd.ObservedAt})
                WHERE id = {device.DeviceId}
                """, c);
            // First use of a new credential ends the previous one at once (TEL-001).
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device_credential SET first_used_at = coalesce(first_used_at, {s.Now}) WHERE id = {device.CredentialId}
                """, c);
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.device_credential SET expires_at = {s.Now}
                WHERE device_id = {device.DeviceId} AND id <> {device.CredentialId} AND (expires_at IS NULL OR expires_at > {s.Now})
                  AND created_at < (SELECT created_at FROM ti.device_credential WHERE id = {device.CredentialId})
                """, c);

            var status = await ExcursionEvaluator.EvaluateAsync(s, support, device.DeviceId, zone, readingId, cmd.ObservedAt, cmd.TemperatureCelsius, late, c);
            return new ReadingResult(readingId, false, status);
        }, ct);
    }

    private static async Task RequireZoneAsync(ITenantSession s, Guid zoneId, CancellationToken ct)
    {
        if (!await s.Db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM ti.zone WHERE id = {zoneId} AND active) AS \"Value\"").SingleAsync(ct))
            throw AppException.Validation("zoneId", "Use an active zone.");
    }

    private static async Task<DeviceDto> LockDeviceAsync(ITenantSession s, Guid id, int expectedVersion, CancellationToken ct)
    {
        var version = (await s.Db.Database.SqlQuery<int>($"SELECT version AS \"Value\" FROM ti.device WHERE id = {id} FOR UPDATE").ToListAsync(ct))
            .Cast<int?>().SingleOrDefault() ?? throw AppException.NotFound("Device");
        Versioning.EnsureVersion(version, expectedVersion);
        return await DeviceAsync(s, id, ct);
    }

    private static Task<int> TouchAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        s.Db.Database.ExecuteSqlAsync($"UPDATE ti.device SET version = version + 1, updated_at = {s.Now} WHERE id = {id}", ct);

    internal static async Task<DeviceDto> DeviceAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var r = (await s.Db.Database.SqlQuery<DeviceRow>($"""
            SELECT d.id AS "Id", d.version AS "Version", d.created_at AS "CreatedAt", d.created_by AS "CreatedBy", d.updated_at AS "UpdatedAt",
                   a.zone_id AS "ZoneId", d.external_id AS "ExternalId", d.name AS "Name", d.active AS "Active", a.effective_from AS "ZoneEffectiveFrom",
                   d.last_reading_at AS "LastReadingAt",
                   (SELECT c.expires_at FROM ti.device_credential c WHERE c.device_id = d.id ORDER BY c.created_at DESC, c.id DESC LIMIT 1) AS "CredentialExpiresAt"
            FROM ti.device d
            LEFT JOIN ti.device_zone_assignment a ON a.device_id = d.id AND a.effective_from <= {s.Now} AND (a.effective_to IS NULL OR a.effective_to > {s.Now})
            WHERE d.id = {id}
            """).ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound("Device");
        var stale = r.Active && (r.LastReadingAt is null || s.Now - r.LastReadingAt > TelemetryRules.StaleAfter);
        return new DeviceDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.ZoneId, r.ExternalId, r.Name, r.Active, r.ZoneEffectiveFrom,
            r.LastReadingAt, stale, r.CredentialExpiresAt);
    }

    private sealed record KeyRow(Guid Id, DateTimeOffset CreatedAt);

    private sealed record DeviceRow(Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid? ZoneId, string ExternalId,
        string Name, bool Active, DateTimeOffset? ZoneEffectiveFrom, DateTimeOffset? LastReadingAt, DateTimeOffset? CredentialExpiresAt);

    private sealed record ReadingRow(Guid Id, Guid DeviceId, Guid ZoneId, string EventId, DateTimeOffset ObservedAt, DateTimeOffset ReceivedAt, decimal Celsius,
        bool Late, DateTimeOffset CreatedAt);
}

/// <summary>Excursion rules applied to a stored reading (TEL-003, TEL-004, AT-TEL-01, AT-TEL-02, DEC-077).</summary>
internal static class ExcursionEvaluator
{
    public static async Task<ReadingProcessingStatus> EvaluateAsync(ITenantSession s, IAuditSupport support, Guid deviceId, Guid zoneId, Guid readingId,
        DateTimeOffset observedAt, decimal celsius, bool late, CancellationToken ct)
    {
        // Profiles of products with stock in this zone, and which of them the reading breaches.
        var profiles = await s.Db.Database.SqlQuery<ProfileRow>($"""
            SELECT DISTINCT sp.id AS "ProfileId", sp.min_c AS "MinC", sp.max_c AS "MaxC"
            FROM ti.stock_bucket b JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
            JOIN ti.product p ON p.tenant_id = b.tenant_id AND p.id = b.product_id
            JOIN ti.storage_profile sp ON sp.tenant_id = p.tenant_id AND sp.id = p.storage_profile_id
            WHERE bn.zone_id = {zoneId} AND b.on_hand_qty > 0
            ORDER BY 1
            """).ToListAsync(ct);
        var breached = profiles.Where(p => TelemetryRules.OutOfRange(celsius, p.MinC, p.MaxC)).ToList();
        if (breached.Count == 0)
        {
            if (!late)
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.excursion SET ended_at = {observedAt}, updated_at = {s.Now}
                    WHERE zone_id = {zoneId} AND status <> 'Resolved' AND ended_at IS NULL AND started_at <= {observedAt}
                    """, ct);
            return ReadingProcessingStatus.Stored;   // normal temperature never releases or resolves (section 13)
        }

        var caller = s.Caller;
        var warehouseId = await s.Db.Database.SqlQuery<Guid>($"SELECT warehouse_id AS \"Value\" FROM ti.zone WHERE id = {zoneId}").SingleAsync(ct);
        foreach (var profile in breached)
        {
            var open = (await s.Db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM ti.excursion WHERE zone_id = {zoneId} AND storage_profile_id = {profile.ProfileId} AND status <> 'Resolved'
                FOR UPDATE
                """).ToListAsync(ct)).Cast<Guid?>().SingleOrDefault();
            Guid excursionId;
            if (open is { } existing)
            {
                excursionId = existing;
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.excursion SET last_abnormal_c = {celsius}, ended_at = NULL, is_historical = is_historical OR {late},
                           started_at = LEAST(started_at, {observedAt}), updated_at = {s.Now}, version = version + 1, last_modified_by = {caller.Subject}
                    WHERE id = {existing}
                    """, ct);
            }
            else
            {
                excursionId = Guid.CreateVersion7();
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.excursion (id, tenant_id, zone_id, device_id, storage_profile_id, trigger_reading_id, min_c, max_c, started_at,
                                              first_abnormal_c, last_abnormal_c, is_historical, last_modified_by, created_by, created_at, updated_at)
                    VALUES ({excursionId}, {caller.TenantId}, {zoneId}, {deviceId}, {profile.ProfileId}, {readingId}, {profile.MinC}, {profile.MaxC}, {observedAt},
                            {celsius}, {celsius}, {late}, {caller.Subject}, {caller.Subject}, {s.Now}, {s.Now})
                    """, ct);
                s.Audit(new AuditEntry("ExcursionOpened", "Excursion", excursionId, 1, $"{celsius} °C outside {profile.MinC}-{profile.MaxC} °C", null,
                    new { zoneId, deviceId, readingId, observedAt, celsius, late }));
            }

            // Hold currently affected stock once: Available or Quarantine buckets of products on this profile in the zone (TEL-003).
            var bucketIds = await s.Db.Database.SqlQuery<Guid>($"""
                SELECT b.id AS "Value" FROM ti.stock_bucket b JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
                JOIN ti.product p ON p.tenant_id = b.tenant_id AND p.id = b.product_id
                WHERE bn.zone_id = {zoneId} AND p.storage_profile_id = {profile.ProfileId} AND b.on_hand_qty > 0
                  AND b.quality_status IN ('Available', 'Quarantine')
                """).ToListAsync(ct);
            if (bucketIds.Count > 0)
            {
                var buckets = await QualityService.LoadAsync(s, bucketIds, ct);
                var moved = await QualityService.MoveWholeAsync(s, caller, buckets, QualityStatus.Hold, DocumentType.Excursion, excursionId,
                    $"Temperature excursion {celsius} °C", ct);
                foreach (var held in moved.Buckets)
                    await s.Db.Database.ExecuteSqlAsync($"""
                        INSERT INTO ti.excursion_bucket (tenant_id, excursion_id, bucket_id, held_qty, created_by, created_at)
                        SELECT {caller.TenantId}, {excursionId}, id, on_hand_qty, {caller.Subject}, {s.Now} FROM ti.stock_bucket WHERE id = {held.BucketId} AND on_hand_qty > 0
                        ON CONFLICT (tenant_id, excursion_id, bucket_id) DO NOTHING
                        """, ct);
            }

            if (late)
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.excursion_shipment_flag (tenant_id, excursion_id, shipment_line_id, created_by, created_at)
                    SELECT {caller.TenantId}, {excursionId}, sl.id, {caller.Subject}, {s.Now}
                    FROM ti.shipment_line sl JOIN ti.shipment sh ON sh.tenant_id = sl.tenant_id AND sh.id = sl.shipment_id
                    JOIN ti.outbound_order o ON o.tenant_id = sh.tenant_id AND o.id = sh.order_id
                    JOIN ti.product p ON p.tenant_id = sl.tenant_id AND p.id = sl.product_id
                    WHERE o.warehouse_id = {warehouseId} AND p.storage_profile_id = {profile.ProfileId} AND sh.dispatched_at >= {observedAt}
                    ON CONFLICT (tenant_id, excursion_id, shipment_line_id) DO NOTHING
                    """, ct);

            var products = await s.Db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT b.product_id AS "Value" FROM ti.excursion_bucket eb JOIN ti.stock_bucket b ON b.tenant_id = eb.tenant_id AND b.id = eb.bucket_id
                WHERE eb.excursion_id = {excursionId}
                """).ToListAsync(ct);
            var lots = await s.Db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT b.lot_id AS "Value" FROM ti.excursion_bucket eb JOIN ti.stock_bucket b ON b.tenant_id = eb.tenant_id AND b.id = eb.bucket_id
                WHERE eb.excursion_id = {excursionId} AND b.lot_id IS NOT NULL
                """).ToListAsync(ct);
            var first = await s.Db.Database.SqlQuery<decimal>($"SELECT first_abnormal_c AS \"Value\" FROM ti.excursion WHERE id = {excursionId}").SingleAsync(ct);
            var details = new JsonObject
            {
                ["title"] = $"Temperature excursion: {celsius} °C (limits {profile.MinC} to {profile.MaxC} °C)",
                ["subjectType"] = "Excursion",
                ["subjectId"] = excursionId.ToString(),
                ["zoneId"] = zoneId.ToString(),
                ["productIds"] = new JsonArray(products.Select(p => (JsonNode)p.ToString()).ToArray()),
                ["lotIds"] = new JsonArray(lots.Select(l => (JsonNode)l.ToString()).ToArray()),
                ["firstAbnormalCelsius"] = first,
                ["lastAbnormalCelsius"] = celsius,
                ["late"] = late,
            };
            var dedupe = $"excursion:{excursionId:D}";
            if (await support.RaiseAlertAsync(s, AlertType.Excursion, AlertSeverity.High, dedupe, details, ct))
            {
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.alert SET warehouse_id = {warehouseId}, zone_id = {zoneId} WHERE dedupe_key = {dedupe} AND status <> 'Closed'
                    """, ct);
                s.Publish("AlertRaised", new { type = nameof(AlertType.Excursion), excursionId, zoneId, details });
            }
            else
            {
                await s.Db.Database.ExecuteSqlAsync($"""
                    UPDATE ti.alert SET details = {details.ToJsonString()}::jsonb, updated_at = {s.Now}, version = version + 1
                    WHERE dedupe_key = {dedupe} AND status <> 'Closed'
                    """, ct);
            }
        }
        return late ? ReadingProcessingStatus.LateReviewQueued : ReadingProcessingStatus.ExcursionOpened;
    }

    private sealed record ProfileRow(Guid ProfileId, decimal MinC, decimal MaxC);
}

/// <summary>Stale telemetry blocks monitored stock (TEL-003): the bucket's zone has devices but none reported in time.</summary>
internal static class TelemetryChecks
{
    public static async Task<bool> StaleAsync(ITenantSession s, Guid bucketId, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<bool>($"""
            SELECT coalesce(bool_or(sp.monitoring_required AND ti.zone_telemetry_stale(bn.zone_id, {s.Now}, 300)), false) AS "Value"
            FROM ti.stock_bucket b JOIN ti.bin bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
            JOIN ti.product p ON p.tenant_id = b.tenant_id AND p.id = b.product_id
            LEFT JOIN ti.storage_profile sp ON sp.tenant_id = p.tenant_id AND sp.id = p.storage_profile_id
            WHERE b.id = {bucketId}
            """).ToListAsync(ct)).Single();
}
