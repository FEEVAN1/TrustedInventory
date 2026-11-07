using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TrustedInventory.Application.Common;

/// <summary>
/// A wire quantity: a decimal string with up to 14 integer and 6 fractional digits (DEC-014). The raw text is
/// kept so a malformed value is reported as a 422 field error by validation rather than a 400 parse failure.
/// </summary>
[JsonConverter(typeof(QuantityJsonConverter))]
public readonly partial record struct Quantity
{
    public const string PositiveMessage = "Use a decimal string greater than zero with at most 6 decimal places, e.g. \"12.5\".";

    private Quantity(string raw) => Raw = raw;

    public string Raw { get; }

    public bool IsWellFormed => Raw is not null && Pattern().IsMatch(Raw);

    /// <summary>The numeric value. Only call after validation.</summary>
    public decimal Value => decimal.Parse(Raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static Quantity Parse(string raw) => new(raw);

    public static Quantity From(decimal value) => new(Format(value));

    public static bool IsValidPositive(Quantity q) => q.IsWellFormed && q.Value > 0;

    /// <summary>Canonical text: no exponent, no trailing fractional zeros.</summary>
    public static string Format(decimal value)
    {
        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public override string ToString() => Raw;

    [GeneratedRegex("^(0|[1-9][0-9]{0,13})(\\.[0-9]{1,6})?$")]
    private static partial Regex Pattern();
}

public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{
    public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? Quantity.Parse(reader.GetString()!)
            : throw new JsonException("Quantities are decimal strings, e.g. \"12.5\".");

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Raw);
}
