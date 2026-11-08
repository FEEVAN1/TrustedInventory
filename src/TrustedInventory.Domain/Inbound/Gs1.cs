using TrustedInventory.Domain.Catalog;

namespace TrustedInventory.Domain.Inbound;

/// <summary>Identifiers read from one GS1 element string. Values are strings, so leading zeros are kept.</summary>
public sealed record Gs1Data(string? Gtin, string? Lot, DateOnly? Expiry, string? Serial);

/// <summary>
/// GS1 element string parser for receiving (INB-002, DEC-048). Accepts the raw scanner form, with FNC1 group
/// separators (ASCII 29) after variable-length fields and an optional symbology prefix such as <c>]C1</c>, and the
/// bracketed human-readable form <c>(01)…(17)…(10)…(21)…</c>. Supported AIs: 01 GTIN (14 digits, check digit),
/// 17 expiry (YYMMDD), 10 lot and 21 serial (1 to 20 characters of the GS1 82-character set). Anything else fails.
/// </summary>
public static class Gs1
{
    public const char GroupSeparator = '\u001D';
    private const int MaxVariableLength = 20;

    /// <summary>GS1 AI encodable character set 82 (General Specifications figure 7.11-1).</summary>
    private const string Cset82Punctuation = "!\"%&'()*+,-./:;<=>?_";

    /// <param name="today">The current date, for the expiry century window (injected clock).</param>
    public static bool TryParse(string? raw, DateOnly today, out Gs1Data data, out string error)
    {
        data = new Gs1Data(null, null, null, null);
        error = "";
        if (string.IsNullOrEmpty(raw))
            return Fail("The barcode is empty.", out error);

        var text = raw;
        if (text.Length >= 3 && text[0] == ']')
            text = text[3..]; // symbology identifier, e.g. ]C1 (GS1-128), ]d2 (DataMatrix), ]Q3 (QR)

        var fields = text.StartsWith('(') ? SplitBracketed(text, out error) : SplitRaw(text, out error);
        if (fields is null)
            return false;

        string? gtin = null, lot = null, serial = null;
        DateOnly? expiry = null;
        foreach (var (ai, value) in fields)
        {
            switch (ai)
            {
                case "01":
                    if (gtin is not null)
                        return Fail("AI 01 appears twice.", out error);
                    if (!Gtin.IsValidGtin14(value))
                        return Fail("AI 01 must be 14 digits with a valid check digit.", out error);
                    gtin = value;
                    break;
                case "17":
                    if (expiry is not null)
                        return Fail("AI 17 appears twice.", out error);
                    if (!TryExpiry(value, today, out var date))
                        return Fail("AI 17 must be a valid YYMMDD date.", out error);
                    expiry = date;
                    break;
                case "10":
                    if (lot is not null)
                        return Fail("AI 10 appears twice.", out error);
                    if (!IsVariableValue(value))
                        return Fail("AI 10 must be 1 to 20 GS1 characters.", out error);
                    lot = value;
                    break;
                case "21":
                    if (serial is not null)
                        return Fail("AI 21 appears twice.", out error);
                    if (!IsVariableValue(value))
                        return Fail("AI 21 must be 1 to 20 GS1 characters.", out error);
                    serial = value;
                    break;
                default:
                    return Fail($"AI {ai} is not supported.", out error);
            }
        }
        data = new Gs1Data(gtin, lot, expiry, serial);
        return true;
    }

    /// <summary>Raw form: fixed-length AIs run on; variable ones end at a group separator or the end.</summary>
    private static List<(string Ai, string Value)>? SplitRaw(string text, out string error)
    {
        error = "";
        var fields = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == GroupSeparator)
            {
                i++;
                continue;
            }
            if (i + 2 > text.Length)
                return FailList("The barcode ends inside an application identifier.", out error);
            var ai = text.Substring(i, 2);
            i += 2;
            int? fixedLength = ai switch { "01" => 14, "17" => 6, _ => null };
            string value;
            if (fixedLength is { } length)
            {
                if (i + length > text.Length)
                    return FailList($"AI {ai} is truncated.", out error);
                value = text.Substring(i, length);
                i += length;
            }
            else if (ai is "10" or "21")
            {
                var end = text.IndexOf(GroupSeparator, i);
                if (end < 0)
                    end = text.Length;
                value = text[i..end];
                i = end;
            }
            else
            {
                return FailList($"AI {ai} is not supported.", out error);
            }
            fields.Add((ai, value));
        }
        return fields.Count == 0 ? FailList("The barcode has no application identifiers.", out error) : fields;
    }

    /// <summary>Bracketed form: <c>(AI)value</c> repeated; a value runs to the next <c>(</c>.</summary>
    private static List<(string Ai, string Value)>? SplitBracketed(string text, out string error)
    {
        error = "";
        var fields = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '(')
                return FailList("Expected '(' before an application identifier.", out error);
            var close = text.IndexOf(')', i);
            if (close < 0)
                return FailList("Unclosed application identifier.", out error);
            var ai = text[(i + 1)..close];
            if (ai.Length != 2 || !ai.All(char.IsAsciiDigit))
                return FailList($"'{ai}' is not a supported application identifier.", out error);
            var next = text.IndexOf('(', close + 1);
            if (next < 0)
                next = text.Length;
            fields.Add((ai, text[(close + 1)..next]));
            i = next;
        }
        return fields.Count == 0 ? FailList("The barcode has no application identifiers.", out error) : fields;
    }

    /// <summary>
    /// YYMMDD. Day 00 means the last day of the month. The century puts the year within 49 years before and
    /// 50 years after the current year (GS1 General Specifications 7.12).
    /// </summary>
    private static bool TryExpiry(string value, DateOnly today, out DateOnly date)
    {
        date = default;
        if (value.Length != 6 || !value.All(char.IsAsciiDigit))
            return false;
        var yy = int.Parse(value[..2], System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(value[2..4], System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(value[4..], System.Globalization.CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
            return false;

        var currentCentury = today.Year / 100 * 100;
        var difference = yy - today.Year % 100;
        var year = difference >= 51 ? currentCentury - 100 + yy
            : difference <= -50 ? currentCentury + 100 + yy
            : currentCentury + yy;

        var lastDay = DateTime.DaysInMonth(year, month);
        if (day == 0)
            day = lastDay;
        if (day > lastDay)
            return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    private static bool IsVariableValue(string value) =>
        value.Length is >= 1 and <= MaxVariableLength && value.All(c => char.IsAsciiLetterOrDigit(c) || Cset82Punctuation.Contains(c));

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static List<(string, string)>? FailList(string message, out string error)
    {
        error = message;
        return null;
    }
}
