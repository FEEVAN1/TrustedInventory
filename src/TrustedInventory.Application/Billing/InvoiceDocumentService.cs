using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Operations;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Billing;

/// <summary>What an invoice document shows: the invoice snapshot (BIL-001), the business and the provider. Amounts are preformatted text.</summary>
public sealed record InvoiceDocument(
    string Number, string Status, string? PaymentStatus, string BusinessName, string Country, string Provider, bool Simulated,
    IReadOnlyList<InvoiceDocumentLine> Lines, string Subtotal, string Tax, string Total, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    DateTimeOffset? IssuedAt, DateTimeOffset? DueAt, DateTimeOffset? PaidAt);

public sealed record InvoiceDocumentLine(string Description, int Quantity, string UnitAmount, string Tax, string Total);

/// <summary>Renders an invoice document (DEC-092). Infrastructure supplies a PDF renderer.</summary>
public interface IInvoiceDocuments
{
    byte[] Render(InvoiceDocument document);
}

/// <summary>
/// Invoice documents (BIL-006, TEN-005, DEC-092): rendered on request from the invoice snapshot, which never changes, so nothing is stored
/// and every download shows what was billed. A short-lived link bound to tenant, caller, invoice and expiry, as for exports (DEC-061);
/// both the link and the download are audited. Draft invoices have no document. Tax references are never shown (section 27).
/// </summary>
public sealed class InvoiceDocumentService(ITenantUnitOfWork uow, ICallerAccessor callers, IDownloadLinks links, IInvoiceDocuments documents)
{
    public Task<DownloadLinkDto> LinkAsync(Guid id, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingRead);
        return uow.WriteAsync(async (s, c) =>
        {
            var row = await RowAsync(s, id, c) ?? throw AppException.NotFound("Invoice");
            if (row.Status == "Draft")
                throw AppException.InvalidState("A draft invoice has no document yet.");
            var expires = s.Now + IDownloadLinks.MaxLifetime;
            var token = links.Issue(caller.TenantId, DownloadKind.Invoice, id, caller.Subject, expires);
            s.Audit(new AuditEntry("InvoiceLinkIssued", "Invoice", id, null, null, null, new { invoiceId = id, expiresAt = expires }));
            await s.Db.SaveChangesAsync(c);
            return new DownloadLinkDto($"/api/v1/saas/invoices/{id}/download?token={Uri.EscapeDataString(token)}", expires);
        }, ct);
    }

    public Task<DownloadedFile> DownloadAsync(Guid id, string token, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.BillingRead);
        return uow.WriteAsync(async (s, c) =>
        {
            if (!links.IsValid(token, caller.TenantId, DownloadKind.Invoice, id, caller.Subject, s.Now))
                throw AppException.NotFound("Download");
            var row = await RowAsync(s, id, c);
            if (row is null || row.Status == "Draft")
                throw AppException.NotFound("Download");
            var content = documents.Render(Document(row));
            s.Audit(new AuditEntry("InvoiceDownloaded", "Invoice", id, null, null, null, new { invoiceId = id, bytes = content.Length }));
            await s.Db.SaveChangesAsync(c);
            var name = new string(row.Number.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
            return new DownloadedFile($"invoice-{name}.pdf", "application/pdf", content);
        }, ct);
    }

    internal static InvoiceDocument Document(DocumentRow r)
    {
        var currency = r.Currency.Trim();
        string M(long minor) => Money(currency, minor, r.Exponent);
        var lines = JsonSerializer.Deserialize<List<InvoiceLineSnapshot>>(r.Lines, JsonDefaults.Options) ?? [];
        return new InvoiceDocument(r.Number, r.Status, r.PaymentStatus, r.BusinessName, r.Country, r.Provider, r.Provider == BillingOptions.Simulator,
            lines.Select(l => new InvoiceDocumentLine(l.Description, l.Quantity, M(l.UnitAmount), M(l.Tax), M(l.Total))).ToList(),
            M(r.Subtotal), M(r.Tax), M(r.Total), r.PeriodStart, r.PeriodEnd, r.IssuedAt, r.DueAt, r.PaidAt);
    }

    /// <summary>"NGN 1,250.00": integer minor units with the currency's exponent, never via floating point.</summary>
    internal static string Money(string currency, long minor, int exponent)
    {
        var scale = 1L;
        for (var i = 0; i < exponent; i++)
            scale *= 10;
        var negative = minor < 0;
        var abs = Math.Abs(minor);
        var whole = (abs / scale).ToString("N0", CultureInfo.InvariantCulture);
        var text = exponent == 0 ? whole : $"{whole}.{(abs % scale).ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0')}";
        return $"{currency} {(negative ? "-" : "")}{text}";
    }

    private static async Task<DocumentRow?> RowAsync(ITenantSession s, Guid id, CancellationToken ct) =>
        await s.Db.Database.SqlQuery<DocumentRow>($"""
            SELECT coalesce(i.number, i.provider_invoice_ref) AS "Number", i.status::text AS "Status",
                   (SELECT p.status::text FROM cp.payment p WHERE p.invoice_id = i.id
                    ORDER BY p.provider_occurred_at DESC, p.recorded_at DESC LIMIT 1) AS "PaymentStatus",
                   b.business_legal_name AS "BusinessName", b.country AS "Country", i.provider AS "Provider",
                   i.lines::text AS "Lines", i.currency::text AS "Currency", i.exponent::int AS "Exponent", i.subtotal AS "Subtotal",
                   i.tax AS "Tax", i.total AS "Total", coalesce(i.period_start, i.created_at) AS "PeriodStart",
                   coalesce(i.period_end, i.period_start, i.created_at) AS "PeriodEnd", i.issued_at AS "IssuedAt", i.due_at AS "DueAt",
                   i.paid_at AS "PaidAt"
            FROM cp.invoice i JOIN cp.billing_account b ON b.tenant_id = i.tenant_id AND b.id = i.billing_account_id
            WHERE i.id = {id}
            """).SingleOrDefaultAsync(ct);

    internal sealed record DocumentRow(string Number, string Status, string? PaymentStatus, string BusinessName, string Country, string Provider,
        string Lines, string Currency, int Exponent, long Subtotal, long Tax, long Total, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
        DateTimeOffset? IssuedAt, DateTimeOffset? DueAt, DateTimeOffset? PaidAt);
}
