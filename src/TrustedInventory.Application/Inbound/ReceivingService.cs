using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;
using TrustedInventory.Domain.Locations;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Inbound;

public sealed record ReceiveItem(
    Guid PurchaseOrderLineId, Guid ProductId, Quantity Quantity, Guid UomId, string? LotNumber, DateOnly? ExpirationDate,
    IReadOnlyList<string>? SerialNumbers, decimal? InitialTemperatureCelsius, Guid TargetBinId, string? SupplierReference,
    string? ScannedBarcode, InspectionResult? InspectionResult, string? ExceptionReason);

public sealed record ReceiveInventory(Guid WarehouseId, Guid PurchaseOrderId, IReadOnlyList<ReceiveItem> Items);

public sealed record ReceiveResultLine(Guid Id, Guid? LotId, QualityStatus QualityStatus, Guid BucketId);

public sealed record ReceiveResult(Guid ReceiptId, ReceiptStatus Status, IReadOnlyList<ReceiveResultLine> Lines, int Version);

public sealed record ReceiptLineDto(
    Guid Id, Guid PurchaseOrderLineId, Guid ProductId, Guid? LotId, IReadOnlyList<Guid> SerialIds, Guid UomId, Quantity FactorToBase,
    Quantity BaseQuantity, decimal? MeasuredTemperatureCelsius, Guid StagingBinId, QualityStatus QualityStatus,
    InspectionResult InspectionResult, string? ExceptionReason, Guid BucketId, Quantity PutawayRemainingQuantity);

public sealed record ReceiptDto(
    Guid Id, int Version, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset UpdatedAt, Guid PurchaseOrderId,
    Guid WarehouseId, ReceiptStatus Status, DateTimeOffset ReceivedAt, IReadOnlyList<ReceiptLineDto> Lines) : IVersioned;

public sealed record ReceiptFilter(Guid? WarehouseId, Guid? PurchaseOrderId, ReceiptStatus? Status);

public sealed class ReceiveItemValidator : AbstractValidator<ReceiveItem>
{
    public ReceiveItemValidator()
    {
        RuleFor(x => x.PurchaseOrderLineId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.UomId).NotEmpty();
        RuleFor(x => x.TargetBinId).NotEmpty();
        RuleFor(x => x.Quantity).Must(Quantity.IsValidPositive).WithMessage(Quantity.PositiveMessage);
        RuleFor(x => x.LotNumber!).Must(BeIdentifier).WithMessage("Use 1 to 100 characters without leading or trailing spaces.")
            .When(x => x.LotNumber is not null);
        RuleFor(x => x.SerialNumbers!).Must(l => l.Count <= 10000).WithMessage("At most 10000 serial numbers.")
            .Must(l => l.Distinct(StringComparer.Ordinal).Count() == l.Count).WithMessage("Serial numbers must be unique.")
            .When(x => x.SerialNumbers is not null);
        RuleForEach(x => x.SerialNumbers).Must(BeIdentifier!).WithMessage("Use 1 to 100 characters without leading or trailing spaces.");
        RuleFor(x => x.InitialTemperatureCelsius).ValidCelsius();
        RuleFor(x => x.SupplierReference!).Must(s => s.Trim().Length is >= 1 and <= 200).WithMessage("Use 1 to 200 characters.")
            .When(x => x.SupplierReference is not null);
        RuleFor(x => x.ScannedBarcode!).MaximumLength(200).NotEmpty().When(x => x.ScannedBarcode is not null);
        RuleFor(x => x.ExceptionReason!).ValidReason().When(x => x.ExceptionReason is not null);
    }

    private static bool BeIdentifier(string value) => value.Length is >= 1 and <= 100 && value == value.Trim();
}

public sealed class ReceiveInventoryValidator : AbstractValidator<ReceiveInventory>
{
    public ReceiveInventoryValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.PurchaseOrderId).NotEmpty();
        RuleFor(x => x.Items).NotNull().Must(i => i is { Count: >= 1 and <= 500 }).WithMessage("Send 1 to 500 items.");
        RuleForEach(x => x.Items).SetValidator(new ReceiveItemValidator());
    }
}

/// <summary>
/// Receiving (INB-002 to INB-004, DEC-048). One command validates every item, then posts the receipt, lots, serials,
/// stock buckets, Receipt movements, purchase order progress, audit and outbox in a single transaction guarded by the
/// idempotency record and a permanent operation key (STK-007). Lock order: purchase order row → its lines → stock
/// buckets in natural-key order → audit stream (migration-design §5).
/// </summary>
public sealed class ReceivingService(
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    ICursorCodec cursors,
    IValidator<ReceiveInventory> validator)
{
    public const string Route = "POST /inventory/receive";

    public Task<CommandOutcome> ReceiveAsync(Guid idempotencyKey, ReceiveInventory cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.ReceiptsPost);
        caller.RequireMutable();
        validator.ValidateOrThrow(cmd);
        caller.RequireWarehouse(Permissions.ReceiptsPost, cmd.WarehouseId);

        var payload = JsonSerializer.SerializeToNode(cmd, JsonDefaults.Options);
        var operationKey = $"receive:{caller.Subject}:{idempotencyKey:D}";
        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, Route, payload), async (s, c) =>
        {
            // Lock the order first: a concurrent retry of the same key waits here, then sees the committed operation key.
            if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.purchase_order WHERE id = {cmd.PurchaseOrderId} FOR UPDATE")
                    .ToListAsync(c)).Count == 0)
                throw AppException.NotFound("Purchase order");

            // A retry after the 7-day idempotency record expired still never posts twice (STK-007).
            var earlier = await s.Db.Database.SqlQuery<string>(
                $"SELECT result_reference::text AS \"Value\" FROM ti.document_operation WHERE operation_key = {operationKey}").ToListAsync(c);
            if (earlier.Count == 1)
            {
                var receiptId = Guid.Parse((string)JsonNode.Parse(earlier[0])!["receiptId"]!);
                return Ok(await ResultAsync(s, receiptId, c));
            }
            return Ok(await PostAsync(s, cmd, operationKey, caller, c));
        }, ct);
    }

    public Task<ReceiptDto> GetAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var receipt = await s.Db.Set<Receipt>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, c) ?? throw AppException.NotFound("Receipt");
            caller.RequireWarehouse(Permissions.StockRead, receipt.WarehouseId);
            return (await DtosAsync(s, [receipt], c))[0];
        }, ct);
    }

    public Task<Page<ReceiptDto>> ListAsync(ReceiptFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = callers.Caller.WarehouseFilter(Permissions.StockRead);
        return uow.ReadAsync(async (s, c) =>
        {
            var query = s.Db.Set<Receipt>().AsNoTracking();
            if (scope is not null)
                query = query.Where(r => scope.Contains(r.WarehouseId));
            if (filter.WarehouseId is { } wid)
                query = query.Where(r => r.WarehouseId == wid);
            if (filter.PurchaseOrderId is { } po)
                query = query.Where(r => r.PurchaseOrderId == po);
            if (filter.Status is { } status)
                query = query.Where(r => r.Status == status);
            var receipts = await query.ToPageAsync(page, $"receipts|{filter.WarehouseId}|{filter.PurchaseOrderId}|{filter.Status}", cursors, r => r, c);
            return new Page<ReceiptDto>(await DtosAsync(s, receipts.Items, c), receipts.NextCursor);
        }, ct);
    }

    private static CommandOutcome Ok(ReceiveResult result) => new(200, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));

    // -----------------------------------------------------------------------------------------------------------
    // Posting
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>One resolved receipt line before posting (serial items expand to one per serial).</summary>
    private sealed record Planned(int Item, ReceiveItem Input, PurchaseOrderLine OrderLine, Product Product, Guid UomId, decimal Factor,
        decimal UomQty, decimal BaseQty, string? LotNumber, DateOnly? Expiry, string? SerialNumber, QualityStatus Quality,
        InspectionResult Inspection, string? Reason);

    private static async Task<ReceiveResult> PostAsync(ITenantSession s, ReceiveInventory cmd, string operationKey, CallerContext caller,
        CancellationToken ct)
    {
        // 1. Lock the document: the order row, then its lines (serializes receipts of one order).
        if ((await s.Db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM ti.purchase_order WHERE id = {cmd.PurchaseOrderId} FOR UPDATE")
                .ToListAsync(ct)).Count == 0)
            throw AppException.NotFound("Purchase order");
        await s.Db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM ti.purchase_order_line WHERE purchase_order_id = {cmd.PurchaseOrderId} ORDER BY line_number FOR UPDATE").ToListAsync(ct);
        var order = await s.Db.Set<PurchaseOrder>().SingleAsync(o => o.Id == cmd.PurchaseOrderId, ct);
        var orderLines = await s.Db.Set<PurchaseOrderLine>().Where(l => l.PurchaseOrderId == order.Id).ToDictionaryAsync(l => l.Id, ct);
        if (order.WarehouseId != cmd.WarehouseId)
            throw AppException.Validation("warehouseId", "The purchase order belongs to another warehouse.");
        if (!order.IsReceivable)
            throw AppException.InvalidState("Only an approved or partly received purchase order can receive goods.");

        // 2. Reference data for every item.
        var warehouse = await s.Db.Set<Warehouse>().AsNoTracking().SingleAsync(w => w.Id == cmd.WarehouseId, ct);
        var today = WarehouseToday(warehouse.TimeZone, s.Now);
        var productIds = cmd.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await s.Db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var profileIds = products.Values.Where(p => p.StorageProfileId is not null).Select(p => p.StorageProfileId!.Value).Distinct().ToList();
        var profiles = await s.Db.Set<StorageProfile>().AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var unitIds = cmd.Items.Select(i => i.UomId).Distinct().ToList();
        var activeUnits = (await s.Db.Set<Unit>().AsNoTracking().Where(u => unitIds.Contains(u.Id) && u.Active).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
        var conversions = await s.Db.Set<ProductConversion>().AsNoTracking()
            .Where(cv => productIds.Contains(cv.ProductId) && unitIds.Contains(cv.AlternateUomId) && cv.Active).ToListAsync(ct);
        var binIds = cmd.Items.Select(i => i.TargetBinId).Distinct().ToList();
        var bins = await s.Db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var zoneIds = bins.Values.Select(b => b.ZoneId).Distinct().ToList();
        var zones = await s.Db.Set<Zone>().AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, ct);
        var hazards = (await s.Db.Set<ZoneAllowedHazardClass>().AsNoTracking().Where(h => zoneIds.Contains(h.ZoneId)).ToListAsync(ct))
            .ToLookup(h => h.ZoneId, h => h.HazardClass);

        // 3. Resolve and validate every item before writing anything.
        var planned = new List<Planned>();
        for (var i = 0; i < cmd.Items.Count; i++)
        {
            var item = cmd.Items[i];
            var f = $"items[{i}]";
            if (!orderLines.TryGetValue(item.PurchaseOrderLineId, out var orderLine))
                throw AppException.Validation($"{f}.purchaseOrderLineId", "The line does not belong to this purchase order.");
            if (item.ProductId != orderLine.ProductId)
                throw AppException.Validation($"{f}.productId", "The product does not match the purchase order line.");
            var product = products[item.ProductId];

            // INB-002/003: the scan must agree with what was entered; scanned values fill omitted ones.
            var lot = item.LotNumber;
            var expiry = item.ExpirationDate;
            var serials = item.SerialNumbers?.ToList();
            if (item.ScannedBarcode is { } scanned)
            {
                if (!Gs1.TryParse(scanned, today, out var gs1, out var parseError))
                    throw Coded(ErrorCode.UnknownBarcode, $"{f}.scannedBarcode", parseError);
                if (gs1.Gtin is { } gtin)
                {
                    var mapped = await s.Db.Set<ProductBarcode>().AsNoTracking().Where(b => b.Gtin == gtin && b.Active).Select(b => b.ProductId).ToListAsync(ct);
                    if (mapped.Count != 1)
                        throw Coded(ErrorCode.UnknownBarcode, $"{f}.scannedBarcode", "The GTIN does not map to exactly one active product.");
                    if (mapped[0] != item.ProductId)
                        throw Coded(ErrorCode.BarcodeConflict, $"{f}.productId", "The scanned GTIN belongs to a different product.");
                }
                lot = Agree(lot, gs1.Lot, $"{f}.lotNumber", "lot");
                if (gs1.Expiry is { } scannedExpiry)
                    expiry = expiry is null || expiry == scannedExpiry ? scannedExpiry
                        : throw Coded(ErrorCode.BarcodeConflict, $"{f}.expirationDate", "The scanned expiry differs from the entered one.");
                if (gs1.Serial is { } scannedSerial)
                {
                    if (serials is null || serials.Count == 0)
                        serials = [scannedSerial];
                    else if (!serials.Contains(scannedSerial, StringComparer.Ordinal))
                        throw Coded(ErrorCode.BarcodeConflict, $"{f}.serialNumbers", "The scanned serial is not among the entered serial numbers.");
                }
            }

            // Unit and quantity (STK-PRECISION).
            if (!activeUnits.Contains(item.UomId))
                throw AppException.Validation($"{f}.uomId", "Unknown or retired unit.");
            var factor = item.UomId == product.BaseUomId ? 1m
                : conversions.FirstOrDefault(cv => cv.ProductId == product.Id && cv.AlternateUomId == item.UomId)?.FactorToBase
                  ?? throw AppException.Validation($"{f}.uomId", "The product has no active conversion for this unit.");
            var uomQty = item.Quantity.Value;
            var baseQty = uomQty * factor;
            if (decimal.Round(baseQty, product.BasePrecision) != baseQty)
                throw AppException.Validation($"{f}.quantity", $"The base quantity exceeds the product's precision of {product.BasePrecision} decimal places.");

            // Tracking.
            var lotTracked = product.TrackingMode is TrackingMode.Lot or TrackingMode.LotAndSerial;
            var serialTracked = product.TrackingMode is TrackingMode.Serial or TrackingMode.LotAndSerial;
            var profile = product.StorageProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null;
            if (product.HandlingProfile == HandlingProfile.Medical)
            {
                // INB-003: missing medical evidence blocks posting.
                if (lot is null)
                    throw Coded(ErrorCode.MissingMedicalEvidence, $"{f}.lotNumber", "Medical receipts need a lot number.");
                if (expiry is null)
                    throw Coded(ErrorCode.MissingMedicalEvidence, $"{f}.expirationDate", "Medical receipts need an expiry date.");
                if (item.SupplierReference is null)
                    throw Coded(ErrorCode.MissingMedicalEvidence, $"{f}.supplierReference", "Medical receipts need the supplier reference.");
                if (profile is { MonitoringRequired: true } && item.InitialTemperatureCelsius is null)
                    throw Coded(ErrorCode.MissingMedicalEvidence, $"{f}.initialTemperatureCelsius", "This product requires the arrival temperature.");
            }
            if (lotTracked && lot is null)
                throw AppException.Validation($"{f}.lotNumber", "This product is lot-tracked.");
            if (!lotTracked && lot is not null)
                throw AppException.Validation($"{f}.lotNumber", "This product is not lot-tracked.");
            if (serialTracked)
            {
                if (factor != 1m)
                    throw AppException.Validation($"{f}.uomId", "Receive serial-tracked products in their base unit (DEC-048).");
                if (serials is null || serials.Count != baseQty)
                    throw AppException.Validation($"{f}.serialNumbers", "Send exactly one serial number per unit received.");
            }
            else if (serials is { Count: > 0 })
                throw AppException.Validation($"{f}.serialNumbers", "This product is not serial-tracked.");

            // Target bin (DEC-012).
            if (!bins.TryGetValue(item.TargetBinId, out var bin) || bin.WarehouseId != cmd.WarehouseId || !bin.Active)
                throw AppException.Validation($"{f}.targetBinId", "Use an active bin of this warehouse.");
            var zone = zones[bin.ZoneId];
            if (bin.Kind is not (BinKind.Staging or BinKind.Quarantine)
                || !StorageCompatibility.IsCompatible(zone.TempMinC, zone.TempMaxC, profile?.MinC, profile?.MaxC, product.HazardClass, hazards[zone.Id].ToList()))
                throw Coded(ErrorCode.IncompatibleBin, $"{f}.targetBinId", "Goods first land in a compatible staging or quarantine bin.");

            // Quality (INB-003/004).
            var inspection = item.InspectionResult ?? InspectionResult.Passed;
            var exceptional = ReceivingRules.IsExpired(expiry, today) || ReceivingRules.IsOutOfRange(item.InitialTemperatureCelsius, profile?.MinC, profile?.MaxC);
            if (ReceivingRules.NeedsReason(inspection, exceptional) && item.ExceptionReason is null)
                throw AppException.Validation($"{f}.exceptionReason",
                    "Expired, out-of-range or failed-inspection arrivals are recorded only into quarantine or hold, with a reason.");
            var quality = ReceivingRules.InitialQuality(product.HandlingProfile, inspection, exceptional);
            var reason = ReceivingRules.NeedsReason(inspection, exceptional) || quality == QualityStatus.Hold ? item.ExceptionReason : null;

            if (serialTracked)
                planned.AddRange(serials!.Select(sn => new Planned(i, item, orderLine, product, item.UomId, 1m, 1m, 1m, lot, expiry, sn, quality, inspection, reason)));
            else
                planned.Add(new Planned(i, item, orderLine, product, item.UomId, factor, uomQty, baseQty, lot, expiry, null, quality, inspection, reason));
        }

        // AT-INB-02: zero over-receipt tolerance across the whole request; nothing posts.
        foreach (var group in planned.GroupBy(p => p.OrderLine.Id))
        {
            var line = group.First().OrderLine;
            var requested = group.Sum(p => p.BaseQty);
            if (requested > line.OutstandingBaseQty)
                throw Coded(ErrorCode.OverReceipt, $"items[{group.First().Item}].quantity",
                    $"Line {line.LineNumber} has {Quantity.Format(line.OutstandingBaseQty)} outstanding; {Quantity.Format(requested)} was sent.");
        }

        // INB-004: duplicate serials (in the request or already known) are rejected.
        var serialNumbersByProduct = planned.Where(p => p.SerialNumber is not null).GroupBy(p => p.Product.Id);
        foreach (var group in serialNumbersByProduct)
        {
            var numbers = group.Select(p => p.SerialNumber!).ToList();
            var duplicate = numbers.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key
                ?? await s.Db.Set<Serial>().AsNoTracking().Where(x => x.ProductId == group.Key && numbers.Contains(x.SerialNumber))
                    .Select(x => x.SerialNumber).FirstOrDefaultAsync(ct);
            if (duplicate is not null)
                throw Coded(ErrorCode.DuplicateSerial, $"items[{group.First(p => p.SerialNumber == duplicate).Item}].serialNumbers",
                    $"Serial {duplicate} is already known for this product.");
        }

        // 4. Lots: find or create; an existing lot keeps its expiry.
        var lotIds = new Dictionary<(Guid Product, string Number), Guid>();
        foreach (var p in planned.Where(p => p.LotNumber is not null).DistinctBy(p => (p.Product.Id, p.LotNumber)))
        {
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, supplier_ref, created_by, created_at, updated_at)
                VALUES ({caller.TenantId}, {p.Product.Id}, {p.Product.HandlingProfile.ToString()}::ti.handling_profile, {p.LotNumber},
                        {p.Expiry}, {p.Input.SupplierReference}, {caller.Subject}, {s.Now}, {s.Now})
                ON CONFLICT (tenant_id, product_id, lot_number) DO NOTHING
                """, ct);
            var existing = (await s.Db.Database.SqlQuery<LotRow>($"""
                SELECT id AS "Id", expiry_date AS "ExpiryDate" FROM ti.lot WHERE product_id = {p.Product.Id} AND lot_number = {p.LotNumber}
                """).ToListAsync(ct)).Single();
            if (existing.ExpiryDate != p.Expiry)
                throw Coded(ErrorCode.BarcodeConflict, $"items[{p.Item}].expirationDate", $"Lot {p.LotNumber} is already recorded with a different expiry.");
            lotIds[(p.Product.Id, p.LotNumber!)] = existing.Id;
        }
        Guid? LotOf(Planned p) => p.LotNumber is null ? null : lotIds[(p.Product.Id, p.LotNumber)];

        // 5. Serials.
        var serialIds = new Dictionary<(Guid Product, string Number), Guid>();
        foreach (var p in planned.Where(p => p.SerialNumber is not null))
        {
            var serial = Serial.Create(caller.TenantId, p.Product.Id, LotOf(p), p.SerialNumber!, caller.Subject, s.Now);
            s.Db.Add(serial);
            serialIds[(p.Product.Id, p.SerialNumber!)] = serial.Id;
        }
        await s.Db.SaveChangesAsync(ct);
        Guid? SerialOf(Planned p) => p.SerialNumber is null ? null : serialIds[(p.Product.Id, p.SerialNumber)];

        // 6. Stock buckets in natural-key order (lock order), adding to on hand.
        var bucketKeys = planned
            .Select(p => (Bin: p.Input.TargetBinId, Product: p.Product.Id, Lot: LotOf(p), Serial: SerialOf(p), p.Quality))
            .Distinct()
            .OrderBy(k => k.Bin).ThenBy(k => k.Product).ThenBy(k => k.Lot).ThenBy(k => k.Serial).ThenBy(k => k.Quality)
            .ToList();
        var bucketIds = new Dictionary<(Guid, Guid, Guid?, Guid?, QualityStatus), Guid>();
        foreach (var key in bucketKeys)
        {
            var qty = planned.Where(p => (p.Input.TargetBinId, p.Product.Id, LotOf(p), SerialOf(p), p.Quality) == key).Sum(p => p.BaseQty);
            var ids = await s.Db.Database.SqlQuery<Guid>($"""
                INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at,
                                             on_hand_qty, created_by, created_at, updated_at)
                VALUES ({caller.TenantId}, {cmd.WarehouseId}, {key.Bin}, {key.Product}, {key.Lot}, {key.Serial}, {key.Quality.ToString()}::ti.quality_status,
                        {s.Now}, {qty}, {caller.Subject}, {s.Now}, {s.Now})
                ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status)
                DO UPDATE SET on_hand_qty = ti.stock_bucket.on_hand_qty + EXCLUDED.on_hand_qty, updated_at = EXCLUDED.updated_at,
                              version = ti.stock_bucket.version + 1
                RETURNING id AS "Value"
                """).ToListAsync(ct);
            bucketIds[key] = ids.Single();
        }
        // OPS-001: a blocked bucket (or an open audit-chain failure) refuses the whole receipt; the upserts roll back.
        await StockBlocks.EnsureNotBlockedAsync(s, bucketIds.Values.ToList(), ct);

        // 7. Receipt, lines, serial links and one Receipt movement per line.
        var receipt = Receipt.Post(caller.TenantId, order.Id, cmd.WarehouseId, caller.Subject, s.Now);
        s.Db.Add(receipt);
        var lines = new List<ReceiptLine>();
        foreach (var p in planned)
        {
            var bucketId = bucketIds[(p.Input.TargetBinId, p.Product.Id, LotOf(p), SerialOf(p), p.Quality)];
            var line = DomainGuard.Run(() => ReceiptLine.Create(caller.TenantId, receipt.Id, p.OrderLine.Id, p.Product.Id, LotOf(p), p.UomId, p.Factor,
                p.UomQty, p.BaseQty, p.Input.InitialTemperatureCelsius, p.Input.TargetBinId, cmd.WarehouseId, p.Quality, p.Inspection,
                p.Input.SupplierReference, p.Input.ScannedBarcode, p.Reason, bucketId, caller.Subject, s.Now));
            lines.Add(line);
            s.Db.Add(line);
            if (SerialOf(p) is { } serialId)
                s.Db.Add(ReceiptLineSerial.Create(caller.TenantId, line.Id, serialId, caller.Subject, s.Now));
            s.Db.Add(StockMovement.Receipt(caller.TenantId, $"receipt:{receipt.Id:D}:line:{line.Id:D}", p.Product.Id, LotOf(p), SerialOf(p),
                bucketId, p.BaseQty, receipt.Id, line.Id, caller.Subject, s.Now, caller.CorrelationId));
        }

        // 8. Purchase order progress.
        var orderBefore = PurchaseOrderDto.From(order, orderLines.Values);
        foreach (var group in planned.GroupBy(p => p.OrderLine.Id))
            DomainGuard.Run(() => orderLines[group.Key].RecordReceipt(group.Sum(p => p.BaseQty), s.Now));
        DomainGuard.Run(() => order.RecordReceipt(orderLines.Values.All(l => l.IsComplete), s.Now));

        // 9. Permanent operation key, audit and outbox (same transaction; the audit stream lock comes last).
        await s.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO ti.document_operation (tenant_id, operation_key, operation_type, result_reference, created_by, created_at)
            VALUES ({caller.TenantId}, {operationKey}, 'Receive', {new JsonObject { ["receiptId"] = receipt.Id.ToString() }.ToJsonString()}::jsonb,
                    {caller.Subject}, {s.Now})
            """, ct);
        var result = new ReceiveResult(receipt.Id, receipt.Status, lines.Select(l => new ReceiveResultLine(l.Id, l.LotId, l.QualityStatus, l.BucketId)).ToList(),
            receipt.Version);
        s.Audit(new AuditEntry("ReceiptPosted", "Receipt", receipt.Id, receipt.Version, null, null, new
        {
            receipt.PurchaseOrderId, receipt.WarehouseId, receipt.Status,
            Lines = lines.Select(l => new { l.Id, l.ProductId, l.LotId, BaseQuantity = Quantity.From(l.BaseQty), l.QualityStatus, l.BucketId, l.StagingBinId }),
        }));
        foreach (var line in lines.Where(l => l.QualityStatus == QualityStatus.Quarantine))
            s.Audit(new AuditEntry("StockQuarantined", "StockBucket", line.BucketId, null, line.QualityReason, null,
                new { ReceiptLineId = line.Id, line.ProductId, line.LotId, BaseQuantity = Quantity.From(line.BaseQty), line.StagingBinId }));
        s.Audit(new AuditEntry("PurchaseOrderReceiptRecorded", "PurchaseOrder", order.Id, order.Version, null, orderBefore,
            PurchaseOrderDto.From(order, orderLines.Values)));
        s.Publish("GoodsReceived", new { receiptId = receipt.Id, purchaseOrderId = order.Id, warehouseId = cmd.WarehouseId });
        await s.Db.SaveChangesAsync(ct);
        return result;
    }

    private static string? Agree(string? entered, string? scanned, string field, string what) =>
        scanned is null || entered is null || entered == scanned ? entered ?? scanned
        : throw Coded(ErrorCode.BarcodeConflict, field, $"The scanned {what} differs from the entered one.");

    private static AppException Coded(ErrorCode code, string field, string message) =>
        new(code, message, [new FieldError(field, code, message)]);

    /// <summary>Expiry is judged against the date in the warehouse's IANA time zone (AGENTS.md, injected clock).</summary>
    private static DateOnly WarehouseToday(string timeZone, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    private static async Task<ReceiveResult> ResultAsync(ITenantSession s, Guid receiptId, CancellationToken ct)
    {
        var receipt = await s.Db.Set<Receipt>().AsNoTracking().SingleAsync(r => r.Id == receiptId, ct);
        var lines = await s.Db.Set<ReceiptLine>().AsNoTracking().Where(l => l.ReceiptId == receiptId).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToListAsync(ct);
        return new ReceiveResult(receipt.Id, receipt.Status, lines.Select(l => new ReceiveResultLine(l.Id, l.LotId, l.QualityStatus, l.BucketId)).ToList(), receipt.Version);
    }

    private static async Task<List<ReceiptDto>> DtosAsync(ITenantSession s, IReadOnlyList<Receipt> receipts, CancellationToken ct)
    {
        var ids = receipts.Select(r => r.Id).ToList();
        var lines = await s.Db.Set<ReceiptLine>().AsNoTracking().Where(l => ids.Contains(l.ReceiptId)).ToListAsync(ct);
        var lineIds = lines.Select(l => l.Id).ToList();
        var serials = (await s.Db.Set<ReceiptLineSerial>().AsNoTracking().Where(x => lineIds.Contains(x.ReceiptLineId)).ToListAsync(ct))
            .ToLookup(x => x.ReceiptLineId, x => x.SerialId);
        var byReceipt = lines.ToLookup(l => l.ReceiptId);
        return receipts.Select(r => new ReceiptDto(r.Id, r.Version, r.CreatedAt, r.CreatedBy, r.UpdatedAt, r.PurchaseOrderId, r.WarehouseId, r.Status,
            r.ReceivedAt, byReceipt[r.Id].OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Select(l => new ReceiptLineDto(l.Id, l.PurchaseOrderLineId,
                l.ProductId, l.LotId, serials[l.Id].ToList(), l.UomId, Quantity.From(l.UomFactorToBase), Quantity.From(l.BaseQty), l.MeasuredTemperatureC,
                l.StagingBinId, l.QualityStatus, l.InspectionResult, l.QualityReason, l.BucketId,
                Quantity.From(l.BaseQty))).ToList())).ToList(); // putaway remaining = base quantity until slice 5 (DEC-048)
    }

    private sealed record LotRow(Guid Id, DateOnly? ExpiryDate);
}
