using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Catalog;

public sealed record ProductDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    string Sku, string Name, HandlingProfile HandlingProfile, TrackingMode TrackingMode, Guid BaseUomId, int BasePrecision,
    RotationRule RotationRule, Guid? StorageProfileId, string? HazardClass, bool Active) : IVersioned
{
    public static ProductDto From(Product p) =>
        new(p.Id, p.Version, p.CreatedAt, p.CreatedBy, p.UpdatedAt, p.Sku, p.Name, p.HandlingProfile, p.TrackingMode, p.BaseUomId,
            p.BasePrecision, p.RotationRule, p.StorageProfileId, p.HazardClass, p.Active);
}

public sealed record CreateProduct(
    string Sku, string Name, HandlingProfile HandlingProfile, TrackingMode TrackingMode, Guid BaseUomId, int BasePrecision,
    RotationRule? RotationRule, Guid? StorageProfileId, string? HazardClass);

public sealed record UpdateProduct(
    Optional<string> Name, Optional<RotationRule> RotationRule, Optional<Guid?> StorageProfileId, Optional<string?> HazardClass,
    string Reason);

public sealed record ProductFilter(bool? Active, string? Search, HandlingProfile? HandlingProfile);

public sealed class CreateProductValidator : AbstractValidator<CreateProduct>
{
    public CreateProductValidator()
    {
        // The database stores SKUs as trimmed codes of at most 64 characters.
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(64).Must(s => s == s.Trim()).WithMessage("Remove leading or trailing spaces.");
        RuleFor(x => x.Name).ValidName();
        RuleFor(x => x.HandlingProfile).IsInEnum();
        RuleFor(x => x.TrackingMode).IsInEnum();
        RuleFor(x => x.BaseUomId).NotEmpty();
        RuleFor(x => x.BasePrecision).InclusiveBetween(0, 6);
        RuleFor(x => x.TrackingMode).Must(t => t is TrackingMode.Lot or TrackingMode.LotAndSerial)
            .When(x => x.HandlingProfile == HandlingProfile.Medical)
            .WithMessage("Medical products must be tracked by Lot or LotAndSerial.");
        RuleFor(x => x.RotationRule).Must(r => r is null or RotationRule.FEFO)
            .When(x => x.HandlingProfile == HandlingProfile.Medical)
            .WithMessage("Medical products use FEFO rotation only.");
        // CFG-002 (DEC-100): a medical product is active from creation, so it needs its temperature limits and excursion policy then.
        RuleFor(x => x.StorageProfileId).NotNull()
            .When(x => x.HandlingProfile == HandlingProfile.Medical)
            .WithMessage("Medical products need a storage profile with temperature limits and an excursion policy.");
        RuleFor(x => x.BasePrecision).Equal(0)
            .When(x => x.TrackingMode is TrackingMode.Serial or TrackingMode.LotAndSerial)
            .WithMessage("Serial-tracked products require basePrecision 0 (whole units).");
        When(x => x.HazardClass is not null, () => RuleFor(x => x.HazardClass!).ValidCode());
    }
}

public sealed class UpdateProductValidator : AbstractValidator<UpdateProduct>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x).Must(x => x.Name.IsSet || x.RotationRule.IsSet || x.StorageProfileId.IsSet || x.HazardClass.IsSet)
            .WithName("body").WithMessage("Change at least one field.");
        When(x => x.Name.IsSet, () => RuleFor(x => x.Name.Value).ValidName().OverridePropertyName("name"));
        When(x => x.RotationRule.IsSet, () => RuleFor(x => x.RotationRule.Value).IsInEnum().OverridePropertyName("rotationRule"));
        When(x => x.HazardClass.IsSet && x.HazardClass.Value is not null, () =>
            RuleFor(x => x.HazardClass.Value!).ValidCode().OverridePropertyName("hazardClass"));
    }
}

/// <summary>
/// Products (section 07). Handling profile, tracking mode, base unit and precision are fixed at creation.
/// A product may reference only an active storage profile, which always carries limits and an excursion
/// policy (CFG-002). Active products count against the plan's active_skus quota (x-quota, DEC-045).
/// </summary>
public sealed class ProductService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IQuotaGate quota,
    IValidator<CreateProduct> createValidator,
    IValidator<UpdateProduct> updateValidator)
{
    public Task<ProductDto> CreateAsync(CreateProduct cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            // SAS-005 order: reserve the quota, then domain validation; a rejection rolls the reservation back.
            await quota.ReserveAsync(s, QuotaKey.ActiveSkus, 1, c);
            if (await s.Db.Set<Product>().AnyAsync(p => p.Sku == cmd.Sku, c))
                throw AppException.Validation("sku", "A product with this SKU already exists.");
            if (!await s.Db.Set<Unit>().AnyAsync(u => u.Id == cmd.BaseUomId && u.Active, c))
                throw AppException.Validation("baseUomId", "Unknown or retired unit.");
            await EnsureActiveProfile(s, cmd.StorageProfileId, c);

            var product = DomainGuard.Run(() => Product.Create(caller.TenantId, cmd.Sku, cmd.Name, cmd.HandlingProfile,
                cmd.TrackingMode, cmd.BaseUomId, (short)cmd.BasePrecision, cmd.RotationRule, cmd.StorageProfileId, cmd.HazardClass,
                caller.Subject, s.Now));
            s.Db.Add(product);

            var dto = ProductDto.From(product);
            s.Audit(new AuditEntry("ProductCreated", "Product", product.Id, product.Version, null, null, dto));
            s.Publish("ProductCreated", new { productId = product.Id, product.Sku });
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<ProductDto> UpdateAsync(Guid id, int expectedVersion, UpdateProduct cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var product = await Load(s, id, c);
            Versioning.EnsureVersion(product.Version, expectedVersion);
            if (cmd.StorageProfileId.IsSet)
                await EnsureActiveProfile(s, cmd.StorageProfileId.Value, c);
            if (product.HandlingProfile == HandlingProfile.Medical && cmd.StorageProfileId is { IsSet: true, Value: null })
                throw AppException.Validation("storageProfileId", "Medical products need a storage profile with temperature limits and an excursion policy.");
            var before = ProductDto.From(product);

            DomainGuard.Run(() => product.Update(cmd.Name.GetValueOrDefault(product.Name),
                cmd.RotationRule.GetValueOrDefault(product.RotationRule),
                cmd.StorageProfileId.GetValueOrDefault(product.StorageProfileId),
                cmd.HazardClass.GetValueOrDefault(product.HazardClass), s.Now));

            var after = ProductDto.From(product);
            s.Audit(new AuditEntry("ProductUpdated", "Product", id, product.Version, cmd.Reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ProductDto> RetireAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();

        return uow.WriteAsync(async (s, c) =>
        {
            var product = await Load(s, id, c);
            Versioning.EnsureVersion(product.Version, expectedVersion);
            var before = ProductDto.From(product);
            // M2 adds: a product with stock on hand cannot be retired.
            DomainGuard.Run(() => product.Retire(s.Now));
            // Before the flush: a counter seeded now still counts this product once, then releases it (DEC-045).
            await quota.ReleaseAsync(s, QuotaKey.ActiveSkus, 1, c);

            var after = ProductDto.From(product);
            s.Audit(new AuditEntry("ProductRetired", "Product", id, product.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<ProductDto> GetAsync(Guid id, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync(async (s, c) => ProductDto.From(await Load(s, id, c)), ct);
    }

    public Task<Page<ProductDto>> ListAsync(ProductFilter filter, PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        return uow.ReadAsync((s, c) =>
        {
            var query = s.Db.Set<Product>().AsNoTracking();
            if (filter.Active is { } active)
                query = query.Where(p => p.Active == active);
            if (filter.HandlingProfile is { } profile)
                query = query.Where(p => p.HandlingProfile == profile);
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(p => p.Sku.ToLower().Contains(term) || p.Name.ToLower().Contains(term));
            }
            return query.ToPageAsync(page, $"products|{filter.Active}|{filter.HandlingProfile}|{filter.Search}", cursors, ProductDto.From, c);
        }, ct);
    }

    internal static async Task<Product> Load(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Set<Product>().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound("Product");

    private static async Task EnsureActiveProfile(ITenantSession s, Guid? profileId, CancellationToken ct)
    {
        if (profileId is { } pid && !await s.Db.Set<StorageProfile>().AnyAsync(p => p.Id == pid && p.Active, ct))
            throw AppException.Validation("storageProfileId", "Unknown or retired storage profile.");
    }
}
