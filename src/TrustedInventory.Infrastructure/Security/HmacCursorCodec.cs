using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Infrastructure.Security;

/// <summary>
/// Cursor = base64url(createdAt ticks | id | filter digest | HMAC-SHA256). Tampering, truncation or reuse with a
/// different filter fails verification and yields 400 INVALID_CURSOR (API-004).
/// </summary>
public sealed class HmacCursorCodec : ICursorCodec
{
    private const int PayloadLength = 8 + 16 + 16;
    private const int MacLength = 16;
    private readonly byte[] _key;

    public HmacCursorCodec(byte[] key)
    {
        if (key.Length < 32)
            throw new ArgumentException("The cursor signing key must be at least 32 bytes.", nameof(key));
        _key = key;
    }

    public string Encode(CursorPosition position, string filterDigest)
    {
        var buffer = new byte[PayloadLength + MacLength];
        BinaryPrimitives.WriteInt64BigEndian(buffer, position.CreatedAt.UtcTicks);
        position.Id.TryWriteBytes(buffer.AsSpan(8, 16));
        FilterDigest(filterDigest).CopyTo(buffer, 24);
        Mac(buffer.AsSpan(0, PayloadLength)).CopyTo(buffer, PayloadLength);
        return Base64Url.EncodeToString(buffer);
    }

    public CursorPosition Decode(string cursor, string filterDigest)
    {
        byte[] buffer;
        try
        {
            buffer = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            throw Invalid();
        }
        if (buffer.Length != PayloadLength + MacLength
            || !CryptographicOperations.FixedTimeEquals(Mac(buffer.AsSpan(0, PayloadLength)), buffer.AsSpan(PayloadLength))
            || !CryptographicOperations.FixedTimeEquals(FilterDigest(filterDigest), buffer.AsSpan(24, 16)))
            throw Invalid();

        var ticks = BinaryPrimitives.ReadInt64BigEndian(buffer);
        return new CursorPosition(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(buffer.AsSpan(8, 16)));
    }

    private byte[] Mac(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_key, payload)[..MacLength];

    private static byte[] FilterDigest(string filter) => SHA256.HashData(Encoding.UTF8.GetBytes(filter))[..16];

    private static AppException Invalid() => new(ErrorCode.InvalidCursor, "The cursor is invalid or belongs to a different query.");
}
