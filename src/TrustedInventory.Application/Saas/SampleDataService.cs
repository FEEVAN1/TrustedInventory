using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Saas;

public sealed record SampleDataItemDto(string ObjectType, Guid ObjectId, string Code, string Name);

public sealed record SampleDataSetDto(Guid Id, int Version, string Status, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? RemovedAt,
    string? RemovedBy, IReadOnlyList<SampleDataItemDto> Items) : IVersioned;

public sealed record CreateSampleData(string TimeZone);

public sealed class CreateSampleDataValidator : AbstractValidator<CreateSampleData>
{
    public CreateSampleDataValidator() => RuleFor(x => x.TimeZone).ValidTimeZone();
}

/// <summary>
/// Opt-in sample data (ONB-004, DEC-098). One labelled set per workspace at a time: ordinary master records whose codes start SAMPLE- and
/// whose names end (sample). They count against the plan like any record. A database trigger keeps them isolated from production
/// quantities: no stock bucket may hold a sample product or sit in a sample warehouse, whichever command tries.
/// </summary>
public sealed class SampleDataService(ICallerAccessor callers, ITenantUnitOfWork uow, IQuotaGate quota, IValidator<CreateSampleData> validator)
{
    private static readonly string[] Products = ["Printer paper A4 box", "Packing tape roll", "Shipping carton, medium"];

    public Task<SampleDataSetDto> GetAsync(CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var id = (await s.Db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM ti.sample_data_set ORDER BY (status = 'Active') DESC, created_at DESC LIMIT 1
                """).ToListAsync(c)).SingleOrDefault();
            return id == Guid.Empty ? throw AppException.NotFound("Sample data") : await LoadAsync(s, id, c);
        }, ct);
    }

    public Task<SampleDataSetDto> CreateAsync(CreateSampleData cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.RequireTenantWide(Permissions.TenantConfigManage);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            // SAS-005 order: quotas first, then domain checks; a refusal rolls everything back.
            await quota.ReserveAsync(s, QuotaKey.ActiveWarehouses, 1, c);
            await quota.ReserveAsync(s, QuotaKey.ActiveSkus, Products.Length, c);
            if ((await s.Db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ti.sample_data_set WHERE status = 'Active'").ToListAsync(c))
                .Single() > 0)
                throw AppException.InvalidState("This workspace already has sample data; remove it before adding it again.");

            var setId = Guid.CreateVersion7();
            var tag = setId.ToString("N")[^4..].ToUpperInvariant(); // keeps codes unique if sample data is added again later
            var (tenant, actor, now) = (caller.TenantId, caller.Subject, s.Now);
            string Code(string part) => $"SAMPLE-{part}-{tag}";

            var warehouse = Warehouse.Create(tenant, $"SAMPLE-{tag}", "Sample warehouse (sample)", cmd.TimeZone, actor, now);
            var systemZone = Zone.CreateSystem(tenant, warehouse.Id, actor, now);
            var transit = Bin.CreateTransit(tenant, warehouse.Id, systemZone.Id, actor, now);
            var zone = Zone.Create(tenant, warehouse.Id, "SAMPLE-AMB", "Ambient (sample)", null, null, actor, now);
            var bins = new[]
            {
                Bin.Create(tenant, warehouse.Id, zone.Id, Code("IN"), BinKind.Staging, 0, null, null, actor, now),
                Bin.Create(tenant, warehouse.Id, zone.Id, Code("A01"), BinKind.Storage, 1, null, null, actor, now),
                Bin.Create(tenant, warehouse.Id, zone.Id, Code("Q01"), BinKind.Quarantine, 9, null, null, actor, now),
            };
            var unit = Unit.Create(tenant, Code("EA"), "Each (sample)", actor, now);
            var products = Products.Select((name, i) => Product.Create(tenant, Code($"P{i + 1:000}"), $"{name} (sample)", HandlingProfile.General,
                TrackingMode.Quantity, unit.Id, 0, null, null, null, actor, now)).ToArray();
            var partners = new[]
            {
                Partner.Create(tenant, Code("SUP"), "Sample supplier (sample)", PartnerType.Supplier, actor, now),
                Partner.Create(tenant, Code("CUS"), "Sample customer (sample)", PartnerType.Customer, actor, now),
            };
            s.Db.AddRange(warehouse, systemZone, transit, zone, unit);
            s.Db.AddRange(bins);
            s.Db.AddRange(products);
            s.Db.AddRange(partners);
            await s.Db.SaveChangesAsync(c);

            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.sample_data_set (id, tenant_id, status, created_at, created_by) VALUES ({setId}, {tenant}, 'Active', {now}, {actor})
                """, c);
            var items = new List<(string Type, Guid Id)> { ("Warehouse", warehouse.Id), ("Zone", zone.Id), ("Unit", unit.Id) };
            items.AddRange(bins.Select(b => ("Bin", b.Id)));
            items.AddRange(products.Select(p => ("Product", p.Id)));
            items.AddRange(partners.Select(p => ("Partner", p.Id)));
            foreach (var (type, id) in items)
                await s.Db.Database.ExecuteSqlAsync($"""
                    INSERT INTO ti.sample_data_item (tenant_id, set_id, object_type, object_id) VALUES ({tenant}, {setId}, {type}, {id})
                    """, c);

            var dto = await LoadAsync(s, setId, c);
            s.Audit(new AuditEntry("SampleDataAdded", "SampleData", setId, dto.Version, null, null,
                new { warehouse = warehouse.Code, products = products.Length, items = dto.Items.Count }));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<SampleDataSetDto> RemoveAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.RequireTenantWide(Permissions.TenantConfigManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<SetRow>($"""
                SELECT status AS "Status", version AS "Version" FROM ti.sample_data_set WHERE id = {id} FOR UPDATE
                """).ToListAsync(c);
            var set = rows.SingleOrDefault() ?? throw AppException.NotFound("Sample data");
            Versioning.EnsureVersion(set.Version, expectedVersion);
            if (set.Status != "Active")
                throw AppException.InvalidState("This sample data was already removed.");

            var items = await s.Db.Database.SqlQuery<ItemRow>($"""
                SELECT object_type AS "Type", object_id AS "Id" FROM ti.sample_data_item WHERE set_id = {id}
                """).ToListAsync(c);
            Guid[] Of(string type) => items.Where(i => i.Type == type).Select(i => i.Id).ToArray();
            var now = s.Now;
            var (warehouses, products, bins, zones, units, partners) = (Of("Warehouse"), Of("Product"), Of("Bin"), Of("Zone"), Of("Unit"), Of("Partner"));
            foreach (var b in await s.Db.Set<Bin>().Where(b => bins.Contains(b.Id) && b.Active).ToListAsync(c)) DomainGuard.Run(() => b.Retire(now));
            foreach (var z in await s.Db.Set<Zone>().Where(z => zones.Contains(z.Id) && z.Active).ToListAsync(c)) DomainGuard.Run(() => z.Retire(now));
            var activeWarehouses = await s.Db.Set<Warehouse>().Where(w => warehouses.Contains(w.Id) && w.Active).ToListAsync(c);
            var activeProducts = await s.Db.Set<Product>().Where(p => products.Contains(p.Id) && p.Active).ToListAsync(c);
            foreach (var w in activeWarehouses) DomainGuard.Run(() => w.Retire(now));
            foreach (var p in activeProducts) DomainGuard.Run(() => p.Retire(now));
            foreach (var u in await s.Db.Set<Unit>().Where(u => units.Contains(u.Id) && u.Active).ToListAsync(c)) DomainGuard.Run(() => u.Retire(now));
            foreach (var p in await s.Db.Set<Partner>().Where(p => partners.Contains(p.Id) && p.Active).ToListAsync(c)) DomainGuard.Run(() => p.Retire(now));
            // Before the flush: counters seeded now still count these records once, then release them (DEC-045).
            if (activeWarehouses.Count > 0)
                await quota.ReleaseAsync(s, QuotaKey.ActiveWarehouses, activeWarehouses.Count, c);
            if (activeProducts.Count > 0)
                await quota.ReleaseAsync(s, QuotaKey.ActiveSkus, activeProducts.Count, c);
            await s.Db.SaveChangesAsync(c);

            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.sample_data_set SET status = 'Removed', removed_at = {now}, removed_by = {caller.Subject}, version = version + 1 WHERE id = {id}
                """, c);
            var dto = await LoadAsync(s, id, c);
            s.Audit(new AuditEntry("SampleDataRemoved", "SampleData", id, dto.Version, null, new { status = "Active" },
                new { status = "Removed", retired = activeWarehouses.Count + activeProducts.Count }));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    private static async Task<SampleDataSetDto> LoadAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var set = (await s.Db.Database.SqlQuery<SetDetailRow>($"""
            SELECT id AS "Id", version AS "Version", status AS "Status", created_at AS "CreatedAt", created_by AS "CreatedBy",
                   removed_at AS "RemovedAt", removed_by AS "RemovedBy"
            FROM ti.sample_data_set WHERE id = {id}
            """).ToListAsync(ct)).Single();
        var items = await s.Db.Database.SqlQuery<ItemDetailRow>($"""
            SELECT i.object_type AS "Type", i.object_id AS "Id",
                   coalesce(w.code, z.code, b.code, u.code, p.sku, pa.code) AS "Code",
                   coalesce(w.name, z.name, b.code, u.name, p.name, pa.name) AS "Name"
            FROM ti.sample_data_item i
            LEFT JOIN ti.warehouse w ON i.object_type = 'Warehouse' AND w.id = i.object_id
            LEFT JOIN ti.zone z ON i.object_type = 'Zone' AND z.id = i.object_id
            LEFT JOIN ti.bin b ON i.object_type = 'Bin' AND b.id = i.object_id
            LEFT JOIN ti.unit u ON i.object_type = 'Unit' AND u.id = i.object_id
            LEFT JOIN ti.product p ON i.object_type = 'Product' AND p.id = i.object_id
            LEFT JOIN ti.partner pa ON i.object_type = 'Partner' AND pa.id = i.object_id
            WHERE i.set_id = {id}
            ORDER BY array_position(ARRAY['Warehouse','Zone','Bin','Unit','Product','Partner'], i.object_type), 3
            """).ToListAsync(ct);
        return new SampleDataSetDto(set.Id, set.Version, set.Status, set.CreatedAt, set.CreatedBy, set.RemovedAt, set.RemovedBy,
            items.Select(i => new SampleDataItemDto(i.Type, i.Id, i.Code, i.Name)).ToList());
    }

    private sealed record SetRow(string Status, int Version);

    private sealed record ItemRow(string Type, Guid Id);

    private sealed record SetDetailRow(Guid Id, int Version, string Status, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? RemovedAt,
        string? RemovedBy);

    private sealed record ItemDetailRow(string Type, Guid Id, string Code, string Name);
}
