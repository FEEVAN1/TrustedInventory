using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.Application.Operations;

/// <summary>
/// The tenant archive export (LFC-003, DEC-071): a ZIP of JSON Lines files, one per tenant table, plus lot lineage, the audit
/// events with an integrity manifest, the attachment manifest with the clean files, and <c>manifest.json</c> with a SHA-256 per
/// file. Every query runs in the tenant's own transaction, so row-level security limits it to that tenant (TEN-003); nothing is
/// read from the control plane, so provider payloads and tax references can never appear.
/// </summary>
public static class TenantArchive
{
    /// <summary>Tables written as data files, in dependency order. Columns that hold token or re-authentication digests are dropped.</summary>
    public static readonly IReadOnlyList<string> Tables =
    [
        "configuration_version", "membership", "role_grant", "ownership_transfer",
        "unit", "storage_profile", "excursion_policy", "product", "product_conversion", "product_barcode", "partner",
        "warehouse", "zone", "zone_allowed_hazard_class", "bin",
        "lot", "serial", "stock_bucket", "stock_bucket_block", "stock_movement", "document_operation",
        "purchase_order", "purchase_order_line", "receipt", "receipt_line", "receipt_line_serial", "warehouse_task",
        "outbound_order", "outbound_order_line", "reservation", "pick", "pick_confirmation", "shipment", "shipment_line",
        "transfer", "transfer_line", "stock_count", "stock_count_bin", "stock_count_line", "adjustment",
        "customer_return", "customer_return_inspection", "approval", "lot_expiry_correction", "attachment", "alert", "export_job", "audit_checkpoint",
        "recall", "recall_item", "recall_outreach",
        "device", "device_zone_assignment", "telemetry_reading", "excursion", "excursion_bucket", "excursion_shipment_flag",
    ];

    /// <summary>
    /// Tenant tables deliberately left out, with the reason. A schema test fails when a tenant table is in neither list, so a new
    /// table cannot silently drop out of the archive.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tenant"] = "Written as tenant.json (name, slug, lifecycle).",
        ["audit_event"] = "Written as audit/events.jsonl with audit/integrity.json.",
        ["audit_stream"] = "Chain head; written into audit/integrity.json.",
        ["idempotency_record"] = "Internal replay cache of API responses, retained 24 hours.",
        ["outbox_event"] = "Internal delivery queue.",
        ["invitation"] = "Holds invitation token digests (credentials).",
        ["device_credential"] = "Holds device secret digests (credentials).",
        ["usage_counter"] = "Internal quota accounting.",
        ["usage_reservation"] = "Internal quota accounting.",
    };

    private static readonly string[] DroppedColumns = ["reauth_reference", "token_hash"];

    public sealed record FileEntry(string Path, string Sha256, long Bytes, int? Rows);

    public sealed record Result(byte[] Content, long ToSequence, int FileCount, int Rows);

    public static async Task<Result> BuildAsync(ITenantSession s, ExportJob job, IObjectStore store, CancellationToken ct)
    {
        var files = new List<(string Path, byte[] Content, int? Rows)>();
        var totalRows = 0;

        var tenant = await Scalar(s, $"SELECT to_jsonb(t)::text AS \"Value\" FROM (SELECT id, name, slug, lifecycle, home_region, ready_at, archived_at FROM ti.tenant WHERE id = {job.TenantId}) t", ct);
        files.Add(("tenant.json", Encoding.UTF8.GetBytes(tenant ?? "{}"), null));

        foreach (var table in Tables)
        {
            var drop = string.Concat(DroppedColumns.Select(c => $" - '{c}'"));
            // The table name comes only from the fixed list above, never from input.
#pragma warning disable EF1002 // identifiers cannot be parameters; the list is a compile-time constant
            var rows = await s.Db.Database.SqlQueryRaw<string>(
                $"SELECT (to_jsonb(t){drop})::text AS \"Value\" FROM ti.{table} t ORDER BY 1").ToListAsync(ct);
#pragma warning restore EF1002
            files.Add(($"data/{table}.jsonl", Lines(rows), rows.Count));
            totalRows += rows.Count;
        }

        var lineage = await s.Db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
                     'lotId', l.id, 'productId', l.product_id, 'lotNumber', l.lot_number, 'expiryDate', l.expiry_date,
                     'receiptLines', coalesce((SELECT jsonb_agg(jsonb_build_object('receiptId', rl.receipt_id, 'receiptLineId', rl.id) ORDER BY rl.created_at, rl.id)
                                               FROM ti.receipt_line rl WHERE rl.lot_id = l.id), '[]'::jsonb),
                     'movements', coalesce((SELECT jsonb_agg(m.id ORDER BY m.occurred_at, m.id) FROM ti.stock_movement m WHERE m.lot_id = l.id), '[]'::jsonb),
                     'shipmentLines', coalesce((SELECT jsonb_agg(jsonb_build_object('shipmentId', sl.shipment_id, 'shipmentLineId', sl.id) ORDER BY sl.id)
                                               FROM ti.shipment_line sl WHERE sl.lot_id = l.id), '[]'::jsonb))::text AS "Value"
            FROM ti.lot l ORDER BY l.created_at, l.id
            """).ToListAsync(ct);
        files.Add(("lineage/lots.jsonl", Lines(lineage), lineage.Count));

        var audit = await s.Db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('sequence', sequence, 'occurredAt', occurred_at, 'actor', actor_subject, 'actorType', actor_type,
                     'eventType', event_type, 'objectType', object_type, 'objectId', object_id, 'objectVersion', object_version, 'reason', reason,
                     'before', before_state, 'after', after_state, 'previousHash', encode(previous_hash, 'hex'), 'eventHash', encode(event_hash, 'hex'))::text AS "Value"
            FROM ti.audit_event ORDER BY sequence
            """).ToListAsync(ct);
        files.Add(("audit/events.jsonl", Lines(audit), audit.Count));
        totalRows += audit.Count;

        var head = await s.Db.Database.SqlQuery<long>($"SELECT last_sequence AS \"Value\" FROM ti.audit_stream WHERE tenant_id = {job.TenantId}")
            .ToListAsync(ct);
        var integrity = await Scalar(s, $"""
            SELECT jsonb_build_object(
                     'streamFrom', (SELECT min(sequence) FROM ti.audit_event), 'streamTo', (SELECT last_sequence FROM ti.audit_stream),
                     'lastHash', (SELECT encode(last_hash, 'hex') FROM ti.audit_stream),
                     'hashRule', 'event_hash = SHA-256(previous_hash || UTF-8 canonical JSON body), RFC 8785 (DEC-024)',
                     'checkpoints', coalesce((SELECT jsonb_agg(jsonb_build_object('date', checkpoint_date, 'sequence', sequence,
                                       'eventHash', encode(event_hash, 'hex'), 'signature', encode(signature, 'hex'), 'keyId', signing_key_id)
                                       ORDER BY checkpoint_date) FROM ti.audit_checkpoint), '[]'::jsonb))::text AS "Value"
            """, ct);
        files.Add(("audit/integrity.json", Encoding.UTF8.GetBytes(integrity ?? "{}"), null));

        var attachments = await s.Db.Database.SqlQuery<AttachmentRow>($"""
            SELECT id AS "Id", file_name AS "FileName", media_type AS "MediaType", size_bytes AS "SizeBytes",
                   encode(digest_sha256, 'hex') AS "Sha256", scan_status::text AS "ScanStatus", object_key AS "ObjectKey",
                   entity_type AS "EntityType", entity_id AS "EntityId"
            FROM ti.attachment ORDER BY created_at, id
            """).ToListAsync(ct);
        var attachmentManifest = new List<object>();
        foreach (var a in attachments)
        {
            // Only clean files are included; a pending or rejected file is listed without content (authorised files only, LFC-003).
            string? path = null;
            if (a.ScanStatus == "Clean" && a.ObjectKey.StartsWith($"{job.TenantId:D}/", StringComparison.Ordinal)
                && await store.GetAsync(a.ObjectKey, ct) is { } bytes)
            {
                path = $"attachments/files/{a.Id:D}";
                files.Add((path, bytes, null));
            }
            attachmentManifest.Add(new { a.Id, a.FileName, a.MediaType, a.SizeBytes, a.Sha256, a.ScanStatus, a.EntityType, a.EntityId, path });
        }
        files.Add(("attachments/manifest.json", JsonSerializer.SerializeToUtf8Bytes(attachmentManifest, JsonDefaults.Options), attachments.Count));

        var entries = files.Select(f => new FileEntry(f.Path, Convert.ToHexStringLower(SHA256.HashData(f.Content)), f.Content.LongLength, f.Rows)).ToList();
        var manifest = new
        {
            format = "TrustedInventory tenant archive v1",
            tenantId = job.TenantId,
            exportId = job.Id,
            generatedAt = s.Now,
            filter = JsonDocument.Parse(job.Filter).RootElement,
            auditStreamTo = head.SingleOrDefault(),
            files = entries,
        };

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content, _) in files)
                await WriteAsync(archive, path, content, s.Now, ct);
            await WriteAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options), s.Now, ct);
        }
        return new Result(zip.ToArray(), head.SingleOrDefault(), files.Count + 1, totalRows);
    }

    private static async Task WriteAsync(ZipArchive archive, string path, byte[] content, DateTimeOffset now, CancellationToken ct)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = now;
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(content, ct);
    }

    private static byte[] Lines(IEnumerable<string> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(row).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<string?> Scalar(ITenantSession s, FormattableString sql, CancellationToken ct) =>
        (await s.Db.Database.SqlQuery<string>(sql).ToListAsync(ct)).SingleOrDefault();

    private sealed record AttachmentRow(Guid Id, string FileName, string MediaType, long SizeBytes, string Sha256, string ScanStatus, string ObjectKey,
        string? EntityType, Guid? EntityId);
}
