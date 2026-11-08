using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Saas;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Infrastructure.Audit;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Saas;


/// <summary>
/// Durable provisioning job (ONB-002, AT-ONB-01). Every step runs in its own ti_control transaction together with
/// the job update that records it, so a crash between steps leaves a consistent job that resumes from the next
/// step. Steps are also individually idempotent, so a duplicate run under a lost lease cannot duplicate rows.
/// The tenant stays in Provisioning (no usable access, section 26) until the final step.
/// </summary>
public sealed class ProvisioningEngine(ControlDataSource control, TimeProvider clock, SaasOptions options, IProvisioningFaults faults)
    : IProvisioningEngine
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly string LeaseOwner = $"provisioning:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Failure codes that a retry cannot fix; the job fails at once and platform operations review it.</summary>
    private static readonly HashSet<string> Permanent = new(StringComparer.Ordinal) { "SLUG_TAKEN", "PLAN_UNAVAILABLE" };

    public async Task<ProvisioningJobView> CreateOrGetJobAsync(NewSignup signup, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var conn = await control.Source.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // Serialize this identity's signups before the key lookup, so a concurrent retry of the same key waits
            // and then replays, and a burst of new keys cannot pass the abuse count together (DEC-044).
            await ExecAsync(conn, tx, "SELECT pg_advisory_xact_lock(hashtextextended('signup:' || @s, 0))", ct, ("s", signup.OwnerSubject));

            if (await FindByKeyAsync(conn, tx, signup.SignupKey, ct) is { } existing)
            {
                if (!existing.Digest.AsSpan().SequenceEqual(signup.RequestDigest))
                    throw new AppException(ErrorCode.IdempotencyMismatch,
                        "This Idempotency-Key was already used with a different signup request.");
                return existing.View;
            }

            // Only new jobs count: replays returned above (ONB-007).
            await EnforceSignupLimitAsync(conn, tx, signup.OwnerSubject, ct);
            await EnsurePlanAsync(conn, tx, signup.PlanVersionId, ct);
            if (await SlugInUseAsync(conn, tx, signup.Slug, ct))
                throw AppException.Validation("requestedSlug", "This workspace address is already taken.");
            var abuse = await HadTrialBeforeAsync(conn, tx, signup.OwnerSubject, ct);

            var jobId = Guid.CreateVersion7();
            var tenantId = Guid.CreateVersion7();
            var now = Now();
            var request = new JsonObject
            {
                ["businessName"] = signup.BusinessName,
                ["slug"] = signup.Slug,
                ["billingCountry"] = signup.BillingCountry,
                ["planVersionId"] = signup.PlanVersionId.ToString(),
                ["termsVersion"] = signup.TermsVersion,
                ["privacyVersion"] = signup.PrivacyVersion,
                ["ownerEmail"] = signup.OwnerEmail,
            };
            try
            {
                await ExecAsync(conn, tx, """
                    INSERT INTO cp.provisioning_job (id, signup_key, requested_by_subject, request_digest, request, tenant_id,
                                                     abuse_review, created_at, updated_at, next_attempt_at)
                    VALUES (@id, @key, @subject, @digest, @request, @tenant, @abuse, @now, @now, @now)
                    """, ct, ("id", jobId), ("key", signup.SignupKey), ("subject", signup.OwnerSubject), ("digest", signup.RequestDigest),
                    ("request", Jsonb(request)), ("tenant", tenantId), ("abuse", abuse), ("now", now));
                foreach (var (document, version) in new[] { ("Terms", signup.TermsVersion), ("Privacy", signup.PrivacyVersion) })
                {
                    await ExecAsync(conn, tx, """
                        INSERT INTO cp.terms_acceptance (subject, provisioning_job_id, document, document_version, accepted_at)
                        VALUES (@subject, @job, @doc::cp.terms_document, @version, @now)
                        """, ct, ("subject", signup.OwnerSubject), ("job", jobId), ("doc", document), ("version", version), ("now", now));
                }
                await tx.CommitAsync(ct);
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation && attempt == 0)
            {
                // A concurrent retry with the same key committed first: read and replay it.
                continue;
            }
            return (await FindAsync(jobId, ct))!;
        }
    }

    public async Task<ProvisioningJobView> RunAsync(Guid jobId, CancellationToken ct)
    {
        if (!await TryLeaseAsync(jobId, ct))
            return (await FindAsync(jobId, ct)) ?? throw AppException.NotFound("Provisioning job");

        foreach (var step in Enum.GetValues<ProvisioningStep>())
        {
            try
            {
                if (!await RunStepAsync(jobId, step, ct))
                    continue;
                faults.AfterStepCommitted(jobId, step);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(jobId, step, ex, ct);
                break;
            }
        }
        return (await FindAsync(jobId, ct))!;
    }

    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var due = new List<Guid>();
        await using (var cmd = control.Source.CreateCommand("""
            SELECT id FROM cp.provisioning_job
            WHERE status IN ('Pending','Provisioning') AND next_attempt_at <= @now
              AND (lease_expires_at IS NULL OR lease_expires_at < @now)
            ORDER BY next_attempt_at LIMIT 20
            """))
        {
            cmd.Parameters.AddWithValue("now", Now());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                due.Add(r.GetGuid(0));
        }
        foreach (var id in due)
            await RunAsync(id, ct);
        return due.Count;
    }

    public async Task<ProvisioningJobView?> FindAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {JobColumns} FROM cp.provisioning_job WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", jobId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadJob(r).View : null;
    }

    // ---------------------------------------------------------------------------------------------------
    // Steps
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Runs one step if the job still needs it. Returns false when it was already complete.</summary>
    private async Task<bool> RunStepAsync(Guid jobId, ProvisioningStep step, CancellationToken ct)
    {
        await using var conn = await control.Source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        Job job;
        await using (var cmd = new NpgsqlCommand($"SELECT {JobColumns}, request::text, abuse_review FROM cp.provisioning_job WHERE id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", jobId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            job = ReadJob(r) with { Request = JsonNode.Parse(r.GetString(14))!.AsObject(), AbuseReview = r.GetBoolean(15) };
        }
        if (job.CompletedSteps.Contains(step) || job.View.Status is ProvisioningStatus.Ready or ProvisioningStatus.Failed)
            return false;
        if (job.LeaseOwner != LeaseOwner)
            throw new InvalidOperationException("The provisioning lease was lost to another runner.");

        await ExecAsync(conn, tx, "UPDATE cp.provisioning_job SET current_step = @s::cp.provisioning_step, updated_at = @now WHERE id = @id",
            ct, ("s", step.ToString()), ("now", Now()), ("id", jobId));

        var now = Now();
        var tenantId = job.View.TenantId;
        var subject = job.View.RequestedBySubject;
        var audit = new List<AuditEntry>();
        string outcome;
        switch (step)
        {
            case ProvisioningStep.AllocateTenant:
                if (await SlugTakenByOtherTenantAsync(conn, tx, (string)job.Request["slug"]!, tenantId, ct))
                    throw new ProvisioningFailure("SLUG_TAKEN");
                await ExecAsync(conn, tx, """
                    INSERT INTO ti.tenant (id, name, created_by, created_at, updated_at, lifecycle, slug, home_region, provisioning_job_id)
                    VALUES (@t, @name, @subject, @now, @now, 'Provisioning', @slug, @region, @job)
                    ON CONFLICT (id) DO NOTHING
                    """, ct, ("t", tenantId), ("name", (string)job.Request["businessName"]!), ("subject", subject), ("now", now),
                    ("slug", (string)job.Request["slug"]!), ("region", options.HomeRegion), ("job", jobId));
                await ExecAsync(conn, tx, "INSERT INTO ti.audit_stream (tenant_id) VALUES (@t) ON CONFLICT DO NOTHING", ct, ("t", tenantId));
                audit.Add(new AuditEntry("TenantCreated", "Tenant", tenantId, 1, "Workspace signup", null,
                    new { name = (string)job.Request["businessName"]!, slug = (string)job.Request["slug"]!, homeRegion = options.HomeRegion, lifecycle = "Provisioning" }));
                outcome = "created";
                break;

            case ProvisioningStep.CreateOwnerMembership:
                var membershipId = await ScalarAsync<Guid?>(conn, tx,
                    "SELECT id FROM ti.membership WHERE tenant_id = @t AND user_subject = @s", ct, ("t", tenantId), ("s", subject));
                if (membershipId is null)
                {
                    membershipId = Guid.CreateVersion7();
                    await ExecAsync(conn, tx, """
                        INSERT INTO ti.membership (id, tenant_id, user_subject, created_by, created_at, updated_at)
                        VALUES (@m, @t, @s, @s, @now, @now)
                        """, ct, ("m", membershipId), ("t", tenantId), ("s", subject), ("now", now));
                    audit.Add(new AuditEntry("MembershipCreated", "Membership", membershipId, 1, "Workspace owner", null,
                        new { userSubject = subject }));
                }
                // DEC-036: the first owner also administers the workspace so setup can start.
                foreach (var role in new[] { "TenantOwner", "TenantAdministrator" })
                {
                    var granted = await ScalarAsync<bool>(conn, tx, """
                        SELECT EXISTS (SELECT 1 FROM ti.role_grant WHERE tenant_id = @t AND membership_id = @m
                                       AND role = @r::ti.role_name AND warehouse_id IS NULL AND revoked_at IS NULL)
                        """, ct, ("t", tenantId), ("m", membershipId), ("r", role));
                    if (granted)
                        continue;
                    var grantId = Guid.CreateVersion7();
                    await ExecAsync(conn, tx, """
                        INSERT INTO ti.role_grant (id, tenant_id, membership_id, grantee_subject, role, reason, granted_by, granted_at,
                                                   created_by, created_at, updated_at)
                        VALUES (@g, @t, @m, @s, @r::ti.role_name, 'Workspace signup', 'system:signup', @now, 'system:signup', @now, @now)
                        """, ct, ("g", grantId), ("t", tenantId), ("m", membershipId), ("s", subject), ("r", role), ("now", now));
                    audit.Add(new AuditEntry("RoleGranted", "RoleGrant", grantId, 1, "Workspace signup", null,
                        new { granteeSubject = subject, role }));
                }
                outcome = membershipId.Value.ToString();
                break;

            case ProvisioningStep.CreateBillingAccount:
                await ExecAsync(conn, tx, """
                    INSERT INTO cp.billing_account (tenant_id, billing_contact_email, business_legal_name, country, created_by, created_at, updated_at)
                    VALUES (@t, @email, @name, @country, @s, @now, @now)
                    ON CONFLICT (tenant_id) DO NOTHING
                    """, ct, ("t", tenantId), ("email", (string)job.Request["ownerEmail"]!), ("name", (string)job.Request["businessName"]!),
                    ("country", (string)job.Request["billingCountry"]!), ("s", subject), ("now", now));
                audit.Add(new AuditEntry("BillingAccountCreated", "BillingAccount", null, null, null, null,
                    new { country = (string)job.Request["billingCountry"]! }));
                outcome = "created";
                break;

            case ProvisioningStep.AttachTrial:
                if (job.AbuseReview)
                {
                    // ONB-003: a repeat trial is not granted silently. The workspace is created without a trial and
                    // stays billing-restricted (reads, export, billing recovery) until a paid checkout or review.
                    audit.Add(new AuditEntry("TrialWithheld", "Subscription", null, null, "Repeat trial flagged for abuse review", null, null));
                    outcome = "withheld";
                    break;
                }
                var planId = Guid.Parse((string)job.Request["planVersionId"]!);
                var hasSubscription = await ScalarAsync<bool>(conn, tx, "SELECT EXISTS (SELECT 1 FROM cp.subscription WHERE tenant_id = @t)", ct, ("t", tenantId));
                if (!hasSubscription)
                {
                    // Provisional window; MarkReady anchors it to the ready time (the trial starts at Ready, ONB-003).
                    await ExecAsync(conn, tx, """
                        WITH sub AS (
                          INSERT INTO cp.subscription (tenant_id, billing_account_id, plan_version_id, state, trial_start, trial_end, created_at, updated_at)
                          SELECT @t, a.id, @plan, 'Trialing', @now, @end, @now, @now FROM cp.billing_account a WHERE a.tenant_id = @t
                          RETURNING revision)
                        INSERT INTO cp.entitlement_snapshot (tenant_id, subscription_revision, entitlement_version, plan_version_id,
                                                             features, limits, effective_from, is_current)
                        SELECT @t, sub.revision, 1, p.id, p.features, p.quotas, @now, true FROM sub, cp.plan_version p WHERE p.id = @plan
                        """, ct, ("t", tenantId), ("plan", planId), ("now", now), ("end", now.AddDays(options.TrialDays)));
                }
                outcome = "trial";
                break;

            case ProvisioningStep.InitializeConfiguration:
                // Configuration version 1 is the built-in default and is never stored (DEC-030). Usage counters start
                // from the records provisioning created: the owner is the only seat.
                await ExecAsync(conn, tx, """
                    INSERT INTO ti.usage_counter (tenant_id, metric, used)
                    SELECT @t, m, CASE WHEN m = 'active_seats' THEN 1 ELSE 0 END
                    FROM unnest(enum_range(NULL::ti.quota_key)) m
                    ON CONFLICT DO NOTHING
                    """, ct, ("t", tenantId));
                outcome = "defaults";
                break;

            case ProvisioningStep.MarkReady:
                await ExecAsync(conn, tx, """
                    UPDATE ti.tenant SET lifecycle = 'Ready', ready_at = @now, updated_at = @now, version = version + 1
                    WHERE id = @t AND lifecycle = 'Provisioning'
                    """, ct, ("t", tenantId), ("now", now));
                var trialEnd = now.AddDays(options.TrialDays);
                var anchored = await ExecAsync(conn, tx, """
                    UPDATE cp.subscription SET trial_start = @now, trial_end = @end, updated_at = @now
                    WHERE tenant_id = @t AND state = 'Trialing'
                    """, ct, ("t", tenantId), ("now", now), ("end", trialEnd));
                await ExecAsync(conn, tx, """
                    UPDATE cp.entitlement_snapshot SET effective_from = LEAST(effective_from, @now) WHERE tenant_id = @t AND is_current
                    """, ct, ("t", tenantId), ("now", now));
                // Terms rows keep their job reference (append-only); the job links them to the tenant.
                audit.Add(new AuditEntry("TenantReady", "Tenant", tenantId, 2, null, new { lifecycle = "Provisioning" },
                    new { lifecycle = "Ready", readyAt = now }));
                if (anchored > 0)
                    audit.Add(new AuditEntry("TrialStarted", "Subscription", null, null, null, null,
                        new { trialStart = now, trialEnd, trialDays = options.TrialDays }));
                outcome = "ready";
                break;

            default:
                throw new InvalidOperationException($"Unknown provisioning step {step}.");
        }

        var ready = step == ProvisioningStep.MarkReady;
        await ExecAsync(conn, tx, """
            UPDATE cp.provisioning_job SET
              completed_steps = array_append(completed_steps, @s::cp.provisioning_step),
              step_outcomes = step_outcomes || jsonb_build_object(@s, @outcome),
              status = CASE WHEN @ready THEN 'Ready'::cp.provisioning_status ELSE 'Provisioning'::cp.provisioning_status END,
              current_step = CASE WHEN @ready THEN NULL ELSE current_step END,
              ready_at = CASE WHEN @ready THEN @now ELSE ready_at END,
              lease_owner = CASE WHEN @ready THEN NULL ELSE lease_owner END,
              lease_expires_at = CASE WHEN @ready THEN NULL ELSE @lease END,
              updated_at = @now
            WHERE id = @id
            """, ct, ("s", step.ToString()), ("outcome", outcome), ("ready", ready), ("now", now), ("lease", now + Lease), ("id", jobId));
        // The audit stream exists from the first step on; events go into the new tenant's own chain (AUD-001).
        await AuditAppender.AppendAsync(conn, tx, tenantId, new AuditActor(subject, ActorType.User, $"provisioning:{jobId}", null),
                now, audit, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------------------------------------------
    // Leases, failures, lookups
    // ---------------------------------------------------------------------------------------------------

    private async Task<bool> TryLeaseAsync(Guid jobId, CancellationToken ct)
    {
        var now = Now();
        await using var cmd = control.Source.CreateCommand("""
            UPDATE cp.provisioning_job SET lease_owner = @owner, lease_expires_at = @until, status = 'Provisioning', updated_at = @now
            WHERE id = @id AND status IN ('Pending','Provisioning')
              AND (lease_owner IS NULL OR lease_expires_at < @now OR lease_owner = @owner)
            """);
        cmd.Parameters.AddWithValue("owner", LeaseOwner);
        cmd.Parameters.AddWithValue("until", now + Lease);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", jobId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Retryable failures back off exponentially; after 10 attempts, or a permanent cause, the job fails.</summary>
    private async Task RecordFailureAsync(Guid jobId, ProvisioningStep step, Exception ex, CancellationToken ct)
    {
        var code = ex is ProvisioningFailure f ? f.Code : $"STEP_{step.ToString().ToUpperInvariant()}_FAILED";
        var now = Now();
        await using var cmd = control.Source.CreateCommand("""
            UPDATE cp.provisioning_job SET
              attempts = LEAST(attempts + 1, 10),
              status = CASE WHEN @permanent OR attempts + 1 >= @max THEN 'Failed'::cp.provisioning_status ELSE 'Provisioning'::cp.provisioning_status END,
              failure_code = @code,
              next_attempt_at = @now + make_interval(secs => LEAST(3600, 5 * power(2, attempts))),
              lease_owner = NULL, lease_expires_at = NULL, updated_at = @now
            WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("permanent", Permanent.Contains(code));
        cmd.Parameters.AddWithValue("max", MaxAttempts);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsurePlanAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid planId, CancellationToken ct)
    {
        var ok = await ScalarAsync<bool>(conn, tx,
            "SELECT EXISTS (SELECT 1 FROM cp.plan_version WHERE id = @p AND published_at IS NOT NULL AND is_public)", ct, ("p", planId));
        if (!ok)
            throw AppException.Validation("planVersionId", "Choose a published plan.");
    }

    /// <summary>A slug is taken by a tenant or reserved by another live signup.</summary>
    private static Task<bool> SlugInUseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string slug, CancellationToken ct) =>
        ScalarAsync<bool>(conn, tx, """
            SELECT EXISTS (SELECT 1 FROM ti.tenant WHERE lower(slug) = @s)
                OR EXISTS (SELECT 1 FROM cp.provisioning_job WHERE request->>'slug' = @s AND status IN ('Pending','Provisioning'))
            """, ct, ("s", slug));

    private static Task<bool> SlugTakenByOtherTenantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string slug, Guid tenantId, CancellationToken ct) =>
        ScalarAsync<bool>(conn, tx, "SELECT EXISTS (SELECT 1 FROM ti.tenant WHERE lower(slug) = @s AND id <> @t)", ct, ("s", slug), ("t", tenantId));

    /// <summary>
    /// ONB-003: the verified identity already owns a workspace that had a trial. Business-level matching (name,
    /// tax reference) is left to platform review.
    /// </summary>
    private static Task<bool> HadTrialBeforeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string subject, CancellationToken ct) =>
        ScalarAsync<bool>(conn, tx, """
            SELECT EXISTS (
              SELECT 1 FROM ti.role_grant g JOIN cp.subscription s ON s.tenant_id = g.tenant_id
              WHERE g.grantee_subject = @s AND g.role = 'TenantOwner' AND s.trial_start IS NOT NULL)
            """, ct, ("s", subject));

    private static async Task<Job?> FindByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {JobColumns} FROM cp.provisioning_job WHERE signup_key = @k", conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadJob(r) : null;
    }

    private const string JobColumns = """
        id, tenant_id, requested_by_subject, status::text, current_step::text, failure_code, ready_at, created_at, updated_at,
        attempts, completed_steps::text[], request_digest, lease_owner, signup_key
        """;

    private static Job ReadJob(NpgsqlDataReader r)
    {
        var status = Enum.Parse<ProvisioningStatus>(r.GetString(3));
        var failure = r.IsDBNull(5) ? null : r.GetString(5);
        var view = new ProvisioningJobView(
            r.GetGuid(0), r.GetGuid(1), r.GetString(2), status,
            r.IsDBNull(4) ? null : Enum.Parse<ProvisioningStep>(r.GetString(4)),
            Retryable: status is ProvisioningStatus.Pending or ProvisioningStatus.Provisioning
                       || (status == ProvisioningStatus.Failed && failure is not null && !Permanent.Contains(failure)),
            failure,
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            r.GetFieldValue<DateTimeOffset>(7),
            r.GetFieldValue<DateTimeOffset>(8));
        return new Job(view, r.GetFieldValue<string[]>(10).Select(Enum.Parse<ProvisioningStep>).ToHashSet(), (byte[])r[11],
            r.IsDBNull(12) ? null : r.GetString(12), new JsonObject(), false);
    }

    /// <summary>New provisioning jobs per identity per rolling window (DEC-044). Caller holds the identity's lock.</summary>
    private async Task EnforceSignupLimitAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string subject, CancellationToken ct)
    {
        var now = Now();
        await using var cmd = new NpgsqlCommand("""
            SELECT created_at FROM cp.provisioning_job
            WHERE requested_by_subject = @s AND created_at > @since
            ORDER BY created_at LIMIT @limit
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", subject);
        cmd.Parameters.AddWithValue("since", now - SaasOptions.SignupWindow);
        cmd.Parameters.AddWithValue("limit", options.SignupsPerIdentityPerDay);
        var created = new List<DateTimeOffset>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                created.Add(reader.GetFieldValue<DateTimeOffset>(0));
        if (created.Count >= options.SignupsPerIdentityPerDay)
            throw AppException.RateLimited("Too many new workspaces for this identity. Retry later.",
                created[0] + SaasOptions.SignupWindow - now);
    }

    private DateTimeOffset Now()
    {
        var t = clock.GetUtcNow();
        return new DateTimeOffset(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);
    }

    private static NpgsqlParameter Jsonb(JsonNode node) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = node.ToJsonString() };

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
        {
            if (value is NpgsqlParameter p)
            {
                p.ParameterName = name;
                cmd.Parameters.Add(p);
            }
            else
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }

    private sealed record Job(ProvisioningJobView View, IReadOnlySet<ProvisioningStep> CompletedSteps, byte[] Digest, string? LeaseOwner,
        JsonObject Request, bool AbuseReview);

    private sealed class ProvisioningFailure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}

/// <summary>Published, public plans with currently active prices, read without tenant context (ti_app policy).</summary>
public sealed class PlanCatalog(AppDataSource app) : IPlanCatalog
{
    public async Task<IReadOnlyList<PlanView>> ListPublishedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await app.Source.OpenConnectionAsync(ct);
        var plans = new Dictionary<Guid, (Guid Id, string Code, int Version, string Name, string[] Features, string Quotas, DateTimeOffset Published)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, plan_code, version, display_name, features, quotas::text, published_at
            FROM cp.plan_version WHERE published_at IS NOT NULL AND is_public
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                plans[r.GetGuid(0)] = (r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetFieldValue<string[]>(4),
                    r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));
        }

        var prices = new List<PriceView>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, plan_version_id, currency, minor_unit_amount, exponent, billing_interval::text, active_from, active_to
            FROM cp.price_version WHERE active_from <= @now AND (active_to IS NULL OR active_to > @now)
            ORDER BY billing_interval, minor_unit_amount
            """, conn))
        {
            cmd.Parameters.AddWithValue("now", now);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                prices.Add(new PriceView(r.GetGuid(0), r.GetGuid(1), r.GetString(2).Trim(), r.GetInt64(3), r.GetInt16(4), r.GetString(5),
                    r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)));
        }

        return plans.Values.Select(p =>
        {
            using var doc = JsonDocument.Parse(p.Quotas);
            var limits = doc.RootElement.EnumerateObject()
                .ToDictionary(e => e.Name, e => e.Value.ValueKind == JsonValueKind.Number ? e.Value.GetInt64() : (long?)null, StringComparer.Ordinal);
            return new PlanView(p.Id, p.Code, p.Version, p.Name, p.Features, limits, p.Published,
                prices.Where(x => x.PlanVersionId == p.Id).ToList());
        }).ToList();
    }
}
