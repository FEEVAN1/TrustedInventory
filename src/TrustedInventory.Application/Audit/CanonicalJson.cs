using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrustedInventory.Application.Audit;

/// <summary>
/// RFC 8785 (JCS) canonical JSON for audit hashing (AUD-002): keys sorted by UTF-16 code units, no
/// whitespace, JCS string escaping. Numbers are limited to integers within ±2^53; decimals such as
/// quantities must already be strings, so no floating-point formatting can differ between writers.
/// </summary>
public static class CanonicalJson
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node, rawNumbers: false);
        return sb.ToString();
    }

    /// <summary>
    /// SHA-256 of the request payload with sorted keys, for idempotency matching (STK-007). Numbers keep their
    /// literal text, so "1.50" and "1.5" count as different payloads, which is the conservative choice.
    /// </summary>
    public static byte[] PayloadDigest(JsonNode? payload)
    {
        var sb = new StringBuilder();
        Write(sb, payload, rawNumbers: true);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Write(StringBuilder sb, JsonNode? node, bool rawNumbers)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    Write(sb, value, rawNumbers);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Write(sb, arr[i], rawNumbers);
                }
                sb.Append(']');
                break;
            case JsonValue value:
                WriteValue(sb, value, rawNumbers);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value, bool rawNumbers)
    {
        // Nodes may wrap a parsed JsonElement or a CLR primitive; GetValueKind and the JSON text work for both.
        var kind = value.GetValueKind();
        switch (kind)
        {
            case JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.Number when rawNumbers:
                sb.Append(value.ToJsonString());
                break;
            case JsonValueKind.Number:
                var text = value.ToJsonString();
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    || n > MaxSafeInteger || n < -MaxSafeInteger)
                    throw new ArgumentException($"Audit JSON allows only safe integers; encode '{text}' as a string.");
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value kind {kind}.");
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>event_hash = SHA-256(previous_hash || UTF-8(canonical body)) (docs/migration-design.md section 7).</summary>
public static class AuditHash
{
    public static readonly byte[] Genesis = new byte[32];

    public static byte[] Compute(byte[] previousHash, string canonicalBody)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(previousHash.Length, 32);
        var body = Encoding.UTF8.GetBytes(canonicalBody);
        var buffer = new byte[32 + body.Length];
        previousHash.CopyTo(buffer, 0);
        body.CopyTo(buffer, 32);
        return SHA256.HashData(buffer);
    }
}
