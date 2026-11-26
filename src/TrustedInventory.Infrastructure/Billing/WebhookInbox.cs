using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Billing;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Billing;

/// <summary>
/// AES-256-GCM for inbox payloads (DEC-064). Associated data binds a ciphertext to its provider and event id, so a payload
/// cannot be moved to another row. Layout: version (1) | nonce (12) | tag (16) | ciphertext.
/// </summary>
public sealed class InboxCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public InboxCipher(byte[] key)
    {
        if (key.Length != 32)
            throw new InvalidOperationException("Billing:InboxKey must be base64 of exactly 32 bytes.");
        _key = key;
    }

    public byte[] Encrypt(string provider, string eventId, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[1 + NonceSize + TagSize + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize), Aad(provider, eventId));
        return output;
    }

    public byte[] Decrypt(string provider, string eventId, ReadOnlySpan<byte> stored)
    {
        if (stored.Length < 1 + NonceSize + TagSize || stored[0] != Version)
            throw new CryptographicException("Unknown inbox payload format.");
        var plaintext = new byte[stored.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(stored.Slice(1, NonceSize), stored[(1 + NonceSize + TagSize)..], stored.Slice(1 + NonceSize, TagSize), plaintext, Aad(provider, eventId));
        return plaintext;
    }

    private static byte[] Aad(string provider, string eventId) => Encoding.UTF8.GetBytes(provider + "|" + eventId);
}

/// <summary>
/// Durable inbox insert on the ti_control pool (BIL-004). Duplicates are no-ops; any database failure becomes
/// <see cref="WebhookStorageException"/> so the endpoint answers 503 and the provider retries.
/// </summary>
public sealed class WebhookInbox(ControlDataSource control, IBillingWebhooks webhooks, InboxCipher cipher, TimeProvider clock) : IWebhookInbox
{
    public Task<bool> StoreAsync(string provider, WebhookEnvelope envelope, byte[] rawBody, CancellationToken ct) =>
        InsertAsync(provider, rawBody, "Webhook", ct);

    /// <summary>
    /// An event fetched from the provider's authenticated API by reconciliation (DEC-066): same row, same dedupe, same processor. It
    /// is never reachable from a caller, so it needs no webhook signature.
    /// </summary>
    public Task<bool> StoreFetchedAsync(string provider, byte[] rawBody, CancellationToken ct) => InsertAsync(provider, rawBody, "Reconciliation", ct);

    private async Task<bool> InsertAsync(string provider, byte[] rawBody, string via, CancellationToken ct)
    {
        var parsed = webhooks.Parse(rawBody);
        var envelope = new WebhookEnvelope(parsed.EventId, parsed.EventType, parsed.CreatedAt);
        // References and type only: no amounts, names or emails (section 27).
        var redacted = JsonSerializer.Serialize(new
        {
            eventType = parsed.EventType,
            customer = parsed.CustomerRef,
            subscription = parsed.SubscriptionRef,
            checkoutSession = parsed.CheckoutSessionRef,
            invoice = parsed.Invoice?.Ref,
            payment = parsed.Payment?.Ref ?? parsed.Dispute?.PaymentRef,
            dispute = parsed.Dispute?.Ref,
        });
        try
        {
            await using var conn = await control.Source.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO cp.webhook_inbox (provider, event_id, event_type, provider_created_at, received_at, raw_body_digest, payload_encrypted,
                                              payload_redacted, next_attempt_at, received_via)
                VALUES (@p, @e, @type, @created, @now, @digest, @payload, @redacted, @now, @via)
                ON CONFLICT (provider, event_id) DO NOTHING
                """, conn);
            var now = clock.GetUtcNow();
            cmd.Parameters.AddWithValue("p", provider);
            cmd.Parameters.AddWithValue("e", envelope.EventId);
            cmd.Parameters.AddWithValue("type", envelope.EventType);
            cmd.Parameters.AddWithValue("created", (object?)envelope.CreatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("via", via);
            cmd.Parameters.AddWithValue("digest", SHA256.HashData(rawBody));
            cmd.Parameters.AddWithValue("payload", cipher.Encrypt(provider, envelope.EventId, rawBody));
            cmd.Parameters.Add(new NpgsqlParameter("redacted", NpgsqlDbType.Jsonb) { Value = redacted });
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            throw new WebhookStorageException("The billing event could not be stored.", e);
        }
    }
}
