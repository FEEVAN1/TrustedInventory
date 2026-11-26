using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Medical;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Audit;

/// <summary>Authentication-relevant actions are audited with every field AUD-001 names, and never with the token itself.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReauthenticatedActionAuditTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AUD-001")]
    [Trait("Requirement", "IAM-003")]
    public async Task A_signature_after_reauthentication_is_audited_with_actor_request_version_and_reason_but_no_token()
    {
        var w = await MedicalWorld.CreateAsync(fx, "audit-reauth");
        var lot = await w.LotAsync(w.Medical, "L-AUD", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(200));
        var bucket = await w.BucketAsync(w.Medical, w.C01, 4, "Quarantine", lot);
        var approval = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "QualityRelease", subjectId = bucket, subjectVersion = await w.BucketVersionAsync(bucket), action = "Release", reason = "Release for audit",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;

        // A refused re-authentication (someone else's token) changes nothing and writes no signature event.
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.OperatorSubject), meaning = "QualityRelease", reason = "wrong person" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        Assert.Equal(0L, await CountAsync(w.Tenant, approval));

        var token = TestTokens.Issue(w.SignerSubject);
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = token, meaning = "QualityRelease", reason = "Certificate checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT actor_subject, actor_type::text, request_id, occurred_at, object_type, object_version, reason, before_state::text, after_state::text
            FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ApprovalSigned' AND object_id = @a
            """, conn);
        cmd.Parameters.AddWithValue("t", w.Tenant);
        cmd.Parameters.AddWithValue("a", Guid.Parse(approval));
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(w.SignerSubject, r.GetString(0));
        Assert.Equal("User", r.GetString(1));
        Assert.False(string.IsNullOrEmpty(r.IsDBNull(2) ? null : r.GetString(2)));                // request id
        Assert.Equal(TimeSpan.Zero, r.GetFieldValue<DateTimeOffset>(3).Offset);                  // UTC
        Assert.True(DateTimeOffset.UtcNow - r.GetFieldValue<DateTimeOffset>(3) < TimeSpan.FromMinutes(5));
        Assert.Equal("Approval", r.GetString(4));
        Assert.Equal(2, r.GetInt32(5));
        Assert.Equal("Certificate checked", r.GetString(6));
        var snapshots = r.GetString(7) + r.GetString(8);
        Assert.Contains(w.SignerSubject, snapshots, StringComparison.Ordinal);                    // the after-state names the signer
        Assert.DoesNotContain(token, snapshots, StringComparison.Ordinal);                          // redacted: no token material
        Assert.DoesNotContain(token.Split('.')[2], snapshots, StringComparison.Ordinal);
        Assert.False(await r.ReadAsync());
    }

    private async Task<long> CountAsync(Guid tenant, string approval) => Convert.ToInt64(await Organization.TenantProvisioning.ScalarAsSuperuserAsync(fx,
        "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ApprovalSigned' AND object_id = @a", ("t", tenant), ("a", Guid.Parse(approval))),
        System.Globalization.CultureInfo.InvariantCulture);
}
