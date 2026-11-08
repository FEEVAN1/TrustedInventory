using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Saas;

/// <summary>
/// ONB-007: responses never reveal whether an unrelated email address is registered (DEC-044). Invitation creation is
/// the only operation that takes an address from the caller; signup takes it from the verified token.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class EmailEnumerationTests(ApiFixture fx)
{
    /// <summary>Fields that legitimately differ between two invitations created back to back.</summary>
    private static readonly string[] PerInvitationFields = ["id", "email", "createdAt", "updatedAt", "expiresAt"];

    [Fact]
    [Trait("Requirement", "ONB-007")]
    public async Task Inviting_a_registered_address_looks_exactly_like_inviting_an_unknown_one()
    {
        // A registered identity: owns a workspace and holds a pending invitation in a third tenant.
        var registered = await TenantProvisioning.SignUpAsync(fx, "enum-owner");
        var registeredEmail = $"{registered.Admin}@example.invalid"; // the token's address (TestTokens)
        var third = await TenantProvisioning.ProvisionAsync(fx, "enum-third");
        await (await fx.Client(third.Admin, third.TenantId).PostJsonAsync("/api/v1/invitations", Invite(registeredEmail)))
            .ExpectAsync(HttpStatusCode.Created);
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@example.invalid";

        var tenant = await TenantProvisioning.ProvisionAsync(fx, "enum-inviter");
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        var toRegistered = await admin.PostJsonAsync("/api/v1/invitations", Invite(registeredEmail));
        var toUnknown = await admin.PostJsonAsync("/api/v1/invitations", Invite(unknownEmail));

        // Same status, same headers that matter, same body apart from per-invitation fields.
        Assert.Equal(HttpStatusCode.Created, toRegistered.StatusCode);
        Assert.Equal(toUnknown.StatusCode, toRegistered.StatusCode);
        Assert.Equal(toUnknown.Content.Headers.ContentType?.MediaType, toRegistered.Content.Headers.ContentType?.MediaType);
        Assert.Equal(toUnknown.Headers.Location is null, toRegistered.Headers.Location is null);
        var registeredBody = (await toRegistered.Content.ReadFromJsonAsync<JsonObject>())!;
        var unknownBody = (await toUnknown.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(registeredEmail, (string?)registeredBody["email"]);
        Assert.Equal(unknownEmail, (string?)unknownBody["email"]);
        Assert.True(JsonNode.DeepEquals(WithoutPerInvitationFields(unknownBody), WithoutPerInvitationFields(registeredBody)),
            $"registered: {registeredBody.ToJsonString()}\nunknown: {unknownBody.ToJsonString()}");

        // The only conflict ever reported is the caller's own pending invitation, for either kind of address.
        var againRegistered = await (await admin.PostJsonAsync("/api/v1/invitations", Invite(registeredEmail)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var againUnknown = await (await admin.PostJsonAsync("/api/v1/invitations", Invite(unknownEmail)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal((string?)againUnknown["detail"], (string?)againRegistered["detail"]);
    }

    [Fact]
    [Trait("Requirement", "ONB-007")]
    [Trait("Requirement", "ONB-001")]
    public async Task Signup_never_accepts_an_email_address_from_the_request_body()
    {
        var someoneElse = await TenantProvisioning.SignUpAsync(fx, "enum-victim");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saas/signup")
        {
            Content = JsonContent.Create(new
            {
                businessName = "Probe Traders",
                requestedSlug = $"p-{Guid.NewGuid():N}"[..20],
                billingCountry = "NG",
                planVersionId = fx.Seed.UnlimitedPlanId,
                termsVersion = "terms-2026-11",
                privacyVersion = "privacy-2026-11",
                email = $"{someoneElse.Admin}@example.invalid",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        // Unknown members are rejected before anything is looked up, so the address is never consulted.
        await (await fx.Client($"user-probe-{Guid.NewGuid():N}", tenantId: null).SendAsync(request))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "MALFORMED_REQUEST");
    }

    private static object Invite(string email) => new { email, roles = new[] { new { role = "Picker" } } };

    private static JsonObject WithoutPerInvitationFields(JsonObject body)
    {
        var copy = body.DeepClone().AsObject();
        foreach (var field in PerInvitationFields)
            copy.Remove(field);
        return copy;
    }
}
