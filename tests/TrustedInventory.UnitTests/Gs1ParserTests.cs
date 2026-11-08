using TrustedInventory.Domain.Catalog;
using TrustedInventory.Domain.Inbound;

namespace TrustedInventory.UnitTests;

/// <summary>GS1 element string parsing for receiving (INB-002, DEC-048).</summary>
public class Gs1ParserTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private const char Gs = Gs1.GroupSeparator;
    private const string Gtin = "09506000134352";       // GS1 example GTIN-14
    private const string LeadingZeros = "00012345678905"; // UPC-A 012345678905 as GTIN-14

    private static Gs1Data Parse(string raw)
    {
        Assert.True(Gs1.TryParse(raw, Today, out var data, out var error), error);
        return data;
    }

    private static string Rejected(string? raw)
    {
        Assert.False(Gs1.TryParse(raw, Today, out _, out var error));
        return error;
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    public void Example_GTINs_are_valid() // guards the fixtures below
    {
        Assert.True(TrustedInventory.Domain.Catalog.Gtin.IsValidGtin14(Gtin));
        Assert.True(TrustedInventory.Domain.Catalog.Gtin.IsValidGtin14(LeadingZeros));
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    public void Bracketed_form_reads_all_four_identifiers()
    {
        var data = Parse($"(01){Gtin}(17)271231(10)ABC-123(21)SN/0001");
        Assert.Equal(new Gs1Data(Gtin, "ABC-123", new DateOnly(2027, 12, 31), "SN/0001"), data);
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    public void Raw_scanner_form_uses_group_separators_after_variable_fields_and_any_order()
    {
        Assert.Equal(new Gs1Data(Gtin, "LOT7", new DateOnly(2027, 1, 15), "S1"), Parse($"01{Gtin}17270115" + $"10LOT7{Gs}21S1"));
        Assert.Equal(new Gs1Data(Gtin, "LOT7", null, "S1"), Parse($"21S1{Gs}10LOT7{Gs}01{Gtin}"));
        Assert.Equal(new Gs1Data(Gtin, "LOT7", null, null), Parse($"]C101{Gtin}10LOT7")); // symbology prefix stripped
        Assert.Equal(new Gs1Data(Gtin, null, null, null), Parse($"]d201{Gtin}"));
    }

    [Fact]
    [Trait("Requirement", "INB-002")]
    public void Leading_zeros_are_preserved_in_every_field()
    {
        var data = Parse($"(01){LeadingZeros}(10)007(21)0000042");
        Assert.Equal(LeadingZeros, data.Gtin);
        Assert.Equal("007", data.Lot);
        Assert.Equal("0000042", data.Serial);
    }

    [Theory]
    [Trait("Requirement", "INB-002")]
    [InlineData("240200", 2024, 2, 29)]  // day 00: last day of the month, leap year
    [InlineData("250200", 2025, 2, 28)]
    [InlineData("761231", 2076, 12, 31)] // 50 years ahead stays in this century
    [InlineData("770101", 1977, 1, 1)]   // 51 years ahead belongs to the previous century
    [InlineData("990630", 1999, 6, 30)]
    public void Expiry_uses_the_GS1_century_window_and_day_zero(string yymmdd, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), Parse($"(01){Gtin}(17){yymmdd}").Expiry);

    [Theory]
    [Trait("Requirement", "INB-002")]
    [InlineData("(02)09506000134352", "not supported")]          // unsupported AI
    [InlineData("(01)09506000134353", "check digit")]             // wrong check digit
    [InlineData("(01)0950600013435", "check digit")]              // 13 digits
    [InlineData("(01)09506000134352(17)271301", "YYMMDD")]         // month 13
    [InlineData("(01)09506000134352(17)270230", "YYMMDD")]         // 30 February
    [InlineData("(01)09506000134352(10)ABCDEFGHIJKLMNOPQRSTU", "1 to 20")] // 21 characters
    [InlineData("(01)09506000134352(10)LOT 7", "1 to 20")]         // space is outside the GS1 character set
    [InlineData("(01)09506000134352(01)09506000134352", "twice")]
    [InlineData("0109506000134", "truncated")]
    [InlineData("(01", "Unclosed")]
    [InlineData("", "empty")]
    public void Malformed_or_unsupported_strings_are_rejected_with_a_reason(string raw, string reason) =>
        Assert.Contains(reason, Rejected(raw), StringComparison.Ordinal);

    [Fact]
    [Trait("Requirement", "INB-002")]
    public void A_null_barcode_is_rejected() => Assert.Contains("empty", Rejected(null), StringComparison.Ordinal);
}
