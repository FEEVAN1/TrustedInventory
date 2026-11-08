using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Organization;

/// <summary>
/// Evidence for docs/saas-state-matrix.md through the real API pipeline (LFC-001, SAS-005, AT-BIL-03 partial).
/// Each access mode is arranged as the superuser on a dedicated tenant; one implemented endpoint per category is
/// called by a non-owner tenant administrator. Categories with no implemented endpoint yet (setup-status,
/// export, safety, ownership and billing on tenant routes) are covered by the AccessMatrix unit tests and gain
/// rows here as their endpoints land.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StateMatrixTests(ApiFixture fx)
{
    public enum Mode { Normal, Provisioning, BillingRestricted, SecuritySuspended, Closed }

    /// <summary>Expected outcome per category: null = allowed, otherwise (status, code).</summary>
    private static readonly Dictionary<Mode, (string Reason, (int, string)? Read, (int, string)? Write, (int, string)? Team, (int, string)? Protective)> Expected = new()
    {
        [Mode.Normal] = ("", null, null, null, null),
        [Mode.Provisioning] = ("Provisioning", (409, "INVALID_STATE"), (409, "INVALID_STATE"), (409, "INVALID_STATE"), (409, "INVALID_STATE")),
        [Mode.BillingRestricted] = ("TrialEnded", null, (403, "SUBSCRIPTION_RESTRICTED"), (403, "SUBSCRIPTION_RESTRICTED"), null),
        [Mode.SecuritySuspended] = ("SecuritySuspended", (403, "TENANT_SECURITY_SUSPENDED"), (403, "TENANT_SECURITY_SUSPENDED"),
            (403, "TENANT_SECURITY_SUSPENDED"), (403, "TENANT_SECURITY_SUSPENDED")),
        [Mode.Closed] = ("Archived", (403, "SUBSCRIPTION_RESTRICTED"), (403, "SUBSCRIPTION_RESTRICTED"), (403, "SUBSCRIPTION_RESTRICTED"), null),
    };

    [Theory]
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "AT-BIL-03")]
    [InlineData(Mode.Normal)]
    [InlineData(Mode.Provisioning)]
    [InlineData(Mode.BillingRestricted)]
    [InlineData(Mode.SecuritySuspended)]
    [InlineData(Mode.Closed)]
    public async Task Each_access_mode_allows_exactly_the_categories_in_the_state_matrix(Mode mode)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "matrix");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        // A member to deactivate (protective-admin), created while the tenant is still Normal.
        var member = await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = $"user-matrix-{Guid.NewGuid():N}" }))
            .ExpectAsync(HttpStatusCode.Created);
        await Arrange(tenant.TenantId, mode);
        var expected = Expected[mode];

        // identity: always answers, and reports the computed mode (section 29).
        var me = await (await fx.Client(tenant.Admin, tenantId: null).GetAsync("/api/v1/me")).ExpectAsync(HttpStatusCode.OK);
        var workspace = me["memberships"]!.AsArray().Single(m => (string?)m!["tenantId"] == tenant.TenantId.ToString())!;
        Assert.Equal(mode.ToString(), (string?)workspace["accessMode"]);

        await Check(admin.GetAsync("/api/v1/warehouses"), HttpStatusCode.OK, expected.Read, expected.Reason);
        await Check(admin.PostJsonAsync("/api/v1/warehouses", new { code = "M-" + Guid.NewGuid().ToString("N")[..6], name = "Matrix", timeZone = "UTC" }),
            HttpStatusCode.Created, expected.Write, expected.Reason);
        await Check(admin.PostJsonAsync("/api/v1/memberships", new { userSubject = $"user-matrix-{Guid.NewGuid():N}" }),
            HttpStatusCode.Created, expected.Team, expected.Reason);
        await Check(admin.PostJsonAsync($"/api/v1/memberships/{member["id"]}/deactivate", new { reason = "matrix check" }, "\"1\""),
            HttpStatusCode.OK, expected.Protective, expected.Reason);
    }

    [Fact]
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "AT-BIL-04")]
    public async Task Security_suspension_takes_precedence_over_billing_restriction()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "precedence");
        await Arrange(tenant.TenantId, Mode.BillingRestricted);
        await Arrange(tenant.TenantId, Mode.SecuritySuspended);
        var problem = await (await fx.Client(tenant.Admin, tenant.TenantId).GetAsync("/api/v1/warehouses"))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "TENANT_SECURITY_SUSPENDED");
        Assert.Equal(["SecuritySuspended", "TrialEnded"], Reasons(problem));
    }

    [Fact]
    [Trait("Requirement", "BIL-007")]
    [Trait("Requirement", "LFC-001")]
    public async Task Past_due_within_grace_keeps_normal_access()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "pastdue");
        await Sql("""
            UPDATE cp.subscription SET state = 'PastDue', period_end = now() - interval '1 day', grace_end = now() + interval '6 days'
            WHERE tenant_id = @t
            """, tenant.TenantId);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "PD-1", name = "Grace", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        await Sql("UPDATE cp.subscription SET grace_end = now() - interval '1 second' WHERE tenant_id = @t", tenant.TenantId);
        var problem = await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "PD-2", name = "Grace over", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Equal(["PaymentOverdue"], Reasons(problem));
    }

    [Fact]
    [Trait("Requirement", "AT-BIL-03")]
    [Trait("Requirement", "LFC-001")]
    public async Task Writes_block_at_trial_end_without_any_worker()
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "trialend");

        // Controllable clock (section 19). Truncated to microseconds, PostgreSQL's timestamptz precision, so the
        // stored trial_end equals the instant computed here. Moves stay within minutes of real time so token
        // validation (15-minute not-before backdating, 30-second skew) is unaffected whichever clock it reads.
        var start = DateTimeOffset.UtcNow;
        start = start.AddTicks(-(start.Ticks % 10));
        var clock = new FakeTimeProvider(start);
        var trialEnd = start.AddMinutes(2);
        await using var factory = fx.Factory.WithWebHostBuilder(web =>
            web.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(tenant.Admin));
        admin.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.TenantId.ToString());

        // The boundary is evaluated per request from stored clocks; nothing changes in the database at trial_end.
        await Sql("""
            UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                   trial_start = @end - interval '14 days', trial_end = @end
            WHERE tenant_id = @t
            """, tenant.TenantId, ("end", trialEnd));

        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "TE-1", name = "In trial", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        // One microsecond before trial_end the trial is still running.
        clock.SetUtcNow(trialEnd.AddTicks(-10));
        await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "TE-2", name = "Last instant", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        // At trial_end exactly, routine writes stop (now >= trial_end).
        clock.SetUtcNow(trialEnd);
        var problem = await (await admin.PostJsonAsync("/api/v1/warehouses", new { code = "TE-3", name = "After trial", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        Assert.Equal(["TrialEnded"], Reasons(problem));
        Assert.Equal("routine-write", (string?)problem["recovery"]!["blockedCategory"]);
        // Reads stay available while billing-restricted.
        await (await admin.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
    }

    private async Task Arrange(Guid tenantId, Mode mode)
    {
        switch (mode)
        {
            case Mode.Provisioning:
                await Sql("UPDATE ti.tenant SET lifecycle = 'Provisioning' WHERE id = @t", tenantId);
                break;
            case Mode.BillingRestricted:
                await Sql("""
                    UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                           trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
                    WHERE tenant_id = @t
                    """, tenantId);
                break;
            case Mode.SecuritySuspended:
                await Sql("""
                    UPDATE ti.tenant SET security_suspended_at = now(), security_suspension_reason = 'matrix test',
                           security_suspended_by = 'user-platform-admin' WHERE id = @t
                    """, tenantId);
                break;
            case Mode.Closed:
                await Sql("UPDATE ti.tenant SET lifecycle = 'Archived', archived_at = now() WHERE id = @t", tenantId);
                break;
        }
    }

    private static async Task Check(Task<HttpResponseMessage> call, HttpStatusCode allowed, (int Status, string Code)? rejection, string reason)
    {
        var response = await call;
        if (rejection is null)
        {
            await response.ExpectAsync(allowed);
            return;
        }
        var problem = await response.ExpectProblemAsync((HttpStatusCode)rejection.Value.Status, rejection.Value.Code);
        Assert.Contains(reason, Reasons(problem));
    }

    private static string[] Reasons(JsonObject problem) =>
        problem["recovery"]!["restrictionReasons"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();

    private async Task Sql(string sql, Guid tenantId, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }
}
