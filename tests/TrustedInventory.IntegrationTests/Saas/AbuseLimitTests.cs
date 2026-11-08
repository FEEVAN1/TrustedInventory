using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// ONB-007 abuse limits (DEC-044): created records are counted, so replays never count; over the limit is 429
/// RATE_LIMITED with Retry-After until the oldest counted record leaves the window; parallel bursts cannot pass the
/// count together. Each test runs a host with a small limit; records are aged as the superuser instead of moving a clock.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AbuseLimitTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "ONB-007")]
    [Trait("Requirement", "AT-ONB-01")]
    public async Task Signup_limit_counts_new_workspaces_per_identity_and_never_replays()
    {
        await using var factory = WithSetting("Saas:Abuse:SignupsPerIdentityPerDay", 2);
        var owner = $"user-abuse-{Guid.NewGuid():N}";
        var client = ClientOn(factory, owner, null);
        var first = new SignupAttempt(fx.Seed.UnlimitedPlanId);

        var firstJob = (string?)(await (await client.SendAsync(first.Request())).ExpectAsync(HttpStatusCode.Accepted))["provisioningJobId"];
        for (var i = 0; i < 3; i++) // replays of the same key are not new signups
            Assert.Equal(firstJob, (string?)(await (await client.SendAsync(first.Request())).ExpectAsync(HttpStatusCode.Accepted))["provisioningJobId"]);
        await (await client.SendAsync(new SignupAttempt(fx.Seed.UnlimitedPlanId).Request())).ExpectAsync(HttpStatusCode.Accepted);

        // Third new workspace within 24 hours: limited, with the real wait (the first job ages out in just under 24 h).
        var third = new SignupAttempt(fx.Seed.UnlimitedPlanId);
        var limited = await client.SendAsync(third.Request());
        await limited.ExpectProblemAsync(HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.InRange(RetryAfter(limited), TimeSpan.FromHours(23), TimeSpan.FromHours(24));

        // A limited identity can still replay, and nobody else is affected.
        Assert.Equal(firstJob, (string?)(await (await client.SendAsync(first.Request())).ExpectAsync(HttpStatusCode.Accepted))["provisioningJobId"]);
        await (await ClientOn(factory, $"user-abuse-other-{Guid.NewGuid():N}", null).SendAsync(new SignupAttempt(fx.Seed.UnlimitedPlanId).Request()))
            .ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal(2L, await JobsAsync(owner));

        // Once the window has passed, the same key succeeds: the rejection was never stored against it.
        await TenantProvisioning.ExecAsSuperuserAsync(fx,
            "UPDATE cp.provisioning_job SET created_at = created_at - interval '25 hours' WHERE requested_by_subject = @s", ("s", owner));
        await (await client.SendAsync(third.Request())).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal(3L, await JobsAsync(owner));
    }

    [Fact]
    [Trait("Requirement", "ONB-007")]
    public async Task Parallel_new_signups_by_one_identity_cannot_pass_the_limit_together()
    {
        await using var factory = WithSetting("Saas:Abuse:SignupsPerIdentityPerDay", 2);
        var owner = $"user-burst-{Guid.NewGuid():N}";
        var client = ClientOn(factory, owner, null);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SendAsync(new SignupAttempt(fx.Seed.UnlimitedPlanId).Request())));

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(2L, await JobsAsync(owner));
    }

    [Fact]
    [Trait("Requirement", "ONB-007")]
    public async Task Invitation_limit_counts_new_invitations_per_tenant_including_revoked_ones()
    {
        await using var factory = WithSetting("Saas:Abuse:InvitationsPerTenantPerHour", 3);
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "abuse-invites");
        var admin = ClientOn(factory, tenant.Admin, tenant.TenantId);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add((string)(await (await admin.PostJsonAsync("/api/v1/invitations", Invite())).ExpectAsync(HttpStatusCode.Created))["id"]!);
        // Revoking does not give the slot back: creation is the abused action.
        await (await admin.PostJsonAsync($"/api/v1/invitations/{ids[0]}/revoke", new { reason = "abuse limit test" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);

        var limited = await admin.PostJsonAsync("/api/v1/invitations", Invite());
        await limited.ExpectProblemAsync(HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.InRange(RetryAfter(limited), TimeSpan.FromMinutes(55), TimeSpan.FromHours(1));
        Assert.Equal(3L, await InvitationsAsync(tenant.TenantId)); // the rejected request created nothing

        // The limit is per tenant.
        var other = await TenantProvisioning.ProvisionAsync(fx, "abuse-invites-other");
        await (await ClientOn(factory, other.Admin, other.TenantId).PostJsonAsync("/api/v1/invitations", Invite()))
            .ExpectAsync(HttpStatusCode.Created);

        // An hour later the tenant may invite again.
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            UPDATE ti.invitation SET created_at = created_at - interval '2 hours', expires_at = expires_at - interval '2 hours'
            WHERE tenant_id = @t
            """, ("t", tenant.TenantId));
        await (await admin.PostJsonAsync("/api/v1/invitations", Invite())).ExpectAsync(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Requirement", "ONB-007")]
    public async Task Parallel_invitations_by_one_tenant_cannot_pass_the_limit_together()
    {
        await using var factory = WithSetting("Saas:Abuse:InvitationsPerTenantPerHour", 3);
        var tenant = await TenantProvisioning.ProvisionAsync(fx, "abuse-burst");
        var admin = ClientOn(factory, tenant.Admin, tenant.TenantId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.PostJsonAsync("/api/v1/invitations", Invite())));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(3L, await InvitationsAsync(tenant.TenantId));
    }

    /// <summary>One signup: key and body stay fixed, so sending it again is a replay.</summary>
    private sealed class SignupAttempt(Guid planId)
    {
        private readonly Guid _key = Guid.NewGuid();
        private readonly string _slug = $"ab-{Guid.NewGuid():N}"[..20];

        public HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
            {
                Content = JsonContent.Create(new
                {
                    businessName = "Abuse Probe Traders",
                    requestedSlug = _slug,
                    billingCountry = "NG",
                    planVersionId = planId,
                    termsVersion = "terms-2026-11",
                    privacyVersion = "privacy-2026-11",
                }),
            };
            request.Headers.Add("Idempotency-Key", _key.ToString());
            return request;
        }
    }

    private WebApplicationFactory<Program> WithSetting(string key, int value) =>
        fx.Factory.WithWebHostBuilder(web => web.UseSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static HttpClient ClientOn(WebApplicationFactory<Program> factory, string subject, Guid? tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(subject));
        if (tenantId is { } t)
            client.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return client;
    }

    private static object Invite() =>
        new { email = $"invitee-{Guid.NewGuid():N}@example.invalid", roles = new[] { new { role = "Picker" } } };

    private static TimeSpan RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ?? throw new Xunit.Sdk.XunitException("No Retry-After delta on the 429.");

    private async Task<long> JobsAsync(string subject) =>
        (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM cp.provisioning_job WHERE requested_by_subject = @s", ("s", subject)))!;

    private async Task<long> InvitationsAsync(Guid tenantId) =>
        (long)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "SELECT count(*) FROM ti.invitation WHERE tenant_id = @t", ("t", tenantId)))!;
}
