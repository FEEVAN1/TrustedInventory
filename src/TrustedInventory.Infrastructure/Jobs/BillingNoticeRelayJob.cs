using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Jobs;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Infrastructure.Jobs;

/// <summary>
/// Moves billing notices into each tenant's outbox (DEC-064), one message per recipient (SUI-002, DEC-074): the workspace owners and
/// billing administrators (by subject; the delivery service resolves their verified address from the identity provider) and the
/// billing contact address on the billing account. The outbox event id is derived from the notice and the recipient, so a notice
/// reaches each recipient at most once even if the relay repeats. There are no preference settings that could suppress these
/// required notices. Runs in a per-tenant ti_app transaction, so the control-plane role never needs an outbox privilege (SAS-004);
/// warehouse users see none of it.
/// </summary>
public sealed partial class BillingNoticeRelayJob(TenantJobRunner runner, ILogger<BillingNoticeRelayJob> log) : IJob
{
    private const int Batch = 100;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var (tenantId, status) in await runner.TenantsAsync(BackgroundJob.BillingNotices, ct))
        {
            try
            {
                var relayed = await runner.UnitOfWorkFor(tenantId, status).WriteAsync(async (s, c) =>
                {
                    var (conn, tx) = TenantJobRunner.Raw(s);
                    var notices = new List<(Guid Id, string Kind, string Detail)>();
                    await using (var cmd = new NpgsqlCommand("""
                        SELECT id, kind::text, detail::text FROM cp.billing_notice
                        WHERE relayed_at IS NULL ORDER BY created_at, id LIMIT @n FOR UPDATE SKIP LOCKED
                        """, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("n", Batch);
                        await using var r = await cmd.ExecuteReaderAsync(c);
                        while (await r.ReadAsync(c))
                            notices.Add((r.GetGuid(0), r.GetString(1), r.GetString(2)));
                    }
                    if (notices.Count == 0)
                        return 0;
                    var recipients = await RecipientsAsync(conn, tx, c);
                    foreach (var (id, kind, detail) in notices)
                    {
                        foreach (var recipient in recipients)
                        {
                            await using var insert = new NpgsqlCommand("""
                                INSERT INTO ti.outbox_event (tenant_id, event_id, event_type, payload) VALUES (@t, @e, @type, @payload)
                                ON CONFLICT (event_id) DO NOTHING
                                """, conn, tx);
                            insert.Parameters.AddWithValue("t", tenantId);
                            insert.Parameters.AddWithValue("e", EventId(id, recipient.Key));
                            insert.Parameters.AddWithValue("type", "Billing" + kind);
                            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
                            {
                                Value = JsonSerializer.Serialize(new { noticeId = id, kind, recipient = recipient.Payload, detail = JsonNode.Parse(detail) },
                                    JsonDefaults.Options),
                            });
                            await insert.ExecuteNonQueryAsync(c);
                        }
                        await using var mark = new NpgsqlCommand(
                            "UPDATE cp.billing_notice SET relayed_at = GREATEST(@now, created_at) WHERE id = @id", conn, tx);
                        mark.Parameters.AddWithValue("now", s.Now);
                        mark.Parameters.AddWithValue("id", id);
                        await mark.ExecuteNonQueryAsync(c);
                    }
                    return notices.Count;
                }, ct);
                if (relayed > 0)
                    LogRelayed(log, relayed, tenantId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(log, e, tenantId);
            }
        }
    }

    /// <summary>A deterministic event id per notice and recipient (UUID version 8 from SHA-256), the outbox's dedupe key.</summary>
    public static Guid EventId(Guid noticeId, string recipientKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{noticeId:N}|{recipientKey}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static async Task<List<(string Key, object Payload)>> RecipientsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var recipients = new List<(string, object)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT DISTINCT g.grantee_subject, g.role::text FROM ti.role_grant g JOIN ti.membership m ON m.tenant_id = g.tenant_id AND m.id = g.membership_id
            WHERE g.revoked_at IS NULL AND m.active AND g.role IN ('TenantOwner', 'BillingAdministrator')
            ORDER BY 1, 2
            """, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct))
            {
                var subject = r.GetString(0);
                if (seen.Add(subject))
                    recipients.Add(($"subject:{subject}", new { type = "member", subject, role = r.GetString(1) }));
            }
        }
        await using (var cmd = new NpgsqlCommand("SELECT billing_contact_email FROM cp.billing_account", conn, tx))
            if (await cmd.ExecuteScalarAsync(ct) is string email && email.Length > 0)
                recipients.Add(($"email:{email.ToLowerInvariant()}", new { type = "billingContact", email }));
        return recipients;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relayed {Count} billing notices to the outbox of tenant {TenantId}")]
    private static partial void LogRelayed(ILogger logger, int count, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing notice relay failed for tenant {TenantId}; retried next run")]
    private static partial void LogFailed(ILogger logger, Exception error, Guid tenantId);
}
