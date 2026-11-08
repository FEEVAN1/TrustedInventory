using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Outbound;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Outbound;

public sealed record OutboundOrderLineDto(
    Guid Id, int LineNumber, Guid ProductId, Quantity RequestedQuantity, int MinRemainingShelfLifeDays, Quantity ReservedQuantity,
    Quantity PickedQuantity, Quantity DispatchedQuantity)
{
    public static OutboundOrderLineDto From(OutboundOrderLine l) =>
        new(l.Id, l.LineNumber, l.ProductId, Quantity.From(l.RequestedBaseQty), l.MinRemainingShelfLifeDays, Quantity.From(l.AllocatedBaseQty),
            Quantity.From(l.PickedBaseQty), Quantity.From(l.DispatchedBaseQty));
}

public sealed record OutboundOrderDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid CustomerId, Guid WarehouseId, string Reference,
    DateOnly PlannedDispatchDate, OutboundOrderStatus Status, string? CancelledReason, IReadOnlyList<OutboundOrderLineDto> Lines) : IVersioned
{
    public static OutboundOrderDto From(OutboundOrder o, IEnumerable<OutboundOrderLine> lines) =>
        new(o.Id, o.Version, o.CreatedAt, o.CreatedBy, o.UpdatedAt, o.CustomerId, o.WarehouseId, o.Reference, o.PlannedDispatchDate, o.Status,
            o.CancelReason, lines.OrderBy(l => l.LineNumber).Select(OutboundOrderLineDto.From).ToList());
}

public sealed record OutboundOrderLineInput(Guid ProductId, Quantity Quantity, int? MinRemainingShelfLifeDays);

public sealed record CreateOutboundOrder(Guid CustomerId, Guid WarehouseId, string Reference, DateOnly PlannedDispatchDate,
    IReadOnlyList<OutboundOrderLineInput> Lines);

public sealed record UpdateOutboundOrder(Optional<string> Reference, Optional<DateOnly> PlannedDispatchDate,
    Optional<IReadOnlyList<OutboundOrderLineInput>> Lines);

public sealed record OutboundOrderFilter(Guid? WarehouseId, Guid? CustomerId, OutboundOrderStatus? Status, string? Reference);

public sealed class OutboundOrderLineInputValidator : AbstractValidator<OutboundOrderLineInput>
{
    public OutboundOrderLineInputValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.MinRemainingShelfLifeDays).InclusiveBetween(0, 3650);
    }
}

public sealed class CreateOutboundOrderValidator : AbstractValidator<CreateOutboundOrder>
{
    public const int MaxLines = 500;

    public CreateOutboundOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Reference).ValidCode();
        RuleFor(x => x.Lines).NotNull().Must(l => l is { Count: >= 1 and <= MaxLines }).WithMessage($"Send 1 to {MaxLines} lines.");
        RuleForEach(x => x.Lines).SetValidator(new OutboundOrderLineInputValidator());
    }
}

public sealed class UpdateOutboundOrderValidator : AbstractValidator<UpdateOutboundOrder>
{
    public UpdateOutboundOrderValidator()
    {
        RuleFor(x => x).Must(x => x.Reference.IsSet || x.PlannedDispatchDate.IsSet || x.Lines.IsSet).WithName("body").WithMessage("Change at least one field.");
        When(x => x.Reference.IsSet, () => RuleFor(x => x.Reference.Value).ValidCode().OverridePropertyName("reference"));
        When(x => x.Lines.IsSet, () =>
        {
            RuleFor(x => x.Lines.Value).NotNull().Must(l => l is { Count: >= 1 and <= CreateOutboundOrderValidator.MaxLines })
                .WithMessage($"Send 1 to {CreateOutboundOrderValidator.MaxLines} lines.").OverridePropertyName("lines");
            RuleForEach(x => x.Lines.Value).SetValidator(new OutboundOrderLineInputValidator()).OverridePropertyName("lines");
        });
    }
}

/// <summary>
/// Outbound orders (OUT-001, OUT-006 cancellation, DEC-052). Writes need orders.create for the order's warehouse; reads need
/// stock.read. Every change is audited in its transaction.
/// </summary>
public sealed class OutboundOrderService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<CreateOutboundOrder> createValidator,
    IValidator<UpdateOutboundOrder> updateValidator,
    IValidator<ReasonRequest> reasonValidator)
{
    public Task<OutboundOrderDto> CreateAsync(CreateOutboundOrder cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersCreate);
        caller.RequireMutable();
        createValidator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.OrdersCreate, cmd.WarehouseId);

        return uow.WriteAsync(async (s, c) =>
        {
            var warehouse = await ActiveWarehouseAsync(s, cmd.WarehouseId, c);
            EnsureDispatchDate(cmd.PlannedDispatchDate, warehouse, s.Now);
            var customer = await s.Db.Set<Partner>().SingleOrDefaultAsync(p => p.Id == cmd.CustomerId, c);
            if (customer is not { Active: true } || customer.Type is not (PartnerType.Customer or PartnerType.Both))
                throw AppException.Validation("customerId", "Use an active partner of type Customer or Both.");
            await EnsureReferenceFreeAsync(s, cmd.Reference, null, c);

            var order = OutboundOrder.Create(caller.TenantId, cmd.CustomerId, cmd.WarehouseId, cmd.Reference, cmd.PlannedDispatchDate, caller.Subject, s.Now);
            var lines = await BuildLinesAsync(s, order.Id, cmd.Lines, caller, c);
            s.Db.Add(order);
            s.Db.AddRange(lines);
            var dto = OutboundOrderDto.From(order, lines);
            s.Audit(new AuditEntry("OutboundOrderCreated", "OutboundOrder", order.Id, order.Version, null, null, dto));
            await s.Db.SaveChangesAsync(c);
            return dto;
        }, ct);
    }

    public Task<OutboundOrderDto> UpdateAsync(Guid id, int expectedVersion, UpdateOutboundOrder cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersCreate);
        caller.RequireMutable();
        updateValidator.ValidateOrThrow(cmd);

        return uow.WriteAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.OrdersCreate, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            var before = OutboundOrderDto.From(order, lines);
            if (cmd.Reference.IsSet && cmd.Reference.Value != order.Reference)
                await EnsureReferenceFreeAsync(s, cmd.Reference.Value, order.Id, c);
            if (cmd.PlannedDispatchDate.IsSet)
                EnsureDispatchDate(cmd.PlannedDispatchDate.Value, await ActiveWarehouseAsync(s, order.WarehouseId, c), s.Now);
            DomainGuard.Run(() => order.EditDraft(cmd.Reference.IsSet ? cmd.Reference.Value : null,
                cmd.PlannedDispatchDate.IsSet ? cmd.PlannedDispatchDate.Value : null, s.Now));
            if (cmd.Lines.IsSet)
            {
                // Replace all lines (DEC-051): delete first so new line numbers never collide with old ones.
                var replacement = await BuildLinesAsync(s, order.Id, cmd.Lines.Value, caller, c);
                s.Db.RemoveRange(lines);
                await s.Db.SaveChangesAsync(c);
                s.Db.AddRange(replacement);
                lines = replacement;
            }
            var after = OutboundOrderDto.From(order, lines);
            s.Audit(new AuditEntry("OutboundOrderUpdated", "OutboundOrder", id, order.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    /// <summary>OUT-001: confirmation rechecks products, quantities and the dispatch date against today's data.</summary>
    public Task<OutboundOrderDto> ConfirmAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersCreate);
        caller.RequireMutable();
        return uow.WriteAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.OrdersCreate, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            EnsureDispatchDate(order.PlannedDispatchDate, await ActiveWarehouseAsync(s, order.WarehouseId, c), s.Now);
            await BuildLinesAsync(s, order.Id, lines.OrderBy(l => l.LineNumber)
                .Select(l => new OutboundOrderLineInput(l.ProductId, Quantity.From(l.RequestedBaseQty), l.MinRemainingShelfLifeDays)).ToList(), caller, c);
            var before = OutboundOrderDto.From(order, lines);
            DomainGuard.Run(() => order.Confirm(caller.Subject, s.Now));
            var after = OutboundOrderDto.From(order, lines);
            s.Audit(new AuditEntry("OutboundOrderConfirmed", "OutboundOrder", id, order.Version, null, before, after));
            await s.Db.SaveChangesAsync(c);
            return after;
        }, ct);
    }

    /// <summary>
    /// OUT-006 (DEC-052): releases every active reservation, then cancels. Lock order: order row → lines → reservations →
    /// buckets → audit stream. Refused while anything is picked or dispatched.
    /// </summary>
    public Task<CommandOutcome> CancelAsync(Guid idempotencyKey, Guid id, int expectedVersion, ReasonRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.OrdersCreate);
        reasonValidator.ValidateOrThrow(cmd);
        var payload = JsonSerializer.SerializeToNode(new { id, expectedVersion, cmd.Reason }, JsonDefaults.Options);

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /orders/{id}/cancel", payload), async (s, c) =>
        {
            if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.outbound_order WHERE id = {id} FOR UPDATE").ToListAsync(c)).Count == 0)
                throw AppException.NotFound("Order");
            await s.Db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM ti.outbound_order_line WHERE order_id = {id} ORDER BY line_number FOR UPDATE").ToListAsync(c);
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.OrdersCreate, order.WarehouseId);
            Versioning.EnsureVersion(order.Version, expectedVersion);
            var before = OutboundOrderDto.From(order, lines);
            if (lines.Any(l => l.PickedBaseQty > 0 || l.DispatchedBaseQty > 0))
                throw AppException.InvalidState("Picked stock must be unpicked before the order can be cancelled (OUT-006).");

            var lineIds = lines.Select(l => l.Id).ToList();
            var releasedIds = await ReservationRelease.ReleaseActiveAsync(s, lineIds, lines.ToDictionary(l => l.Id), c);
            DomainGuard.Run(() => order.Cancel(cmd.Reason, lines.Select(l => l.Progress), s.Now));

            var after = OutboundOrderDto.From(order, lines);
            s.Audit(new AuditEntry("OutboundOrderCancelled", "OutboundOrder", id, order.Version, cmd.Reason, before,
                new { order = after, releasedReservations = releasedIds }));
            s.Publish("OutboundOrderCancelled", new { orderId = id, releasedReservations = releasedIds });
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(200, JsonSerializer.SerializeToNode(after, JsonDefaults.Options));
        }, ct);
    }

    public Task<OutboundOrderDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var (order, lines) = await LoadAsync(s, id, c);
            caller.RequireWarehouse(Permissions.StockRead, order.WarehouseId);
            return OutboundOrderDto.From(order, lines);
        }, ct);
    }

    public Task<Page<OutboundOrderDto>> ListAsync(OutboundOrderFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<OutboundOrder>().AsNoTracking();
            if (scope is not null)
                query = query.Where(o => scope.Contains(o.WarehouseId));
            if (filter.WarehouseId is { } w)
                query = query.Where(o => o.WarehouseId == w);
            if (filter.CustomerId is { } cid)
                query = query.Where(o => o.CustomerId == cid);
            if (filter.Status is { } status)
                query = query.Where(o => o.Status == status);
            if (filter.Reference is { Length: > 0 } reference)
                query = query.Where(o => o.Reference == reference);
            var orders = await query.ToPageAsync(page, $"orders|{filter.WarehouseId}|{filter.CustomerId}|{filter.Status}|{filter.Reference}", cursors, o => o, c);
            var ids = orders.Items.Select(o => o.Id).ToList();
            var lines = (await s.Db.Set<OutboundOrderLine>().AsNoTracking().Where(l => ids.Contains(l.OrderId)).ToListAsync(c)).ToLookup(l => l.OrderId);
            return new Page<OutboundOrderDto>(orders.Items.Select(o => OutboundOrderDto.From(o, lines[o.Id])).ToList(), orders.NextCursor);
        }, ct);
    }

    private static async Task<List<OutboundOrderLine>> BuildLinesAsync(ITenantSession s, Guid orderId, IReadOnlyList<OutboundOrderLineInput> inputs,
        CallerContext caller, CancellationToken ct)
    {
        var ids = inputs.Select(i => i.ProductId).Distinct().ToList();
        var products = await s.Db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = new List<OutboundOrderLine>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var f = $"lines[{i}]";
            if (!products.TryGetValue(input.ProductId, out var product) || !product.Active)
                throw AppException.Validation($"{f}.productId", "Unknown or retired product.");
            var qty = input.Quantity.Value;
            if (decimal.Round(qty, product.BasePrecision) != qty)
                throw AppException.Validation($"{f}.quantity", $"The quantity exceeds the product's precision of {product.BasePrecision} decimal places.");
            if (product.TrackingMode is TrackingMode.Serial or TrackingMode.LotAndSerial && decimal.Truncate(qty) != qty)
                throw AppException.Validation($"{f}.quantity", "Serial-tracked products are ordered in whole units.");
            lines.Add(OutboundOrderLine.Create(caller.TenantId, orderId, i + 1, product.Id, qty, input.MinRemainingShelfLifeDays ?? 0, caller.Subject, s.Now));
        }
        return lines;
    }

    private static async Task<Warehouse> ActiveWarehouseAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var warehouse = await s.Db.Set<Warehouse>().AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct)
            ?? throw AppException.Validation("warehouseId", "Unknown warehouse.");
        return warehouse.Active ? warehouse : throw AppException.Validation("warehouseId", "The warehouse is retired.");
    }

    /// <summary>OUT-001: not before today in the warehouse's time zone.</summary>
    private static void EnsureDispatchDate(DateOnly date, Warehouse warehouse, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(warehouse.TimeZone)).DateTime);
        if (date < today)
            throw AppException.Validation("plannedDispatchDate", $"The dispatch date cannot be before today ({today:yyyy-MM-dd}) in the warehouse.");
    }

    private static async Task EnsureReferenceFreeAsync(ITenantSession s, string reference, Guid? exceptId, CancellationToken ct)
    {
        if (await s.Db.Set<OutboundOrder>().AnyAsync(o => o.Reference == reference && o.Id != exceptId, ct))
            throw AppException.Validation("reference", "An order with this reference already exists.");
    }

    private static async Task<(OutboundOrder Order, List<OutboundOrderLine> Lines)> LoadAsync(ITenantSession s, Guid id, CancellationToken ct)
    {
        var order = await s.Db.Set<OutboundOrder>().SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound("Order");
        var lines = await s.Db.Set<OutboundOrderLine>().Where(l => l.OrderId == id).ToListAsync(ct);
        return (order, lines);
    }
}

/// <summary>
/// Returns active reservations to their buckets (cancellation now; deallocation and expiry later). Locks the reservations
/// and then their buckets in id order (migration-design §5) and decreases bucket reserved quantity; on hand is untouched.
/// </summary>
internal static class ReservationRelease
{
    public static async Task<List<Guid>> ReleaseActiveAsync(ITenantSession s, IReadOnlyCollection<Guid> lineIds, IReadOnlyDictionary<Guid, OutboundOrderLine> lines,
        CancellationToken ct)
    {
        // Lock order (DEC-054): picks → live reservations → buckets, all by id.
        var ids = lineIds.ToArray();
        await s.Db.Database.SqlQuery<Guid>($"""
            SELECT p.id AS "Value" FROM ti.pick p JOIN ti.reservation r ON r.tenant_id = p.tenant_id AND r.id = p.reservation_id
            WHERE r.order_line_id = ANY({ids}) AND r.state = 'Active' ORDER BY p.id FOR UPDATE OF p
            """).ToListAsync(ct);
        var liveIds = await s.Db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM ti.reservation WHERE order_line_id = ANY({ids}) AND state IN ('Active','Picked') ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var live = await s.Db.Set<Reservation>().Where(r => liveIds.Contains(r.Id)).ToListAsync(ct);
        var reservations = live.Where(r => r.State == ReservationState.Active).OrderBy(r => r.Id).ToList();
        if (reservations.Count == 0)
            return [];
        var activeIds = reservations.Select(r => r.Id).ToList();
        var picks = await s.Db.Set<Pick>().Where(p => activeIds.Contains(p.ReservationId)).ToDictionaryAsync(p => p.ReservationId, ct);
        var bucketIds = reservations.Select(r => r.BucketId).Distinct().Order().ToArray();
        await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.stock_bucket WHERE id = ANY({bucketIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        foreach (var reservation in reservations)
        {
            var released = reservation.Qty;
            await s.Db.Database.ExecuteSqlAsync($"""
                UPDATE ti.stock_bucket SET reserved_qty = reserved_qty - {released}, updated_at = {s.Now}, version = version + 1
                WHERE id = {reservation.BucketId}
                """, ct);
            // A part-picked task keeps what was picked: the picked child folds into the own segment (DEC-054).
            var child = live.SingleOrDefault(r => r.ParentReservationId == reservation.Id && r.State == ReservationState.Picked);
            if (child is not null)
                reservation.ReleaseKeepingPicked(child, s.Now);
            else
                reservation.Release(s.Now);
            if (picks.TryGetValue(reservation.Id, out var pick) && pick.IsOpen)
                pick.CloseRemainder(s.Now);
            lines[reservation.OrderLineId].ReleaseAllocation(released, s.Now);
        }
        return activeIds;
    }
}
