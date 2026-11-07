using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record ProductBarcodeDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid ProductId, string Gtin, bool Active) : IVersioned
{
    public static ProductBarcodeDto From(ProductBarcode b) =>
        new(b.Id, b.Version, b.CreatedAt, b.CreatedBy, b.UpdatedAt, b.ProductId, b.Gtin, b.Active);
}

public sealed record CreateProductBarcode(string Gtin);

/// <summary>
/// GTIN-to-product mappings used by receiving scans (INB-002). Input GTIN-8/12/13/14 is stored as GTIN-14 with
/// leading zeros; one active product per GTIN so a scan never maps ambiguously.
/// </summary>
public sealed class ProductBarcodeService(ITenantUnitOfWork uow, ICallerAccessor callers, ICursorCodec cursors)
{
    public Task<ProductBarcodeDto> CreateAsync(Guid productId, CreateProductBarcode cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        if (!Gtin.TryNormalize(cmd.Gtin, out var gtin14))
            throw AppException.Validation("gtin", "Use an 8, 12, 13 or 14 digit GTIN with a valid check digit.");

        return uow.WriteAsync(async (s, c) =>
        {
            var product = await ProductService.Load(s, productId, c);
            if (await s.Db.Set<ProductBarcode>().AnyAsync(b => b.Gtin == gtin14 && b.Active, c))
                throw AppException.Validation("gtin", "This GTIN is already mapped to an active product.");

            var barcode = DomainGuard.Run(() => ProductBarcode.Create(caller.TenantId, product, gtin14, caller.Subject, s.Now));
            s.Db.Add(barcode);

            var dto = ProductBarcodeDto.From(barcode);
            s.Audit(new AuditEntry("ProductBarcodeCreated", "ProductBarcode", barcode.Id, barcode.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<ProductBarcodeDto> RetireAsync(Guid productId, Guid barcodeId, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var barcode = await s.Db.Set<ProductBarcode>().SingleOrDefaultAsync(b => b.Id == barcodeId && b.ProductId == productId, c)
                ?? throw AppException.NotFound("Product barcode");
            Versioning.EnsureVersion(barcode.Version, expectedVersion);
            var before = ProductBarcodeDto.From(barcode);
            DomainGuard.Run(() => barcode.Retire(s.Now));

            var after = ProductBarcodeDto.From(barcode);
            s.Audit(new AuditEntry("ProductBarcodeRetired", "ProductBarcode", barcodeId, barcode.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<Page<ProductBarcodeDto>> ListAsync(Guid productId, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            await ProductService.Load(s, productId, c);
            return await s.Db.Set<ProductBarcode>().AsNoTracking().Where(b => b.ProductId == productId)
                .ToPageAsync(page, $"product-barcodes|{productId}", cursors, ProductBarcodeDto.From, c);
        }, ct);
    }
}
