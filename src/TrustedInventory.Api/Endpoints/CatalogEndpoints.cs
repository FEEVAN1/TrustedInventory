using Microsoft.AspNetCore.Mvc;
using TrustedInventory.Api.Http;
using TrustedInventory.Api.Security;
using TrustedInventory.Application.Catalog;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Api.Endpoints;

/// <summary>Units, storage profiles, products (+ conversions, barcodes) and partners. Handlers only translate HTTP to service calls.</summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        MapUnits(api.MapGroup("/units"));
        MapStorageProfiles(api.MapGroup("/storage-profiles"));
        MapProducts(api.MapGroup("/products"));
        MapPartners(api.MapGroup("/partners"));
        return api;
    }

    private static void MapUnits(RouteGroupBuilder units)
    {
        units.MapGet("", (UnitService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, [FromQuery] string? search,
                CancellationToken ct) => svc.ListAsync(new UnitFilter(active, search), q.ToPage(), ct))
            .WithName("listUnits").RequirePermission(Permissions.CatalogRead);
        units.MapPost("", async (HttpContext http, UnitService svc, CreateUnit body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/units/{created.Id}", created);
            })
            .WithName("createUnit").RequirePermission(Permissions.CatalogManage);
        units.MapGet("/{id:guid}", async (HttpContext http, UnitService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getUnit").RequirePermission(Permissions.CatalogRead);
        units.MapPatch("/{id:guid}", async (HttpContext http, UnitService svc, Guid id, UpdateUnit body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateUnit").RequirePermission(Permissions.CatalogManage);
        units.MapPost("/{id:guid}/retire", async (HttpContext http, UnitService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireUnit").RequirePermission(Permissions.CatalogManage);
    }

    private static void MapStorageProfiles(RouteGroupBuilder profiles)
    {
        profiles.MapGet("", (StorageProfileService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, CancellationToken ct) =>
                svc.ListAsync(new StorageProfileFilter(active), q.ToPage(), ct))
            .WithName("listStorageProfiles").RequirePermission(Permissions.CatalogRead);
        profiles.MapPost("", async (HttpContext http, StorageProfileService svc, CreateStorageProfile body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/storage-profiles/{created.Id}", created);
            })
            .WithName("createStorageProfile").RequirePermission(Permissions.TenantConfigManage);
        profiles.MapGet("/{id:guid}", async (HttpContext http, StorageProfileService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getStorageProfile").RequirePermission(Permissions.CatalogRead);
        profiles.MapPatch("/{id:guid}", async (HttpContext http, StorageProfileService svc, Guid id, UpdateStorageProfile body,
                CancellationToken ct) => Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateStorageProfile").RequirePermission(Permissions.TenantConfigManage);
        profiles.MapPost("/{id:guid}/retire", async (HttpContext http, StorageProfileService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireStorageProfile").RequirePermission(Permissions.TenantConfigManage);
    }

    private static void MapProducts(RouteGroupBuilder products)
    {
        products.MapGet("", (ProductService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, [FromQuery] string? search,
                [FromQuery] HandlingProfile? handlingProfile, CancellationToken ct) =>
                svc.ListAsync(new ProductFilter(active, search, handlingProfile), q.ToPage(), ct))
            .WithName("listProducts").RequirePermission(Permissions.CatalogRead);
        products.MapPost("", async (HttpContext http, ProductService svc, CreateProduct body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/products/{created.Id}", created);
            })
            .WithName("createProduct").RequirePermission(Permissions.CatalogManage);
        products.MapGet("/{id:guid}", async (HttpContext http, ProductService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getProduct").RequirePermission(Permissions.CatalogRead);
        products.MapPatch("/{id:guid}", async (HttpContext http, ProductService svc, Guid id, UpdateProduct body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updateProduct").RequirePermission(Permissions.CatalogManage);
        products.MapPost("/{id:guid}/retire", async (HttpContext http, ProductService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retireProduct").RequirePermission(Permissions.CatalogManage);

        products.MapGet("/{id:guid}/conversions", (ProductConversionService svc, Guid id, [AsParameters] ListQuery q, CancellationToken ct) =>
                svc.ListAsync(id, q.ToPage(), ct))
            .WithName("listProductConversions").RequirePermission(Permissions.CatalogRead);
        products.MapPost("/{id:guid}/conversions", async (HttpContext http, ProductConversionService svc, Guid id,
                CreateProductConversion body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(id, body, ct);
                // No single-conversion route exists (contract); Location names the collection that lists it.
                return Conventions.Created(http, $"/api/v1/products/{id}/conversions", created);
            })
            .WithName("createProductConversion").RequirePermission(Permissions.CatalogManage);
        products.MapPost("/{id:guid}/conversions/{conversionId:guid}/retire", async (HttpContext http, ProductConversionService svc,
                Guid id, Guid conversionId, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, conversionId, Conventions.IfMatch(http), ct)))
            .WithName("retireProductConversion").RequirePermission(Permissions.CatalogManage);

        products.MapGet("/{id:guid}/barcodes", (ProductBarcodeService svc, Guid id, [AsParameters] ListQuery q, CancellationToken ct) =>
                svc.ListAsync(id, q.ToPage(), ct))
            .WithName("listProductBarcodes").RequirePermission(Permissions.CatalogRead);
        products.MapPost("/{id:guid}/barcodes", async (HttpContext http, ProductBarcodeService svc, Guid id,
                CreateProductBarcode body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(id, body, ct);
                // No single-barcode route exists (contract); Location names the collection that lists it.
                return Conventions.Created(http, $"/api/v1/products/{id}/barcodes", created);
            })
            .WithName("createProductBarcode").RequirePermission(Permissions.CatalogManage);
        products.MapPost("/{id:guid}/barcodes/{barcodeId:guid}/retire", async (HttpContext http, ProductBarcodeService svc,
                Guid id, Guid barcodeId, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, barcodeId, Conventions.IfMatch(http), ct)))
            .WithName("retireProductBarcode").RequirePermission(Permissions.CatalogManage);
    }

    private static void MapPartners(RouteGroupBuilder partners)
    {
        partners.MapGet("", (PartnerService svc, [AsParameters] ListQuery q, [FromQuery] bool? active, [FromQuery] string? search,
                [FromQuery] PartnerType? type, CancellationToken ct) =>
                svc.ListAsync(new PartnerFilter(active, search, type), q.ToPage(), ct))
            .WithName("listPartners").RequirePermission(Permissions.CatalogRead);
        partners.MapPost("", async (HttpContext http, PartnerService svc, CreatePartner body, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(body, ct);
                return Conventions.Created(http, $"/api/v1/partners/{created.Id}", created);
            })
            .WithName("createPartner").RequirePermission(Permissions.CatalogManage);
        partners.MapGet("/{id:guid}", async (HttpContext http, PartnerService svc, Guid id, CancellationToken ct) =>
                Conventions.Ok(http, await svc.GetAsync(id, ct)))
            .WithName("getPartner").RequirePermission(Permissions.CatalogRead);
        partners.MapPatch("/{id:guid}", async (HttpContext http, PartnerService svc, Guid id, UpdatePartner body, CancellationToken ct) =>
                Conventions.Ok(http, await svc.UpdateAsync(id, Conventions.IfMatch(http), body, ct)))
            .WithName("updatePartner").RequirePermission(Permissions.CatalogManage);
        partners.MapPost("/{id:guid}/retire", async (HttpContext http, PartnerService svc, Guid id, CancellationToken ct) =>
                Conventions.Retired(http, await svc.RetireAsync(id, Conventions.IfMatch(http), ct)))
            .WithName("retirePartner").RequirePermission(Permissions.CatalogManage);
    }
}
