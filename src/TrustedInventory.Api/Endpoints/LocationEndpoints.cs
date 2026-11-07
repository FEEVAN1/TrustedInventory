using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Locations;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Warehouses, zones and bins (docs/openapi.yaml tags Locations). Handlers only translate HTTP to service calls.</summary>
public static class LocationEndpoints
{
    public static RouteGroupBuilder MapLocationEndpoints(this RouteGroupBuilder api)
    {
        var warehouses = api.MapGroup("/warehouses");
        warehouses.MapGet("", (HttpContext http, WarehouseService svc, [AsParameters] ListQuery q,
                [FromQuery] bool? active, [FromQuery] string? search, CancellationToken ct) =>
                svc.ListAsync(new WarehouseFilter(active, search), q.ToPage(), ct))
            .WithName("listWarehouses").RequirePermission(Permissions.CatalogRead);
        warehouses.MapPost("", async (HttpContext http, WarehouseService svc, CreateWarehouse body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/warehouses/{created.Id}", created);
            })
            .WithName("createWarehouse").RequirePermission(Permissions.WarehousesManage);
        warehouses.MapGet("/{id:guid}", async (HttpContext http, WarehouseService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getWarehouse").RequirePermission(Permissions.CatalogRead);
        warehouses.MapPatch("/{id:guid}", async (HttpContext http, WarehouseService svc, Guid id, UpdateWarehouse body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateWarehouse").RequirePermission(Permissions.WarehousesManage);
        warehouses.MapPost("/{id:guid}/retire", async (HttpContext http, WarehouseService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireWarehouse").RequirePermission(Permissions.WarehousesManage);

        var zones = api.MapGroup("/zones");
        zones.MapGet("", (ZoneService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] bool? active,
                CancellationToken ct) => svc.ListAsync(new ZoneFilter(warehouseId, active), q.ToPage(), ct))
            .WithName("listZones").RequirePermission(Permissions.CatalogRead);
        zones.MapPost("", async (HttpContext http, ZoneService svc, CreateZone body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/zones/{created.Id}", created);
            })
            .WithName("createZone").RequirePermission(Permissions.WarehousesManage);
        zones.MapGet("/{id:guid}", async (HttpContext http, ZoneService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getZone").RequirePermission(Permissions.CatalogRead);
        zones.MapPatch("/{id:guid}", async (HttpContext http, ZoneService svc, Guid id, UpdateZone body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateZone").RequirePermission(Permissions.WarehousesManage);
        zones.MapPost("/{id:guid}/retire", async (HttpContext http, ZoneService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireZone").RequirePermission(Permissions.WarehousesManage);

        var bins = api.MapGroup("/bins");
        bins.MapGet("", (BinService svc, [AsParameters] ListQuery q, [FromQuery] Guid? warehouseId, [FromQuery] Guid? zoneId,
                [FromQuery] bool? active, [FromQuery] BinKind? kind, [FromQuery] string? search, CancellationToken ct) =>
                svc.ListAsync(new BinFilter(warehouseId, zoneId, active, kind, search), q.ToPage(), ct))
            .WithName("listBins").RequirePermission(Permissions.CatalogRead);
        bins.MapPost("", async (HttpContext http, BinService svc, CreateBin body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/bins/{created.Id}", created);
            })
            .WithName("createBin").RequirePermission(Permissions.WarehousesManage);
        bins.MapGet("/{id:guid}", async (HttpContext http, BinService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getBin").RequirePermission(Permissions.CatalogRead);
        bins.MapPatch("/{id:guid}", async (HttpContext http, BinService svc, Guid id, UpdateBin body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateBin").RequirePermission(Permissions.WarehousesManage);
        bins.MapPost("/{id:guid}/retire", async (HttpContext http, BinService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireBin").RequirePermission(Permissions.WarehousesManage);

        return api;
    }
}
