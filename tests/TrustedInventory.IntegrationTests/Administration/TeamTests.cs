using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrustedInventory.Application.Administration;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Administration;

/// <summary>Invitations, seats, owner and billing roles, ownership transfer, entitlements (S1).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class TeamTests(ApiFixture fx)
{
    private sealed record Team(Guid TenantId, string Owner, Guid OwnerMembership, string Admin, Guid AdminMembership);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    /// <summary>A fresh tenant with an owner (TenantOwner + TenantAdministrator) and an administrator: two seats in use.</summary>
    private async Task<Team> NewTeamAsync(long seatLimit)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        var tenantId = Guid.CreateVersion7();
        await Exec(conn, "INSERT INTO ti.tenant (id, name, created_by) VALUES (@id, 'Team tenant', 'seed')", ("id", tenantId));
        await Exec(conn, "INSERT INTO ti.audit_stream (tenant_id) VALUES (@id)", ("id", tenantId));
        var owner = Unique("user-owner");
        var admin = Unique("user-admin");
        var ownerMembership = await Member(conn, tenantId, owner, "TenantOwner", "TenantAdministrator");
        var adminMembership = await Member(conn, tenantId, admin, "TenantAdministrator");
        var plan = await SaasSeed.PlanAsync(conn, Unique("plan").ToLowerInvariant(), SaasSeed.AllFeatures, seatLimit);
        await SaasSeed.ActiveSubscriptionAsync(conn, tenantId, plan);
        return new Team(tenantId, owner, ownerMembership, admin, adminMembership);
    }

    private HttpClient As(string subject, Guid? tenantId, string? email = null, bool emailVerified = true, bool mfa = true)
    {
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestTokens.Issue(subject, mfa: mfa, email: email, emailVerified: emailVerified));
        if (tenantId is { } t)
            client.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return client;
    }

    private string TokenFor(JsonObject invitation) =>
        fx.Factory.Services.GetRequiredService<IInvitationTokens>().Issue(Guid.Parse((string)invitation["id"]!));

    private static object Invite(string email, params object[] roles) => new { email, roles };

    [Fact]
    [Trait("Requirement", "ONB-005")]
    [Trait("Requirement", "AT-ONB-02")]
    public async Task Invited_user_with_the_bound_verified_email_joins_with_the_invited_roles_once()
    {
        var team = await NewTeamAsync(seatLimit: 10);
        var admin = As(team.Admin, team.TenantId);
        var email = $"{Unique("new")}@example.invalid";

        var created = await admin.PostJsonAsync("/api/v1/invitations", Invite(email.ToUpperInvariant(), new { role = "Picker" }));
        var invitation = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Pending", (string?)invitation["status"]);
        Assert.Equal(email, (string?)invitation["email"]);
        Assert.Null(invitation["token"]); // never returned or stored (ONB-005)

        var joiner = Unique("user-joiner");
        var accepted = await As(joiner, null, email).PostJsonAsync("/api/v1/invitations/accept", new { token = TokenFor(invitation) });
        var membership = await accepted.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(team.TenantId.ToString(), (string?)membership["tenantId"]);
        Assert.Equal("Picker", (string?)membership["roles"]![0]!["role"]);
        Assert.Equal("Normal", (string?)membership["accessMode"]);

        await (await As(joiner, team.TenantId, email).GetAsync("/api/v1/warehouses")).ExpectAsync(HttpStatusCode.OK);
        await (await As(joiner, null, email).PostJsonAsync("/api/v1/invitations/accept", new { token = TokenFor(invitation) }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        var list = await (await admin.GetAsync("/api/v1/invitations?status=Accepted")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(list["items"]!.AsArray(), i => (string?)i!["id"] == (string?)invitation["id"] && (string?)i["acceptedBy"] == joiner);
    }

    [Fact]
    [Trait("Requirement", "AT-ONB-02")]
    public async Task Mismatched_unverified_expired_or_revoked_invitations_create_no_membership()
    {
        var team = await NewTeamAsync(seatLimit: 10);
        var admin = As(team.Admin, team.TenantId);
        var joiner = Unique("user-joiner");
        var email = $"{Unique("bound")}@example.invalid";
        var invitation = await (await admin.PostJsonAsync("/api/v1/invitations", Invite(email, new { role = "Picker" })))
            .ExpectAsync(HttpStatusCode.Created);
        var token = TokenFor(invitation);

        await (await As(joiner, null, "someone.else@example.invalid").PostJsonAsync("/api/v1/invitations/accept", new { token }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await As(joiner, null, email, emailVerified: false).PostJsonAsync("/api/v1/invitations/accept", new { token }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await As(joiner, null, email).PostJsonAsync("/api/v1/invitations/accept", new { token = "x" + token[1..] }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // Expired: move the invitation's clock back past 72 hours.
        await using (var conn = new NpgsqlConnection(fx.SuperuserConnection))
        {
            await conn.OpenAsync();
            await Exec(conn, "UPDATE ti.invitation SET created_at = now() - interval '4 days', expires_at = now() - interval '1 day' WHERE id = @id",
                ("id", Guid.Parse((string)invitation["id"]!)));
        }
        await (await As(joiner, null, email).PostJsonAsync("/api/v1/invitations/accept", new { token }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        // Revoked: a fresh invitation (the expired one no longer blocks the address), then revoked before use.
        var second = await (await admin.PostJsonAsync("/api/v1/invitations", Invite(email, new { role = "Picker" })))
            .ExpectAsync(HttpStatusCode.Created);
        await (await admin.PostJsonAsync($"/api/v1/invitations/{second["id"]}/revoke", new { reason = "sent in error" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        await (await As(joiner, null, email).PostJsonAsync("/api/v1/invitations/accept", new { token = TokenFor(second) }))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(0, await MembershipCountAsync(team.TenantId, joiner));
    }

    [Fact]
    [Trait("Requirement", "AT-ONB-02")]
    [Trait("Requirement", "ENT-003")]
    public async Task Concurrent_acceptance_of_the_final_seat_admits_exactly_one()
    {
        var team = await NewTeamAsync(seatLimit: 3); // owner + admin use two
        var admin = As(team.Admin, team.TenantId);
        var joiners = Enumerable.Range(0, 4).Select(_ => (Subject: Unique("user-race"), Email: $"{Unique("race")}@example.invalid")).ToList();
        var tokens = new List<string>();
        foreach (var j in joiners)
            tokens.Add(TokenFor(await (await admin.PostJsonAsync("/api/v1/invitations", Invite(j.Email, new { role = "Picker" })))
                .ExpectAsync(HttpStatusCode.Created)));

        var responses = await Task.WhenAll(joiners.Select((j, i) =>
            As(j.Subject, null, j.Email).PostJsonAsync("/api/v1/invitations/accept", new { token = tokens[i] })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            var problem = await refused.ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");
            Assert.Equal("active_seats", (string?)problem["quota"]!["quota"]);
            Assert.Equal(3, (long)problem["quota"]!["limit"]!);
        }
    }

    [Fact]
    [Trait("Requirement", "ENT-003")]
    [Trait("Requirement", "ENT-004")]
    public async Task Memberships_use_seats_and_deactivation_frees_one()
    {
        var team = await NewTeamAsync(seatLimit: 3);
        var admin = As(team.Admin, team.TenantId);

        var first = await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = Unique("user-m1") })).ExpectAsync(HttpStatusCode.Created);
        await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = Unique("user-m2") }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "PLAN_LIMIT_REACHED");

        await (await admin.PostJsonAsync($"/api/v1/memberships/{first["id"]}/deactivate", new { reason = "left" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = Unique("user-m3") })).ExpectAsync(HttpStatusCode.Created);

        var entitlements = await (await admin.GetAsync("/api/v1/saas/entitlements")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(3, (long)entitlements["usage"]!["active_seats"]!);
        Assert.Equal(3, (long)entitlements["limits"]!["active_seats"]!);
        Assert.Contains(entitlements["features"]!.AsArray(), f => (string?)f == "core_inventory");
        Assert.Equal("Normal", (string?)entitlements["accessMode"]);
    }

    [Fact]
    [Trait("Requirement", "SOD-5")]
    [Trait("Requirement", "SOD-6")]
    [Trait("Requirement", "ONB-006")]
    public async Task Owner_and_billing_roles_change_only_through_their_own_paths()
    {
        var team = await NewTeamAsync(seatLimit: 10);
        var admin = As(team.Admin, team.TenantId);
        var owner = As(team.Owner, team.TenantId);
        var target = await (await admin.PostJsonAsync("/api/v1/memberships", new { userSubject = Unique("user-target") })).ExpectAsync(HttpStatusCode.Created);

        await (await admin.PostJsonAsync($"/api/v1/memberships/{target["id"]}/role-grants", new { role = "TenantOwner", reason = "try" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await admin.PostJsonAsync($"/api/v1/memberships/{target["id"]}/role-grants", new { role = "BillingAdministrator", reason = "try" }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var billing = await (await owner.PostJsonAsync($"/api/v1/memberships/{target["id"]}/role-grants",
            new { role = "BillingAdministrator", reason = "billing delegation" })).ExpectAsync(HttpStatusCode.Created);
        await (await admin.PostJsonAsync($"/api/v1/role-grants/{billing["id"]}/revoke", new { reason = "try" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        // Invitations follow the same rules.
        await (await admin.PostJsonAsync("/api/v1/invitations", Invite($"{Unique("b")}@example.invalid", new { role = "BillingAdministrator" })))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await admin.PostJsonAsync("/api/v1/invitations", Invite($"{Unique("o")}@example.invalid", new { role = "TenantOwner" })))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        // The last owner can neither lose the role through role-grant routes nor be deactivated.
        var ownerGrant = await OwnerGrantIdAsync(team.TenantId, team.OwnerMembership);
        await (await admin.PostJsonAsync($"/api/v1/role-grants/{ownerGrant}/revoke", new { reason = "try" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await admin.PostJsonAsync($"/api/v1/memberships/{team.OwnerMembership}/deactivate", new { reason = "try" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "ONB-006")]
    [Trait("Requirement", "SOD-7")]
    [Trait("Requirement", "AT-LFC-01")]
    public async Task Ownership_moves_only_after_reauthenticated_start_and_recipient_acceptance()
    {
        var team = await NewTeamAsync(seatLimit: 10);
        var owner = As(team.Owner, team.TenantId);
        object Start(string reauthToken) => new { toMembershipId = team.AdminMembership, reason = "handing over", reauthToken };

        await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers",
                Start(TestTokens.Issue(team.Owner, authTime: DateTimeOffset.UtcNow.AddMinutes(-10)))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Admin))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Owner, mfa: false))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "REAUTH_REQUIRED");
        // A tenant administrator who is not an owner cannot start a transfer at all.
        await (await As(team.Admin, team.TenantId).PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Admin))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var transfer = await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Owner))))
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Pending", (string?)transfer["status"]);
        await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Owner))))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        // Only the named recipient accepts.
        var bystander = await (await As(team.Admin, team.TenantId).PostJsonAsync("/api/v1/memberships", new { userSubject = Unique("user-by") }))
            .ExpectAsync(HttpStatusCode.Created);
        await (await As((string)bystander["userSubject"]!, team.TenantId).PostEmptyAsync($"/api/v1/saas/ownership-transfers/{transfer["id"]}/accept", "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");

        var accepted = await (await As(team.Admin, team.TenantId).PostEmptyAsync($"/api/v1/saas/ownership-transfers/{transfer["id"]}/accept", "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Accepted", (string?)accepted["status"]);

        Assert.Equal(0, await ActiveOwnerGrantsAsync(team.TenantId, team.OwnerMembership));
        Assert.Equal(1, await ActiveOwnerGrantsAsync(team.TenantId, team.AdminMembership));
        // The former owner lost owner permissions; the new owner has them.
        await (await owner.PostJsonAsync("/api/v1/saas/ownership-transfers", Start(TestTokens.Issue(team.Owner))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    public async Task Another_tenant_cannot_see_or_revoke_invitations()
    {
        var team = await NewTeamAsync(seatLimit: 10);
        var invitation = await (await As(team.Admin, team.TenantId).PostJsonAsync("/api/v1/invitations",
            Invite($"{Unique("iso")}@example.invalid", new { role = "Picker" }))).ExpectAsync(HttpStatusCode.Created);

        var other = fx.Client(fx.Seed.B.Admin, fx.Seed.B.TenantId);
        var list = await (await other.GetAsync("/api/v1/invitations?limit=200")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(list["items"]!.AsArray(), i => (string?)i!["id"] == (string?)invitation["id"]);
        await (await other.PostJsonAsync($"/api/v1/invitations/{invitation["id"]}/revoke", new { reason = "x" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    private async Task<long> MembershipCountAsync(Guid tenantId, string subject) =>
        await ScalarAsync("SELECT count(*) FROM ti.membership WHERE tenant_id = @t AND user_subject = @s", ("t", tenantId), ("s", subject));

    private async Task<long> ActiveOwnerGrantsAsync(Guid tenantId, Guid membershipId) =>
        await ScalarAsync("SELECT count(*) FROM ti.role_grant WHERE tenant_id = @t AND membership_id = @m AND role = 'TenantOwner' AND revoked_at IS NULL",
            ("t", tenantId), ("m", membershipId));

    private async Task<Guid> OwnerGrantIdAsync(Guid tenantId, Guid membershipId)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM ti.role_grant WHERE tenant_id = @t AND membership_id = @m AND role = 'TenantOwner' AND revoked_at IS NULL", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("m", membershipId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> Member(NpgsqlConnection conn, Guid tenantId, string subject, params string[] roles)
    {
        var id = Guid.CreateVersion7();
        await Exec(conn, "INSERT INTO ti.membership (id, tenant_id, user_subject, created_by) VALUES (@id, @t, @s, 'seed')",
            ("id", id), ("t", tenantId), ("s", subject));
        foreach (var role in roles)
            await Exec(conn, """
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
                VALUES (@t, @m, @s, @r::ti.role_name, 'test seed', 'seed', 'seed')
                """, ("t", tenantId), ("m", id), ("s", subject), ("r", role));
        return id;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
