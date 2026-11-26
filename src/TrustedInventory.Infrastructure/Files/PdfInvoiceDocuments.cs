using System.Globalization;
using System.Text;
using TrustedInventory.Application.Billing;

namespace TrustedInventory.Infrastructure.Files;

/// <summary>
/// A plain PDF 1.4 invoice (DEC-092): A4 pages, the standard Helvetica fonts, text only, so no external package or font file is needed.
/// The output is a deterministic function of the invoice snapshot. Text outside Latin-1 is replaced with "?" (WinAnsi encoding).
/// </summary>
public sealed class PdfInvoiceDocuments : IInvoiceDocuments
{
    private const int RowsPerPage = 46;

    public byte[] Render(InvoiceDocument document)
    {
        var d = document;
        static string Day(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";
        var rows = new List<Row>
        {
            new(true, 18, 50, $"Invoice {d.Number}"),
        };
        if (d.Simulated)
            rows.Add(new(true, 10, 50, "SIMULATED INVOICE - issued by the TrustedInventory billing simulator; not a demand for payment."));
        rows.Add(Row.Blank);
        rows.Add(new(false, 10, 50, $"Billed to: {d.BusinessName} ({d.Country})"));
        rows.Add(new(false, 10, 50, $"Status: {d.Status}{(d.PaymentStatus is { } p ? $"   Payment: {p}" : "")}"));
        rows.Add(new(false, 10, 50, $"Period: {Day(d.PeriodStart)} to {Day(d.PeriodEnd)} (UTC)"));
        rows.Add(new(false, 10, 50, $"Issued: {Day(d.IssuedAt)}   Due: {Day(d.DueAt)}   Paid: {Day(d.PaidAt)}"));
        rows.Add(Row.Blank);
        rows.Add(new(true, 10, 50, "Description", [(300, "Qty"), (340, "Unit amount"), (430, "Tax"), (500, "Total")]));
        foreach (var line in d.Lines)
            rows.Add(new(false, 10, 50, Clip(line.Description, 45), [(300, line.Quantity.ToString(CultureInfo.InvariantCulture)), (340, line.UnitAmount), (430, line.Tax), (500, line.Total)]));
        rows.Add(Row.Blank);
        rows.Add(new(false, 10, 340, $"Subtotal: {d.Subtotal}"));
        rows.Add(new(false, 10, 340, $"Tax: {d.Tax}"));
        rows.Add(new(true, 11, 340, $"Total: {d.Total}"));
        rows.Add(Row.Blank);
        rows.Add(new(false, 8, 50, $"TrustedInventory subscription invoice, generated from the stored invoice record. Payments are processed by {d.Provider}."));

        var pages = rows.Chunk(RowsPerPage).Select(PageContent).ToList();
        return Assemble(pages);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "~";

    private static byte[] PageContent(Row[] rows)
    {
        var s = new StringBuilder();
        var y = 790;
        foreach (var r in rows)
        {
            if (r.Text.Length > 0)
                s.Append(CultureInfo.InvariantCulture, $"BT /{(r.Bold ? "F2" : "F1")} {r.Size} Tf {r.X} {y} Td ({Escape(r.Text)}) Tj ET\n");
            foreach (var (x, text) in r.Columns)
                s.Append(CultureInfo.InvariantCulture, $"BT /{(r.Bold ? "F2" : "F1")} {r.Size} Tf {x} {y} Td ({Escape(text)}) Tj ET\n");
            y -= r.Size >= 14 ? 26 : 16;
        }
        return Encoding.Latin1.GetBytes(s.ToString());
    }

    /// <summary>Escapes PDF string syntax and keeps only Latin-1 printable characters.</summary>
    private static string Escape(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch is < ' ' or > 'ÿ' or (> '~' and < ' ') ? '?' : ch;
            if (c is '\\' or '(' or ')')
                s.Append('\\');
            s.Append(c);
        }
        return s.ToString();
    }

    private static byte[] Assemble(List<byte[]> pageContents)
    {
        // Objects: 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, then per page a page object and its content stream.
        var objects = new List<byte[]>();
        var pageIds = Enumerable.Range(0, pageContents.Count).Select(i => 5 + 2 * i).ToList();
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageContents.Count} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        foreach (var (content, i) in pageContents.Select((c, i) => (c, i)))
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageIds[i] + 1} 0 R >>"));
            objects.Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n"), .. content, .. Ascii("\nendstream")]);
        }

        using var pdf = new MemoryStream();
        void Write(byte[] bytes) => pdf.Write(bytes);
        Write(Ascii("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write(Ascii($"{i + 1} 0 obj\n"));
            Write(objects[i]);
            Write(Ascii("\nendobj\n"));
        }
        var xref = pdf.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(Ascii(table.ToString()));
        return pdf.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private sealed record Row(bool Bold, int Size, int X, string Text, (int X, string Text)[]? ColumnList = null)
    {
        public static readonly Row Blank = new(false, 10, 50, "");

        public (int X, string Text)[] Columns => ColumnList ?? [];
    }
}
