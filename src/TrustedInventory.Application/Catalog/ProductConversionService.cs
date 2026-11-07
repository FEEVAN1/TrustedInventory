using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record ProductConversionDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid ProductId, Guid AlternateUomId, Quantity FactorToBase, bool Active) : IVersioned
{
    public static ProductConversionDto From(ProductConversion c) =>
        new(c.Id, c.Version, c.CreatedAt, c.CreatedBy, c.UpdatedAt, c.ProductId, c.AlternateUomId, Quantity.From(c.FactorToBase), c.Active);
}

public sealed record CreateProductConversion(Guid AlternateUomId, Quantity FactorToBase);

public sealed class CreateProductConversionValidator : AbstractValidator<CreateProductConversion>
{
    public CreateProductConversionValidator()
    {
        RuleFor(x => x.AlternateUomId).NotEmpty();
        RuleFor(x => x.FactorToBase).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
    }
}

/// <summary>Alternate units of a product (section 07). The factor must be representable at the product base precision.</summary>
public sealed class ProductConversionService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateProductConversion> createValidator)
{
    public Task<ProductConversionDto> CreateAsync(Guid productId, CreateProductConversion cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var product = await ProductService.Load(s, productId, c);
            if (!await s.Db.Set<Unit>().AnyAsync(u => u.Id == cmd.AlternateUomId && u.Active, c))
                throw AppException.Validation("alternateUomId", "Unknown or retired unit.");
            if (cmd.AlternateUomId == product.BaseUomId)
                throw AppException.Validation("alternateUomId", "The alternate unit must differ from the product base unit.");
            if (decimal.Round(cmd.FactorToBase.Value, product.BasePrecision) != cmd.FactorToBase.Value)
                throw AppException.Validation("factorToBase",
                    $"factorToBase has more decimal places than the product base precision ({product.BasePrecision}).");
            // One active conversion per product and unit; retired rows stay as history (migration 0002, DEC-029).
            if (await s.Db.Set<ProductConversion>().AnyAsync(x => x.ProductId == productId && x.AlternateUomId == cmd.AlternateUomId && x.Active, c))
                throw AppException.Validation("alternateUomId", "This product already has a conversion for this unit.");

            var conversion = DomainGuard.Run(() => ProductConversion.Create(caller.TenantId, product, cmd.AlternateUomId,
                cmd.FactorToBase.Value, caller.Subject, s.Now));
            s.Db.Add(conversion);

            var dto = ProductConversionDto.From(conversion);
            s.Audit(new AuditEntry("ProductConversionCreated", "ProductConversion", conversion.Id, conversion.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<ProductConversionDto> RetireAsync(Guid productId, Guid conversionId, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var conversion = await s.Db.Set<ProductConversion>()
                .SingleOrDefaultAsync(x => x.Id == conversionId && x.ProductId == productId, c)
                ?? throw AppException.NotFound("Product conversion");
            Versioning.EnsureVersion(conversion.Version, expectedVersion);
            var before = ProductConversionDto.From(conversion);
            DomainGuard.Run(() => conversion.Retire(s.Now));

            var after = ProductConversionDto.From(conversion);
            s.Audit(new AuditEntry("ProductConversionRetired", "ProductConversion", conversionId, conversion.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<Page<ProductConversionDto>> ListAsync(Guid productId, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) =>
        {
            await ProductService.Load(s, productId, c);
            return await s.Db.Set<ProductConversion>().AsNoTracking().Where(x => x.ProductId == productId)
                .ToPageAsync(page, $"product-conversions|{productId}", cursors, ProductConversionDto.From, c);
        }, ct);
    }
}
