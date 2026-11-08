using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.Infrastructure.Saas;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>Self-service signup and resumable provisioning (S1: ONB-001..007, AT-ONB-01, AT-SAS-01).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SignupTests(ApiFixture fx)
{
    private static string Slug() => $"s-{Guid.NewGuid():N}"[..20];

    private static HttpRequestMessage SignupRequest(string slug, Guid planId, Guid key, string business = "Synthetic Traders")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
        {
            Content = JsonContent.Create(new
            {
                businessName = business,
                requestedSlug = slug,
                billingCountry = "NG",
                planVersionId = planId,
                termsVersion = "terms-2026-11",
                privacyVersion = "privacy-2026-11",
            }),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return request;
    }

    [Fact]
    [Trait("Requirement", "AT-ONB-01")]
    [Trait("Requirement", "ONB-002")]
    [Trait("Requirement", "ONB-003")]
    [Trait("Requirement", "SAS-001")]
    public async Task Signup_retried_ten_times_gives_one_tenant_one_owner_and_one_trial_starting_at_ready()
    {
        var owner = $"user-signup-{Guid.NewGuid():N}";
        var client = fx.Client(owner, tenantId: null);
        var slug = Slug();
        var key = Guid.NewGuid();

        var responses = new List<JsonObject>();
        for (var i = 0; i < 10; i++)
        {
            var response = await client.SendAsync(SignupRequest(slug, fx.Seed.UnlimitedPlanId, key));
            responses.Add(await response.ExpectAsync(HttpStatusCode.Accepted));
            Assert.Equal($"/api/v1/saas/provisioning/{responses[^1]["provisioningJobId"]}", response.Headers.Location?.ToString());
        }
        Assert.Single(responses.Select(r => (string?)r["tenantId"]).Distinct());
        Assert.Single(responses.Select(r => (string?)r["provisioningJobId"]).Distinct());
        Assert.All(responses, r => Assert.Equal("Ready", (string?)r["status"]));
        var tenantId = Guid.Parse((string)responses[0]["tenantId"]!);

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM ti.tenant WHERE slug = @s", ("s", slug)));
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM ti.membership WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(1L, await Scalar<long>(conn,
            "SELECT count(*) FROM ti.role_grant WHERE tenant_id = @t AND role = 'TenantOwner' AND grantee_subject = @s", ("t", tenantId), ("s", owner)));
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM cp.billing_account WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM cp.subscription WHERE tenant_id = @t AND state = 'Trialing'", ("t", tenantId)));
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM cp.entitlement_snapshot WHERE tenant_id = @t AND is_current", ("t", tenantId)));
        Assert.Equal(2L, await Scalar<long>(conn, "SELECT count(*) FROM cp.terms_acceptance WHERE subject = @s", ("s", owner)));
        // The trial clock starts at Ready and lasts 14 days (ONB-003, DEC-040).
        Assert.True(await Scalar<bool>(conn, """
            SELECT s.trial_start = t.ready_at AND s.trial_end = t.ready_at + interval '14 days'
            FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id WHERE t.id = @t
            """, ("t", tenantId)));

        // The owner can work in the new workspace straight away (Ready + Trialing = normal access).
        var workspace = fx.Client(owner, tenantId);
        await (await workspace.PostJsonAsync("/api/v1/warehouses", new { code = "MAIN", name = "Main", timeZone = "Africa/Lagos" }))
            .ExpectAsync(HttpStatusCode.Created);

        // The same key with a different body is a different request (STK-007 conventions).
        await (await client.SendAsync(SignupRequest(slug, fx.Seed.UnlimitedPlanId, key, business: "Another Name")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "IDEMPOTENCY_MISMATCH");
    }

    [Fact]
    [Trait("Requirement", "AT-ONB-01")]
    public async Task Concurrent_retries_of_one_signup_create_one_job()
    {
        var client = fx.Client($"user-race-{Guid.NewGuid():N}", tenantId: null);
        var slug = Slug();
        var key = Guid.NewGuid();
        var bodies = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
            await (await client.SendAsync(SignupRequest(slug, fx.Seed.UnlimitedPlanId, key))).ExpectAsync(HttpStatusCode.Accepted)));
        Assert.Single(bodies.Select(b => (string?)b["provisioningJobId"]).Distinct());

        await using var conn = await OpenAsync();
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM ti.tenant WHERE slug = @s", ("s", slug)));
        Assert.Equal(1L, await Scalar<long>(conn,
            "SELECT count(*) FROM ti.membership m JOIN ti.tenant t ON t.id = m.tenant_id WHERE t.slug = @s", ("s", slug)));
    }

    [Fact]
    [Trait("Requirement", "AT-ONB-01")]
    [Trait("Requirement", "ONB-002")]
    [Trait("Requirement", "LFC-001")]
    public async Task Provisioning_interrupted_after_the_owner_step_resumes_without_duplicates()
    {
        var faults = new CrashOnceAfter(ProvisioningStep.CreateOwnerMembership);
        await using var factory = fx.Factory.WithWebHostBuilder(web =>
            web.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IProvisioningFaults>(faults))));
        var owner = $"user-resume-{Guid.NewGuid():N}";
        var client = Client(factory, owner, null);

        var accepted = await (await client.SendAsync(SignupRequest(Slug(), fx.Seed.UnlimitedPlanId, Guid.NewGuid())))
            .ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("Provisioning", (string?)accepted["status"]);
        var tenantId = Guid.Parse((string)accepted["tenantId"]!);
        var jobId = (string)accepted["provisioningJobId"]!;

        // While provisioning, the workspace is not usable: only identity and setup status (section 26).
        var status = await (await client.GetAsync($"/api/v1/saas/provisioning/{jobId}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Provisioning", (string?)status["status"]);
        Assert.True((bool)status["retryable"]!);
        Assert.Null((string?)status["readyAt"]);
        var workspace = Client(factory, owner, tenantId);
        await (await workspace.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await workspace.PostJsonAsync("/api/v1/warehouses", new { code = "X", name = "X", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await client.GetAsync("/api/v1/me")).ExpectAsync(HttpStatusCode.OK);

        await using var conn = await OpenAsync();
        Assert.Equal("Provisioning", await Scalar<string>(conn, "SELECT lifecycle::text FROM ti.tenant WHERE id = @t", ("t", tenantId)));
        Assert.Equal(0L, await Scalar<long>(conn, "SELECT count(*) FROM cp.subscription WHERE tenant_id = @t", ("t", tenantId)));

        // The worker sweep resumes the job once its retry time comes.
        await Exec(conn, "UPDATE cp.provisioning_job SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t", ("t", tenantId));
        await factory.Services.GetRequiredService<ProvisioningSweepJob>().RunOnceAsync(CancellationToken.None);

        var done = await (await client.GetAsync($"/api/v1/saas/provisioning/{jobId}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Ready", (string?)done["status"]);
        Assert.Null((string?)done["currentStep"]);
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM ti.membership WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(2L, await Scalar<long>(conn, "SELECT count(*) FROM ti.role_grant WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(1L, await Scalar<long>(conn, "SELECT count(*) FROM cp.subscription WHERE tenant_id = @t", ("t", tenantId)));
        Assert.True(await Scalar<bool>(conn, """
            SELECT s.trial_start = t.ready_at FROM cp.subscription s JOIN ti.tenant t ON t.id = s.tenant_id WHERE t.id = @t
            """, ("t", tenantId)));
        // Each step's audit events were written once, in the new tenant's own chain.
        Assert.Equal(1L, await Scalar<long>(conn,
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'MembershipCreated'", ("t", tenantId)));
        await (await workspace.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Requirement", "ONB-001")]
    public async Task Unverified_email_cannot_create_a_workspace()
    {
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue($"user-unverified-{Guid.NewGuid():N}", emailVerified: false));
        var slug = Slug();
        await (await client.SendAsync(SignupRequest(slug, fx.Seed.UnlimitedPlanId, Guid.NewGuid())))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        await using var conn = await OpenAsync();
        Assert.Equal(0L, await Scalar<long>(conn, "SELECT count(*) FROM cp.provisioning_job WHERE request->>'slug' = @s", ("s", slug)));
    }

    [Fact]
    [Trait("Requirement", "ONB-001")]
    [Trait("Requirement", "SAS-003")]
    public async Task Signup_validates_slug_plan_and_idempotency_key()
    {
        var client = fx.Client($"user-validate-{Guid.NewGuid():N}", tenantId: null);
        var taken = Slug();
        await TenantProvisioningSignup(taken);

        var problem = await (await client.SendAsync(SignupRequest(taken, fx.Seed.UnlimitedPlanId, Guid.NewGuid())))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Contains(problem["errors"]!.AsArray(), e => (string?)e!["field"] == "requestedSlug");
        await (await client.SendAsync(SignupRequest("platform", fx.Seed.UnlimitedPlanId, Guid.NewGuid())))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await client.SendAsync(SignupRequest("Bad_Slug!", fx.Seed.UnlimitedPlanId, Guid.NewGuid())))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var plan = await (await client.SendAsync(SignupRequest(Slug(), Guid.NewGuid(), Guid.NewGuid())))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Contains(plan["errors"]!.AsArray(), e => (string?)e!["field"] == "planVersionId");

        var noKey = SignupRequest(Slug(), fx.Seed.UnlimitedPlanId, Guid.NewGuid());
        noKey.Headers.Remove("Idempotency-Key");
        await (await client.SendAsync(noKey)).ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");

        // Ownership comes from the token only: a body field naming an owner is rejected outright.
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
        {
            Content = JsonContent.Create(new
            {
                businessName = "Forged", requestedSlug = Slug(), billingCountry = "NG", planVersionId = fx.Seed.UnlimitedPlanId,
                termsVersion = "t", privacyVersion = "p", ownerSubject = "someone-else",
            }),
        };
        forged.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await (await client.SendAsync(forged)).ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
    }

    [Fact]
    [Trait("Requirement", "ONB-007")]
    public async Task Provisioning_status_is_visible_only_to_the_signer()
    {
        var owner = $"user-status-{Guid.NewGuid():N}";
        var accepted = await TenantProvisioningSignup(Slug(), owner);
        var jobId = (string)accepted["provisioningJobId"]!;

        var mine = await (await fx.Client(owner, null).GetAsync($"/api/v1/saas/provisioning/{jobId}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Ready", (string?)mine["status"]);
        Assert.False((bool)mine["retryable"]!);
        Assert.Null((string?)mine["errorCode"]);

        var other = await (await fx.Client($"user-other-{Guid.NewGuid():N}", null).GetAsync($"/api/v1/saas/provisioning/{jobId}"))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        var unknown = await (await fx.Client(owner, null).GetAsync($"/api/v1/saas/provisioning/{Guid.NewGuid()}"))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal((string?)unknown["detail"], (string?)other["detail"]);
    }

    [Fact]
    [Trait("Requirement", "ONB-003")]
    public async Task A_second_signup_by_the_same_identity_gets_no_silent_trial()
    {
        var owner = $"user-repeat-{Guid.NewGuid():N}";
        await TenantProvisioningSignup(Slug(), owner);
        var second = await TenantProvisioningSignup(Slug(), owner);
        var tenantId = Guid.Parse((string)second["tenantId"]!);
        Assert.Equal("Ready", (string?)second["status"]);

        await using var conn = await OpenAsync();
        Assert.True(await Scalar<bool>(conn, "SELECT abuse_review FROM cp.provisioning_job WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(0L, await Scalar<long>(conn, "SELECT count(*) FROM cp.subscription WHERE tenant_id = @t", ("t", tenantId)));
        // Without a subscription the workspace is billing-restricted: no new work until checkout or review.
        await (await fx.Client(owner, tenantId).PostJsonAsync("/api/v1/warehouses", new { code = "X", name = "X", timeZone = "UTC" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
    }

    [Fact]
    [Trait("Requirement", "ENT-001")]
    [Trait("Requirement", "SAS-003")]
    public async Task Public_plan_catalog_shows_only_published_public_plans_with_minor_unit_prices()
    {
        await using var conn = await OpenAsync();
        var hiddenCode = $"hidden-{Guid.NewGuid():N}"[..20];
        var draftCode = $"draft-{Guid.NewGuid():N}"[..20];
        await Exec(conn, """
            INSERT INTO cp.plan_version (plan_code, version, display_name, features, quotas, is_public, published_at, created_by)
            VALUES (@h, 1, 'Contract only', '{core_inventory}', '{"active_seats":1,"active_warehouses":1,"active_skus":1,"active_devices":0,"attachment_bytes":1}', false, now(), 'seed'),
                   (@d, 1, 'Draft', '{core_inventory}', '{"active_seats":1,"active_warehouses":1,"active_skus":1,"active_devices":0,"attachment_bytes":1}', true, NULL, 'seed')
            """, ("h", hiddenCode), ("d", draftCode));

        var page = await (await fx.Anonymous().GetAsync("/api/v1/saas/plans?limit=200")).ExpectAsync(HttpStatusCode.OK);
        var plans = page["items"]!.AsArray();
        Assert.DoesNotContain(plans, p => (string?)p!["planCode"] is var c && (c == hiddenCode || c == draftCode));
        var unlimited = Assert.Single(plans, p => (string?)p!["id"] == fx.Seed.UnlimitedPlanId.ToString())!;
        Assert.Equal("Published", (string?)unlimited["status"]);
        Assert.Equal(14, (int)unlimited["trialDays"]!);
        var price = unlimited["prices"]!.AsArray()[0]!;
        Assert.Equal("XTS", (string?)price["amount"]!["currency"]);
        Assert.Equal(2, (int)price["amount"]!["exponent"]!);
        Assert.False((bool)price["displayApproved"]!);
    }

    [Fact]
    [Trait("Requirement", "AT-SAS-01")]
    [Trait("Requirement", "TEN-003")]
    [Trait("Requirement", "SAS-005")]
    public async Task Tenants_on_different_plans_sharing_one_pooled_connection_get_their_own_decisions()
    {
        var full = await TenantProvisioning.ProvisionAsync(fx, "plan-full");
        var narrow = await TenantProvisioning.ProvisionAsync(fx, "plan-narrow");
        await using (var conn = await OpenAsync())
        {
            var narrowPlan = await SaasSeed.PlanAsync(conn, $"no-inv-{Guid.NewGuid():N}"[..20], ["service_api"], 10);
            await Exec(conn, "UPDATE cp.subscription SET plan_version_id = @p WHERE tenant_id = @t", ("p", narrowPlan), ("t", narrow.TenantId));
            await Exec(conn, """
                UPDATE cp.entitlement_snapshot e SET plan_version_id = p.id, features = p.features, limits = p.quotas
                FROM cp.plan_version p WHERE p.id = @p AND e.tenant_id = @t AND e.is_current
                """, ("p", narrowPlan), ("t", narrow.TenantId));
        }

        // One physical connection serves both tenants, alternately.
        var singleConnection = new NpgsqlConnectionStringBuilder(fx.AppConnection) { MaxPoolSize = 1 }.ConnectionString;
        await using var factory = fx.Factory.WithWebHostBuilder(web => web.UseSetting("ConnectionStrings:App", singleConnection));
        var fullClient = Client(factory, full.Admin, full.TenantId);
        var narrowClient = Client(factory, narrow.Admin, narrow.TenantId);
        await (await fullClient.PostJsonAsync("/api/v1/warehouses", new { code = "FULL-1", name = "Full", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created);

        for (var i = 0; i < 3; i++)
        {
            var list = await (await fullClient.GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
            Assert.All(list["items"]!.AsArray(), w => Assert.Equal("FULL-1", (string?)w!["code"]));
            var denied = await (await narrowClient.GetAsync("/api/v1/warehouses")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FEATURE_NOT_ENABLED");
            Assert.Equal("core_inventory", (string?)denied["feature"]);
        }

        // Worker side: a job reading each tenant's access state on the same single-connection pool.
        await using var raw = DependencyInjection.BuildDataSource(singleConnection);
        var reader = new TenantAccessReader(new AppDataSource(raw));
        foreach (var (tenant, hasInventory) in new[] { (full.TenantId, true), (narrow.TenantId, false), (full.TenantId, true) })
        {
            var state = await reader.ReadAsync(tenant, CancellationToken.None);
            Assert.NotNull(state);
            Assert.Equal(tenant, state.TenantId);
            Assert.Equal(hasInventory, state.HasFeature(FeatureKeys.CoreInventory, DateTimeOffset.UtcNow));
        }
    }

    private async Task<JsonObject> TenantProvisioningSignup(string slug, string? owner = null)
    {
        var client = fx.Client(owner ?? $"user-slug-{Guid.NewGuid():N}", tenantId: null);
        return await (await client.SendAsync(SignupRequest(slug, fx.Seed.UnlimitedPlanId, Guid.NewGuid()))).ExpectAsync(HttpStatusCode.Accepted);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string subject, Guid? tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Issue(subject));
        if (tenantId is { } t)
            client.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return client;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Simulates a crash right after one step commits, once (AT-ONB-01 resumption evidence).</summary>
    private sealed class CrashOnceAfter(ProvisioningStep step) : IProvisioningFaults
    {
        private int _fired;

        public void AfterStepCommitted(Guid jobId, ProvisioningStep completed)
        {
            if (completed == step && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException($"Simulated crash after {step}.");
        }
    }
}
