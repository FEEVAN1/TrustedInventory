using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record PurchaseOrderLineDto(
    Guid Id, int LineNumber, Guid ProductId, Guid UomId, Quantity Quantity, Quantity FactorToBase,
    Quantity OrderedBaseQuantity, Quantity ReceivedBaseQuantity, Quantity OutstandingBaseQuantity)
{
    public static PurchaseOrderLineDto From(PurchaseOrderLine l) =>
        new(l.Id, l.LineNumber, l.ProductId, l.UomId, Quantity.From(l.OrderedUomQty), Quantity.From(l.UomFactorToBase),
            Quantity.From(l.OrderedBaseQty), Quantity.From(l.ReceivedBaseQty), Quantity.From(l.OutstandingBaseQty));
}

public sealed record PurchaseOrderDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt,
    Guid SupplierId, Guid WarehouseId, string Reference, PurchaseOrderStatus Status, DateTimeOffset? ApprovedAt,
    string? ApprovedBy, string? ShortCloseReason, IReadOnlyList<PurchaseOrderLineDto> Lines) : IVersioned
{
    public static PurchaseOrderDto From(PurchaseOrder o, IEnumerable<PurchaseOrderLine> lines) =>
        new(o.Id, o.Version, o.CreatedAt, o.CreatedBy, o.UpdatedAt, o.SupplierId, o.WarehouseId, o.Reference, o.Status,
            o.ApprovedAt, o.ApprovedBy, o.ShortCloseReason, lines.OrderBy(l => l.LineNumber).Select(PurchaseOrderLineDto.From).ToList());
}

public sealed record PurchaseOrderLineInput(Guid ProductId, Quantity Quantity, Guid UomId);

public sealed record CreatePurchaseOrder(Guid SupplierId, Guid WarehouseId, string Reference, IReadOnlyList<PurchaseOrderLineInput> Lines);

public sealed record UpdatePurchaseOrder(Optional<string> Reference, Optional<IReadOnlyList<PurchaseOrderLineInput>> Lines);

public sealed record PurchaseOrderFilter(Guid? WarehouseId, Guid? SupplierId, PurchaseOrderStatus? Status, string? Reference);

public sealed class PurchaseOrderLineInputValidator : AbstractValidator<PurchaseOrderLineInput>
{
    public PurchaseOrderLineInputValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.UomId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
    }
}

public sealed class CreatePurchaseOrderValidator : AbstractValidator<CreatePurchaseOrder>
{
    public const int MaxLines = 500;

    public CreatePurchaseOrderValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Reference).ValidCode();
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= MaxLines }).WithMessage($"Send 1 to {MaxLines} lines.");
        RuleForEach(x => x.Lines).SetValidator(new PurchaseOrderLineInputValidator());
    }
}

public sealed class UpdatePurchaseOrderValidator : AbstractValidator<UpdatePurchaseOrder>
{
    public UpdatePurchaseOrderValidator()
    {
        RuleFor(x => x).Must(x => x.Reference.IsSet || x.Lines.IsSet).WithName("body").WithMessage("Change at least one field.");
        When(x => x.Reference.IsSet, () => RuleFor(x => x.Reference.Value).ValidCode().OverridePropertyName("reference"));
        When(x => x.Lines.IsSet, () =>
        {
            RuleFor(x => x.Lines.Value).NotNull()
                .Must(l => l is { Count: >= 1 and <= CreatePurchaseOrderValidator.MaxLines })
                .WithMessage($"Send 1 to {CreatePurchaseOrderValidator.MaxLines} lines.").OverridePropertyName("lines");
            RuleForEach(x => x.Lines.Value).SetValidator(new PurchaseOrderLineInputValidator()).OverridePropertyName("lines");
        });
    }
}

/// <summary>
/// Purchase orders (INB-001, DEC-047). Writes need the warehouse-scoped permission for the order's warehouse; every
/// change is audited in the same transaction. Lines freeze the unit factor; approved orders cannot change their lines
/// (enforced here and by the 0007 trigger).
/// </summary>
public sealed class PurchaseOrderService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreatePurchaseOrder> createValidator,
    IValidator<UpdatePurchaseOrder> updateValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<PurchaseOrderDto> CreateAsync(CreatePurchaseOrder cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PurchaseOrdersCreate);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.PurchaseOrdersCreate, cmd.WarehouseId);

        return uow.WriteAsync(async (s, c) =>
        {
            var warehouse = await s.Db.Set<Warehouse>().SingleOrDefaultAsync(w => w.Id == cmd.WarehouseId, c)
                ?? throw AppException.Validation("warehouseId", "Unknown warehouse.");
            if (!warehouse.Active)
                throw AppException.Validation("warehouseId", "The warehouse is retired.");
            var supplier = await s.Db.Set<Partner>().SingleOrDefaultAsync(p => p.Id == cmd.SupplierId, c);
            if (supplier is not { Active: true } || supplier.Type is not (PartnerType.Supplier or PartnerType.Both))
                throw AppException.Validation("supplierId", "Use an active partner of type Supplier or Both.");
            await EnsureReferenceFreeAsync(s, cmd.Reference, null, c);

            var order = PurchaseOrder.Create(caller.TenantId, cmd.SupplierId, cmd.WarehouseId, cmd.Reference, caller.Subject, s.Now);
            var lines = await BuildLinesAsync(s, order.Id, cmd.Lines, caller, c);
            s.Db.Add(order);
            s.Db.AddRange(lines);

            var dto = PurchaseOrderDto.From(order, lines);
            s.Audit(new AuditEntry("PurchaseOrderCreated", "PurchaseOrder", order.Id, order.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<PurchaseOrderDto> UpdateAsync(Guid id, int expectedVersion, UpdatePurchaseOrder cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PurchaseOrdersCreate);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.PurchaseOrdersCreate, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            var before = PurchaseOrderDto.From(order, lines);
            if (cmd.Reference.IsSet && cmd.Reference.Value != order.Reference)
                await EnsureReferenceFreeAsync(s, cmd.Reference.Value, order.Id, c);
            DomainGuard.Run(() => order.EditDraft(cmd.Reference.IsSet ? cmd.Reference.Value : null, s.Now));

            if (cmd.Lines.IsSet)
            {
                // Replace all lines (DEC-047): delete first so new line numbers never collide with old ones.
                var replacement = await BuildLinesAsync(s, order.Id, cmd.Lines.Value, caller, c);
                s.Db.RemoveRange(lines);
                await s.Db.SaveChangesAsync(c);
                s.Db.AddRange(replacement);
                lines = replacement;
            }

            var after = PurchaseOrderDto.From(order, lines);
            s.Audit(new AuditEntry("PurchaseOrderUpdated", "PurchaseOrder", id, order.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    public Task<PurchaseOrderDto> ApproveAsync(Guid id, int expectedVersion, CancellationToken ct) =>
        TransitionAsync(id, expectedVersion, "PurchaseOrderApproved", null, (order, _, caller, now) => order.Approve(caller.Subject, now), ct);

    public Task<PurchaseOrderDto> CancelAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        reasonValidator.ValidateOrThrow(cmd);
        return TransitionAsync(id, expectedVersion, "PurchaseOrderCancelled", cmd.Reason,
            (order, lines, _, now) => order.Cancel(lines.Any(l => l.ReceivedBaseQty > 0), now), ct);
    }

    public Task<PurchaseOrderDto> ShortCloseAsync(Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        reasonValidator.ValidateOrThrow(cmd);
        return TransitionAsync(id, expectedVersion, "PurchaseOrderShortClosed", cmd.Reason,
            (order, _, _, now) => order.ShortClose(cmd.Reason, now), ct);
    }

    public Task<PurchaseOrderDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.StockRead, order.WarehouseId);
            return PurchaseOrderDto.From(order, lines);
        }, ct);
    }

    public Task<Page<PurchaseOrderDto>> ListAsync(PurchaseOrderFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<PurchaseOrder>().AsNoTracking();
            if (scope is not null)
                query = query.Where(o => scope.Contains(o.WarehouseId));
            if (filter.WarehouseId is { } wid)
                query = query.Where(o => o.WarehouseId == wid);
            if (filter.SupplierId is { } sid)
                query = query.Where(o => o.SupplierId == sid);
            if (filter.Status is { } status)
                query = query.Where(o => o.Status == status);
            if (filter.Reference is { Length: > 0 } reference)
                query = query.Where(o => o.Reference == reference);
            var orders = await query.ToPageAsync(page,
                $"purchase-orders|{filter.WarehouseId}|{filter.SupplierId}|{filter.Status}|{filter.Reference}", cursors, o => o, c);

            var ids = orders.Items.Select(o => o.Id).ToList();
            var lines = (await s.Db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => ids.Contains(l.PurchaseOrderId)).ToListAsync(c))
                .ToLookup(l => l.PurchaseOrderId);
            return new Page<PurchaseOrderDto>(orders.Items.Select(o => PurchaseOrderDto.From(o, lines[o.Id])).ToList(), orders.NextCursor);
        }, ct);
    }

    /// <summary>Approve, cancel and short-close: purchase-orders.approve for the order's warehouse.</summary>
    private Task<PurchaseOrderDto> TransitionAsync(Guid id, int expectedVersion, string eventType, string? reason,
        Action<PurchaseOrder, IReadOnlyList<PurchaseOrderLine>, CallerContext, DateTimeOffset> change, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.PurchaseOrdersApprove);
        caller.RequireMutable();
        return uow.WriteAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.PurchaseOrdersApprove, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            var before = PurchaseOrderDto.From(order, lines);
            DomainGuard.Run(() => change(order, lines, caller, s.Now));
            var after = PurchaseOrderDto.From(order, lines);
            s.Audit(new AuditEntry(eventType, "PurchaseOrder", id, order.Version, reason, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    /// <summary>
    /// Resolves each input line: active product, its base unit (factor 1) or an active conversion, and a base quantity
    /// within the product's base precision (whole units for serial tracking).
    /// </summary>
    private static async Task<List<PurchaseOrderLine>> BuildLinesAsync(ITenantSession s, Guid orderId,
        IReadOnlyList<PurchaseOrderLineInput> inputs, CallerContext caller, CancellationToken ct)
    {
        var productIds = inputs.Select(i => i.ProductId).Distinct().ToList();
        var products = await s.Db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var unitIds = inputs.Select(i => i.UomId).Distinct().ToList();
        var activeUnits = (await s.Db.Set<Unit>().AsNoTracking().Where(u => unitIds.Contains(u.Id) && u.Active).Select(u => u.Id).ToListAsync(ct))
            .ToHashSet();
        var conversions = await s.Db.Set<ProductConversion>().AsNoTracking()
            .Where(cv => productIds.Contains(cv.ProductId) && unitIds.Contains(cv.AlternateUomId) && cv.Active)
            .ToListAsync(ct);

        var lines = new List<PurchaseOrderLine>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"lines[{i}]";
            if (!products.TryGetValue(input.ProductId, out var product) || !product.Active)
                throw AppException.Validation($"{field}.productId", "Unknown or retired product.");
            if (!activeUnits.Contains(input.UomId))
                throw AppException.Validation($"{field}.uomId", "Unknown or retired unit.");
            decimal factor;
            if (input.UomId == product.BaseUomId)
                factor = 1m;
            else
                factor = conversions.FirstOrDefault(cv => cv.ProductId == product.Id && cv.AlternateUomId == input.UomId)?.FactorToBase
                    ?? throw AppException.Validation($"{field}.uomId", "The product has no active conversion for this unit.");

            var quantity = input.Quantity.Value;
            var baseQty = quantity * factor;
            if (decimal.Round(baseQty, product.BasePrecision) != baseQty)
                throw AppException.Validation($"{field}.quantity",
                    $"The base quantity {Quantity.Format(baseQty)} exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (product.TrackingMode is TrackingMode.Serial or TrackingMode.LotAndSerial && decimal.Truncate(baseQty) != baseQty)
                throw AppException.Validation($"{field}.quantity", "Serial-tracked products are ordered in whole units.");

            lines.Add(PurchaseOrderLine.Create(caller.TenantId, orderId, i + 1, product.Id, input.UomId, factor, quantity, baseQty,
                caller.Subject, s.Now));
        }
        return lines;
    }

    private static async Task EnsureReferenceFreeAsync(ITenantSession s, string reference, Guid? exceptId, CancellationToken ct)
    {
        if (await s.Db.Set<PurchaseOrder>().AnyAsync(o => o.Reference == reference && o.Id != exceptId, ct))
            throw AppException.Validation("reference", "A purchase order with this reference already exists.");
    }

    private static async Task<(PurchaseOrder Order, List<PurchaseOrderLine> Lines)> LoadAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var order = await s.Db.Set<PurchaseOrder>().SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound("Purchase order");
        var lines = await s.Db.Set<PurchaseOrderLine>().Where(l => l.PurchaseOrderId == id).ToListAsync(ct);
        return (order, lines);
    }
}
