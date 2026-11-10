using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TrustedInventory.Application.Common;

/// <summary>
/// A non-zero signed wire quantity for adjustment deltas (EXC-004, DEC-014): up to 14 integer and 6 fractional digits.
/// Like <see cref="Quantity"/>, the raw text is kept so a malformed value becomes a 422 field error.
/// </summary>
[JsonConverter(typeof(SignedQuantityJsonConverter))]
public readonly partial record struct SignedQuantity
{
    public const string Message = "Use a non-zero decimal string with at most 6 decimal places, e.g. \"-3\" or \"12.5\".";

    private SignedQuantity(string raw) => Raw = raw;

    public string Raw { get; }

    public bool IsValid => Raw is not null && Pattern().IsMatch(Raw) && Value != 0;

    /// <summary>The numeric value. Only call after validation.</summary>
    public decimal Value => decimal.Parse(Raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    public static SignedQuantity Parse(string raw) => new(raw);

    public static SignedQuantity From(decimal value) => new(Quantity.Format(value));

    public override string ToString() => Raw;

    [GeneratedRegex("^-?(0|[1-9][0-9]{0,13})(\\.[0-9]{1,6})?$")]
    private static partial Regex Pattern();
}

public sealed class SignedQuantityJsonConverter : JsonConverter<SignedQuantity>
{
    public override SignedQuantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? SignedQuantity.Parse(reader.GetString()!)
            : throw new JsonException("Quantities are decimal strings, e.g. \"-3\".");

    public override void Write(Utf8JsonWriter writer, SignedQuantity value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Raw);
}
