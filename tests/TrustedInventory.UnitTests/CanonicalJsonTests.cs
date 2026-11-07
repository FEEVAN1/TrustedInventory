using System.Text.Json.Nodes;
using TrustedInventory.Application.Audit;

namespace TrustedInventory.UnitTests;

public class CanonicalJsonTests
{
    [Fact]
    [Trait("Requirement", "AUD-002")]
    public void Keys_are_sorted_by_utf16_code_units_without_whitespace()
    {
        var node = JsonNode.Parse("""{ "b": 1, "a": { "z": true, "A": null }, "é": "x", "Z": [3, 2] }""");
        Assert.Equal("""{"Z":[3,2],"a":{"A":null,"z":true},"b":1,"é":"x"}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    [Trait("Requirement", "AUD-002")]
    public void Strings_use_jcs_escaping()
    {
        var node = new JsonObject { ["s"] = "q\"b\\n\n\t\u0001/€" };
        Assert.Equal("{\"s\":\"q\\\"b\\\\n\\n\\t\\u0001/€\"}", CanonicalJson.Serialize(node));
    }

    [Fact]
    [Trait("Requirement", "AUD-002")]
    public void Parsed_and_constructed_nodes_canonicalise_identically()
    {
        var constructed = new JsonObject { ["n"] = 42L, ["s"] = "v", ["b"] = false };
        var parsed = JsonNode.Parse("""{"s":"v","b":false,"n":42}""");
        Assert.Equal(CanonicalJson.Serialize(parsed), CanonicalJson.Serialize(constructed));
    }

    [Theory]
    [Trait("Requirement", "AUD-002")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("9007199254740992")]
    public void Non_integer_or_unsafe_numbers_are_rejected(string number)
    {
        Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(JsonNode.Parse($$"""{"q":{{number}}}""")));
    }

    [Fact]
    [Trait("Requirement", "AUD-002")]
    public void Hash_chain_depends_on_previous_hash_and_body()
    {
        var a = AuditHash.Compute(AuditHash.Genesis, "{}");
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, AuditHash.Compute(a, "{}"));
        Assert.NotEqual(a, AuditHash.Compute(AuditHash.Genesis, "{ }"));
        // Pins the documented construction: SHA-256(previous_hash || UTF-8 body) (docs/migration-design.md §7).
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([.. new byte[32], .. "{}"u8]), a);
    }

    [Fact]
    [Trait("Requirement", "STK-007")]
    public void Payload_digest_ignores_key_order_but_not_values()
    {
        var a = CanonicalJson.PayloadDigest(JsonNode.Parse("""{"x":1.50,"y":"a"}"""));
        Assert.Equal(a, CanonicalJson.PayloadDigest(JsonNode.Parse("""{"y":"a","x":1.50}""")));
        Assert.NotEqual(a, CanonicalJson.PayloadDigest(JsonNode.Parse("""{"y":"a","x":1.5}""")));
    }
}
