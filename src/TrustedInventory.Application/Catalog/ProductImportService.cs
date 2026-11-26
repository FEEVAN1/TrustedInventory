using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed record ProductImportRequest(string Csv);

public sealed record ProductImportDto(Guid Id, int RowCount, string Digest, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>
/// The optional import of the setup checklist (ONB-004, DEC-099): general products from CSV, all or nothing. Every row is checked
/// before anything is written and every problem is reported (rows[n].column); then each product is created with its own audit event
/// in one transaction, the SKU quota is reserved for the whole file, and the import is recorded by digest (the file is not kept).
/// </summary>
public sealed class ProductImportService(ICallerAccessor callers, ITenantUnitOfWork uow, IQuotaGate quota, ICursorCodec cursors)
{
    public const int MaxRows = 1000;
    private static readonly string[] Required = ["sku", "name", "unit", "tracking"];

    public Task<Page<ProductImportDto>> ListAsync(PageRequest page, CancellationToken ct)
    {
        callers.Caller.Require(Permissions.CatalogRead);
        page.EnsureSupportedSort();
        const string filter = "product-imports";
        var after = page.Cursor is { } c ? cursors.Decode(c, filter) : null;
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<ImportRow>($"""
                SELECT id AS "Id", row_count AS "RowCount", digest AS "Digest", created_at AS "CreatedAt", created_by AS "CreatedBy"
                FROM ti.product_import
                WHERE {after == null} OR (created_at, id) < ({after?.CreatedAt ?? DateTimeOffset.MinValue}, {after?.Id ?? Guid.Empty})
                ORDER BY created_at DESC, id DESC LIMIT {page.EffectiveLimit + 1}
                """).ToListAsync(c);
            var items = rows.Take(page.EffectiveLimit).Select(r => r.ToDto()).ToList();
            return new Page<ProductImportDto>(items,
                rows.Count > page.EffectiveLimit ? cursors.Encode(new CursorPosition(items[^1].CreatedAt, items[^1].Id), filter) : null);
        }, ct);
    }

    public Task<CommandOutcome> ImportAsync(Guid idempotencyKey, ProductImportRequest cmd, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.CatalogManage);
        caller.RequireMutable();
        if (string.IsNullOrEmpty(cmd.Csv) || cmd.Csv.Length > 1_000_000)
            throw AppException.Validation("csv", "Send the CSV text, at most 1,000,000 characters.");
        var lines = Parse(cmd.Csv);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(cmd.Csv));
        var payload = JsonSerializer.SerializeToNode(new { digest = Convert.ToHexStringLower(digest) }, JsonDefaults.Options);

        return uow.WriteIdempotentAsync(new IdempotencyRequest(idempotencyKey, "POST /imports/products", payload), async (s, c) =>
        {
            await quota.ReserveAsync(s, QuotaKey.ActiveSkus, lines.Count, c);
            var units = await s.Db.Set<Unit>().Where(u => u.Active).ToDictionaryAsync(u => u.Code, u => u.Id, StringComparer.Ordinal, c);
            var skus = lines.Select(l => l.Sku).ToList();
            var existing = (await s.Db.Set<Product>().Where(p => skus.Contains(p.Sku)).Select(p => p.Sku).ToListAsync(c)).ToHashSet(StringComparer.Ordinal);

            var errors = new List<FieldError>();
            void Problem(ImportLine l, string column, string message) =>
                errors.Add(new FieldError($"rows[{l.Row}].{column}", ErrorCode.ValidationFailed, message));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                if (l.Sku.Length is 0 or > 64 || l.Sku != l.Sku.Trim())
                    Problem(l, "sku", "Use 1 to 64 characters without leading or trailing spaces.");
                else if (existing.Contains(l.Sku))
                    Problem(l, "sku", "A product with this SKU already exists.");
                else if (!seen.Add(l.Sku))
                    Problem(l, "sku", "This SKU appears more than once in the file.");
                if (l.Name.Length is 0 or > 200 || l.Name != l.Name.Trim())
                    Problem(l, "name", "Use 1 to 200 characters without leading or trailing spaces.");
                if (!units.ContainsKey(l.Unit))
                    Problem(l, "unit", "Use the code of an active unit.");
                if (!Enum.TryParse<TrackingMode>(l.Tracking, ignoreCase: false, out var tracking) || !Enum.IsDefined(tracking))
                    Problem(l, "tracking", "Use Quantity, Lot, Serial or LotAndSerial.");
                if (l.Precision is not { } p || p is < 0 or > 6)
                    Problem(l, "precision", "Use a whole number from 0 to 6.");
            }
            if (errors.Count > 0)
                throw new AppException(ErrorCode.ValidationFailed, $"{errors.Count} problem(s) in the file; nothing was imported.", errors);

            var importId = Guid.CreateVersion7();
            foreach (var l in lines)
            {
                var product = DomainGuard.Run(() => Product.Create(caller.TenantId, l.Sku, l.Name, HandlingProfile.General,
                    Enum.Parse<TrackingMode>(l.Tracking), units[l.Unit], (short)l.Precision!.Value, null, null, null, caller.Subject, s.Now));
                s.Db.Add(product);
                var dto = ProductDto.From(product);
                s.Audit(new AuditEntry("ProductCreated", "Product", product.Id, product.Version, $"Product import {importId:D}, row {l.Row}", null, dto));
                s.Publish("ProductCreated", new { productId = product.Id, product.Sku });
            }
            await s.Db.SaveChangesAsync(c);
            await s.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO ti.product_import (id, tenant_id, row_count, digest, created_at, created_by)
                VALUES ({importId}, {caller.TenantId}, {lines.Count}, {digest}, {s.Now}, {caller.Subject})
                """, c);
            var result = new ProductImportDto(importId, lines.Count, Convert.ToHexStringLower(digest), s.Now, caller.Subject);
            s.Audit(new AuditEntry("ProductsImported", "ProductImport", importId, null, null, null, result));
            await s.Db.SaveChangesAsync(c);
            return new CommandOutcome(201, JsonSerializer.SerializeToNode(result, JsonDefaults.Options));
        }, ct);
    }

    internal sealed record ImportLine(int Row, string Sku, string Name, string Unit, string Tracking, int? Precision);

    /// <summary>RFC 4180 CSV: a header row naming the columns (any order), quoted fields with doubled quotes, CRLF or LF.</summary>
    internal static List<ImportLine> Parse(string csv)
    {
        var records = Records(csv).Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
        if (records.Count < 2)
            throw AppException.Validation("csv", "Send a header row and at least one product row.");
        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var missing = Required.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0)
            throw AppException.Validation("csv", $"The header row needs the columns {string.Join(", ", Required)}; missing {string.Join(", ", missing)}.");
        if (records.Count - 1 > MaxRows)
            throw AppException.Validation("csv", $"Import at most {MaxRows} rows at a time.");
        string Cell(List<string> r, string column) => header.IndexOf(column) is var i and >= 0 && i < r.Count ? r[i] : "";
        return records.Skip(1).Select((r, i) =>
        {
            var precision = Cell(r, "precision");
            return new ImportLine(i + 1, Cell(r, "sku"), Cell(r, "name"), Cell(r, "unit"), Cell(r, "tracking"),
                precision.Length == 0 ? 0 : int.TryParse(precision, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null);
        }).ToList();
    }

    private static IEnumerable<List<string>> Records(string csv)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                yield return record;
                record = [];
            }
            else field.Append(ch);
        }
        if (quoted)
            throw AppException.Validation("csv", "A quoted field is not closed.");
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    private sealed record ImportRow(Guid Id, int RowCount, byte[] Digest, DateTimeOffset CreatedAt, string CreatedBy)
    {
        public ProductImportDto ToDto() => new(Id, RowCount, Convert.ToHexStringLower(Digest), CreatedAt, CreatedBy);
    }
}
