using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TrustedInventory.Application.Administration;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Billing;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Operations;
using TrustedInventory.Domain.Saas;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.Application.Saas;

public sealed record ClosureRequest(string Reason, bool RequestedDeletion, string Confirmation, string ReauthToken);

public sealed record ClosureDto(Guid Id, ClosureStatus Status, DateTimeOffset RequestedAt, string RequestedBy, bool RequestedDeletion,
    DateTimeOffset? ExportWindowEndsAt, Guid? ArchiveExportId, int OutstandingInvoiceCount, IReadOnlyList<BlockerDto> Blockers);

public sealed record RetentionHoldRequest(bool Hold, string Reason, string? Reference);

public sealed record WithdrawClosureRequest(string Reason);

public sealed record ApproveDeletionRequest(string Reason);

/// <summary>A deletion record as stored (cp.deletion_record).</summary>
public sealed record ClosureRow(Guid Id, Guid TenantId, ClosureStatus Status, DateTimeOffset RequestedAt, string RequestedBy, bool DeletionRequested,
    DateTimeOffset? ExportWindowEndsAt, Guid? ArchiveExportId, int OutstandingInvoices, IReadOnlyList<ClosureBlocker> Blockers, Guid? IdempotencyKey)
{
    public ClosureDto Dto() => new(Id, Status, RequestedAt, RequestedBy, DeletionRequested, ExportWindowEndsAt, ArchiveExportId, OutstandingInvoices,
        Blockers.Select(ClosureBlockers.Dto).ToList());
}

/// <summary>The tenant and subscription facts closure needs, read on the control plane.</summary>
public sealed record ClosureTenant(Guid TenantId, string Slug, TenantLifecycle Lifecycle, int Version, SubscriptionState? Subscription,
    bool CancelAtPeriodEnd, DateTimeOffset? ServiceEnds);

public sealed record ClosureCommand(Guid IdempotencyKey, string Reason, bool DeletionRequested, byte[] ReauthReference, int OutstandingInvoices,
    Guid? ArchiveExportId = null);

/// <summary>Closure records on the ti_control pool (DEC-070). Every write appends its audit events to the tenant's chain in the same transaction.</summary>
public interface IClosureStore
{
    Task<ClosureTenant?> LoadTenantAsync(Guid tenantId, CancellationToken ct);

    Task<ClosureRow?> FindByKeyAsync(Guid tenantId, Guid idempotencyKey, CancellationToken ct);

    Task<int> OutstandingInvoicesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Records (or refreshes) the open Blocked record; nothing else changes.</summary>
    Task<ClosureRow> RecordBlockedAsync(BillingActor actor, ClosureCommand cmd, IReadOnlyList<ClosureBlocker> blockers, DateTimeOffset now,
        CancellationToken ct);

    /// <summary>Locks the tenant, requires Ready, opens the export window and archives the tenant (one transaction).</summary>
    Task<ClosureRow> ArchiveAsync(BillingActor actor, ClosureCommand cmd, int exportWindowDays, DateTimeOffset now, CancellationToken ct);

    Task SetRetentionHoldAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, RetentionHoldRequest request, DateTimeOffset now,
        CancellationToken ct);

    Task WithdrawAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>Records platform approval of an owner-requested deletion (LFC-004, DEC-072). Deletion itself waits for the export window.</summary>
    Task ApproveDeletionAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, string reason, DateTimeOffset now, CancellationToken ct);
}

public sealed class LifecycleOptions
{
    public int ExportWindowDays { get; init; } = ClosureRules.DefaultExportWindowDays;

    /// <summary>How long the last backup holding deleted data is kept (D06 proposed schedule); sets backup_expiry_at, never an erasure claim.</summary>
    public int BackupRetentionDays { get; init; } = 35;
}

public sealed class ClosureRequestValidator : AbstractValidator<ClosureRequest>
{
    public ClosureRequestValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Confirmation).NotEmpty().MaximumLength(80);
        RuleFor(x => x.ReauthToken).NotEmpty().MaximumLength(16_384);
    }
}

public sealed class RetentionHoldRequestValidator : AbstractValidator<RetentionHoldRequest>
{
    public RetentionHoldRequestValidator()
    {
        RuleFor(x => x.Reason).ValidReason();
        RuleFor(x => x.Reference).NotEmpty().When(x => x.Hold).WithMessage("Setting a hold needs the instruction reference.");
        RuleFor(x => x.Reference).MaximumLength(200);
    }
}

public sealed class WithdrawClosureRequestValidator : AbstractValidator<WithdrawClosureRequest>
{
    public WithdrawClosureRequestValidator() => RuleFor(x => x.Reason).ValidReason();
}

/// <summary>Safety and retention obligations that stop closure, read in a tenant transaction (ti_app; the control role reads no stock).</summary>
public static class ClosureBlockers
{
    public static async Task<IReadOnlyList<ClosureBlocker>> ComputeAsync(ITenantSession s, CancellationToken c)
    {
        var blockers = new List<ClosureBlocker>();
        var medical = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.stock_bucket b JOIN ti.product p ON p.tenant_id = b.tenant_id AND p.id = b.product_id
            WHERE p.handling_profile = 'Medical' AND b.on_hand_qty > 0
            """).SingleAsync(c);
        if (medical > 0)
            blockers.Add(new ClosureBlocker("MEDICAL_STOCK_ON_HAND", medical,
                $"{medical} medical stock positions are on hand. Ship, transfer out or dispose of them under QA control first."));
        var transit = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.transfer_line l
            JOIN ti.transfer t ON t.tenant_id = l.tenant_id AND t.id = l.transfer_id
            JOIN ti.product p ON p.tenant_id = l.tenant_id AND p.id = l.product_id
            WHERE p.handling_profile = 'Medical' AND t.status IN ('InTransit','PartReceived') AND l.dispatched_qty > l.received_qty + l.disposed_qty
            """).SingleAsync(c);
        if (transit > 0)
            blockers.Add(new ClosureBlocker("MEDICAL_IN_TRANSIT", transit, $"{transit} transfer lines with medical stock are in transit. Receive them first."));
        var held = await s.Db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM ti.stock_bucket WHERE quality_status IN ('Hold','Recalled') AND on_hand_qty > 0
            """).SingleAsync(c);
        if (held > 0)
            blockers.Add(new ClosureBlocker("ACTIVE_HOLDS", held,
                $"{held} stock positions are on quality hold or recalled. Resolve them through QA (disposition or release) first."));
        return blockers;
    }

    public static BlockerDto Dto(ClosureBlocker b) => new(b.Code, null, b.Count, null, b.Detail);

    public static AppException Blocked(IReadOnlyList<ClosureBlocker> blockers) =>
        new(ErrorCode.RetentionBlocked, "The workspace cannot close while safety obligations remain. Nothing was changed; safety work continues.")
        {
            Extensions = new JsonObject { ["blockers"] = JsonSerializer.SerializeToNode(blockers.Select(Dto).ToList(), JsonDefaults.Options) },
        };
}

/// <summary>
/// Workspace closure (LFC-002, DEC-070). Cancelling the subscription and closing the workspace stay separate actions; closing never
/// deletes anything: it archives the workspace, opens the export window and queues the tenant archive export.
/// </summary>
public sealed class ClosureService(
    IClosureStore store,
    IPlanChangeStore planChanges,
    IBillingProvider provider,
    ITenantUnitOfWork uow,
    ICallerAccessor callers,
    IReauthVerifier reauth,
    LifecycleOptions options,
    TimeProvider clock,
    IValidator<ClosureRequest> validator,
    IValidator<RetentionHoldRequest> holdValidator,
    IValidator<WithdrawClosureRequest> withdrawValidator)
{
    public const string ArchiveFilter = """{"from":null,"to":null,"warehouseId":null,"productId":null,"lotId":null}""";

    public async Task<ClosureDto> RequestAsync(Guid idempotencyKey, ClosureRequest request, CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantClose);
        if (!caller.IsOwner)
            throw AppException.Forbidden("Only the workspace owner can close the workspace.");
        validator.ValidateOrThrow(request);

        // A retry with the same key answers from the record (and finishes any step a lost response left undone).
        if (await store.FindByKeyAsync(caller.TenantId, idempotencyKey, ct) is { } existing)
            return Replay(existing, request);

        var tenant = await store.LoadTenantAsync(caller.TenantId, ct) ?? throw AppException.NotFound("Tenant");
        if (!ClosureRules.ConfirmationMatches(tenant.Slug, request.Confirmation))
            throw AppException.Validation("confirmation", $"Type \"{ClosureRules.Confirmation(tenant.Slug)}\" to confirm.");
        DomainGuard.Run(() => ClosureRules.EnsureClosable(tenant.Lifecycle));
        var reauthReference = await reauth.VerifyAsync(request.ReauthToken, caller.Subject, ct);

        var actor = new BillingActor(caller.TenantId, caller.Subject, caller.RequestId, caller.CorrelationId);
        var cmd = new ClosureCommand(idempotencyKey, request.Reason.Trim(), request.RequestedDeletion, reauthReference,
            await store.OutstandingInvoicesAsync(caller.TenantId, ct));

        var blockers = await uow.ReadAsync(ClosureBlockers.ComputeAsync, ct);
        if (blockers.Count > 0)
        {
            await store.RecordBlockedAsync(actor, cmd, blockers, clock.GetUtcNow(), ct);
            throw ClosureBlockers.Blocked(blockers);
        }

        // Nothing renews after closure: a paid subscription is cancelled at period end at the provider first (DEC-065 path).
        await CancelSubscriptionAsync(actor, tenant, cmd.Reason, ct);
        // The archive export is queued before the workspace is archived and linked in the same transaction that archives it: once
        // archived, the access gate refuses this route, so no later retry could add it (LFC-003).
        var exportId = await QueueArchiveExportAsync(caller, ct);
        var row = await store.ArchiveAsync(actor, cmd with { ArchiveExportId = exportId }, options.ExportWindowDays, clock.GetUtcNow(), ct);
        return row.Dto();
    }

    public Task<ClosureDto> GetAsync(CancellationToken ct)
    {
        var caller = callers.Caller;
        caller.Require(Permissions.TenantClose);
        if (!caller.IsOwner)
            throw AppException.Forbidden("Only the workspace owner can see closure status.");
        return uow.ReadAsync(async (s, c) =>
        {
            var rows = await s.Db.Database.SqlQuery<StoredClosure>($"""
                SELECT id AS "Id", status::text AS "Status", requested_at AS "RequestedAt", requested_by AS "RequestedBy",
                       deletion_requested AS "DeletionRequested", export_window_ends_at AS "ExportWindowEndsAt", archive_export_id AS "ArchiveExportId",
                       outstanding_invoice_count AS "OutstandingInvoices", hold_results::text AS "HoldResults"
                FROM cp.deletion_record ORDER BY requested_at DESC, id DESC LIMIT 1
                """).ToListAsync(c);
            var r = rows.SingleOrDefault() ?? throw AppException.NotFound("Closure");
            return new ClosureRow(r.Id, caller.TenantId, Enum.Parse<ClosureStatus>(r.Status), r.RequestedAt, r.RequestedBy, r.DeletionRequested,
                r.ExportWindowEndsAt, r.ArchiveExportId, r.OutstandingInvoices, ParseBlockers(r.HoldResults), null).Dto();
        }, ct);
    }

    public async Task SetRetentionHoldAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, RetentionHoldRequest request, CancellationToken ct)
    {
        caller.RequireAdministrator();
        holdValidator.ValidateOrThrow(request);
        await store.SetRetentionHoldAsync(caller, tenantId, expectedVersion, request with { Reason = request.Reason.Trim() }, clock.GetUtcNow(), ct);
    }

    public async Task WithdrawAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, WithdrawClosureRequest request, CancellationToken ct)
    {
        caller.RequireAdministrator();
        withdrawValidator.ValidateOrThrow(request);
        await store.WithdrawAsync(caller, tenantId, expectedVersion, request.Reason.Trim(), clock.GetUtcNow(), ct);
    }

    public async Task ApproveDeletionAsync(PlatformCaller caller, Guid tenantId, int expectedVersion, ApproveDeletionRequest request, CancellationToken ct)
    {
        caller.RequireAdministrator();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 2000)
            throw AppException.Validation("reason", "Give a reason of 1 to 2000 characters.");
        await store.ApproveDeletionAsync(caller, tenantId, expectedVersion, request.Reason.Trim(), clock.GetUtcNow(), ct);
    }

    public static IReadOnlyList<ClosureBlocker> ParseBlockers(string? holdResults)
    {
        if (holdResults is null)
            return [];
        var node = JsonNode.Parse(holdResults)?["blockers"];
        return node is null ? [] : JsonSerializer.Deserialize<List<ClosureBlocker>>(node, JsonDefaults.Options) ?? [];
    }

    /// <summary>
    /// The same key answers from the record. Only reachable while the workspace is still Ready (a Blocked attempt): once archived, the
    /// access gate refuses this route with recovery "view-closure" and <c>GET /saas/closure</c> answers instead.
    /// </summary>
    private static ClosureDto Replay(ClosureRow existing, ClosureRequest request)
    {
        if (existing.DeletionRequested != request.RequestedDeletion)
            throw new AppException(ErrorCode.IdempotencyMismatch, "This Idempotency-Key was used for a different closure request.");
        return existing.Status == ClosureStatus.Blocked ? throw ClosureBlockers.Blocked(existing.Blockers) : existing.Dto();
    }

    private async Task CancelSubscriptionAsync(BillingActor actor, ClosureTenant tenant, string reason, CancellationToken ct)
    {
        if (tenant.Subscription is not (SubscriptionState.Active or SubscriptionState.PastDue) || tenant.CancelAtPeriodEnd)
            return; // a trial simply ends; a cancelled or already-cancelling subscription needs nothing
        var context = await planChanges.LoadAsync(tenant.TenantId, Guid.Empty, ct) ?? throw AppException.NotFound("Subscription");
        if (context.CancelAtPeriodEnd)
            return;
        var subscriptionRef = context.ProviderSubscriptionRef
            ?? throw AppException.InvalidState("The subscription is not linked to the billing provider; contact support to close the workspace.");
        try
        {
            await provider.CancelAtPeriodEndAsync(context.SubscriptionId, context.Revision, subscriptionRef, ct);
        }
        catch (BillingProviderUnavailableException ex)
        {
            throw new AppException(ErrorCode.ServiceUnavailable, $"The billing provider is unavailable; retry with the same Idempotency-Key. {ex.Message}");
        }
        await planChanges.CancelAsync(actor, checked((int)context.Revision), $"Workspace closure: {reason}", clock.GetUtcNow(), ct);
    }

    /// <summary>Queues the tenant archive export for the closing owner (LFC-003).</summary>
    private Task<Guid> QueueArchiveExportAsync(CallerContext caller, CancellationToken ct) =>
        uow.WriteAsync(async (s, c) =>
        {
            var job = ExportJob.Queue(caller.TenantId, ExportType.TenantArchive, ArchiveFilter,
                SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(JsonNode.Parse(ArchiveFilter)))), caller.Subject, s.Now);
            s.Db.Add(job);
            s.Audit(new AuditEntry("ExportRequested", "ExportJob", job.Id, job.Version, "Workspace closure", null,
                new { exportId = job.Id, type = nameof(ExportType.TenantArchive) }));
            await s.Db.SaveChangesAsync(c);
            return job.Id;
        }, ct);

    private sealed record StoredClosure(Guid Id, string Status, DateTimeOffset RequestedAt, string RequestedBy, bool DeletionRequested,
        DateTimeOffset? ExportWindowEndsAt, Guid? ArchiveExportId, int OutstandingInvoices, string? HoldResults);
}
