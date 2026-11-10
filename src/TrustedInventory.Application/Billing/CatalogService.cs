using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Organization;
using TrustedInventory.Application.Saas;
using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Saas;

namespace TrustedInventory.Application.Billing;

/// <summary>Billing configuration (DEC-039, DEC-062). Only the simulator exists until the owner chooses a provider (D09).</summary>
public sealed record BillingOptions(string Provider, bool LiveCheckoutEnabled)
{
    public const string Simulator = "simulator";

    public bool SimulatorOnly => Provider == Simulator;

    /// <summary>Where the hosted pages send the browser back: navigation only, never proof of payment (BIL-002).</summary>
    public string ReturnUrl { get; init; } = "http://localhost:5173/billing/return";
}

public sealed record CatalogPriceRow(Guid Id, Guid PlanVersionId, string Currency, long MinorUnits, int Exponent, BillingInterval Interval,
    DateTimeOffset ActiveFrom, DateTimeOffset? ActiveTo, bool DisplayApproved, string? Provider, string? ProviderPriceRef);

public sealed record CatalogPlanRow(Guid Id, string PlanCode, int Version, string Name, IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long?> Limits, int TrialDays, bool IsPublic, DateTimeOffset? PublishedAt, int Revision, DateTimeOffset CreatedAt,
    IReadOnlyList<CatalogPriceRow> Prices);

public sealed record NewPlanVersion(string PlanCode, string Name, IReadOnlyList<string> Features, IReadOnlyDictionary<string, long?> Limits,
    int TrialDays);

public sealed record NewPriceVersion(string Currency, long MinorUnits, int Exponent, BillingInterval Interval, DateTimeOffset ActiveFrom,
    string? Provider, string? ProviderPriceRef);

/// <summary>Who changed the catalog and why: written to the append-only cp.catalog_event (DEC-062).</summary>
public sealed record CatalogChange(string Actor, string Reason, string? RequestId, DateTimeOffset At);

/// <summary>Control-plane catalog persistence (ti_control). Draft checks and revisions are enforced under a row lock.</summary>
public interface IBillingCatalogStore
{
    Task<IReadOnlyList<CatalogPlanRow>> ListAsync(string? planCode, CancellationToken ct);

    Task<CatalogPlanRow?> GetAsync(Guid id, CancellationToken ct);

    Task<CatalogPlanRow> CreatePlanAsync(NewPlanVersion plan, CatalogChange change, CancellationToken ct);

    /// <summary>Adds a price to a draft; 404 if missing, 412 if the revision moved, 409 if already published.</summary>
    Task<CatalogPriceRow> AddPriceAsync(Guid planVersionId, int expectedRevision, NewPriceVersion price, CatalogChange change, CancellationToken ct);

    /// <summary>Publishes a draft that has at least one price; same failures as <see cref="AddPriceAsync"/>.</summary>
    Task<CatalogPlanRow> PublishAsync(Guid planVersionId, int expectedRevision, CatalogChange change, CancellationToken ct);
}

public sealed record CreatePlanVersionRequest(string PlanCode, string Name, IReadOnlyList<string> Features, IReadOnlyDictionary<string, long?> Limits,
    int TrialDays, string Reason);

public sealed record CreatePriceVersionRequest(MoneyDto Amount, BillingInterval Interval, DateTimeOffset ActiveFrom, string? ProviderPriceRef,
    string Reason);

public sealed record PublishPlanVersionRequest(string Reason);

/// <summary>A plan version with its draft revision, which is the ETag for If-Match on price and publish calls.</summary>
public sealed record PlanVersionView(PlanDto Plan, int Revision);

/// <summary>
/// Platform plan catalog (ENT-001, ENT-002, DEC-062): drafts, immutable prices, publication. Fixture currency only while the
/// simulator is the provider, so nothing here can price a real charge (DEC-040).
/// </summary>
public sealed class CatalogService(IBillingCatalogStore store, ICursorCodec cursors, TimeProvider clock, BillingOptions options)
{
    public async Task<Page<PlanDto>> ListAsync(PlatformCaller caller, PlanVersionStatus? status, string? planCode, PageRequest page, CancellationToken ct)
    {
        caller.RequireCatalogReader();
        page.EnsureSupportedSort();
        var now = clock.GetUtcNow();
        var filterKey = $"plan-versions|{status}|{planCode}";
        var after = page.Cursor is { } c ? cursors.Decode(c, filterKey) : null;
        // Catalog rows are few: filtered in memory and paged by (createdAt, id), newest first.
        var rows = (await store.ListAsync(planCode, ct))
            .Select(r => (r.CreatedAt, Dto: Dto(r, now)))
            .Where(p => status is null || p.Dto.Status == status.ToString())
            .Where(p => after is null || p.CreatedAt < after.CreatedAt || (p.CreatedAt == after.CreatedAt && p.Dto.Id.CompareTo(after.Id) < 0))
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Dto.Id)
            .ToList();
        var limit = page.EffectiveLimit;
        var items = rows.Take(limit).ToList();
        var next = rows.Count > limit ? cursors.Encode(new CursorPosition(items[^1].CreatedAt, items[^1].Dto.Id), filterKey) : null;
        return new Page<PlanDto>(items.Select(p => p.Dto).ToList(), next);
    }

    public async Task<PlanVersionView> CreatePlanAsync(PlatformCaller caller, CreatePlanVersionRequest request, CancellationToken ct)
    {
        caller.RequireBilling();
        var reason = RequireReason(request.Reason);
        var features = request.Features ?? [];
        var limits = request.Limits ?? new Dictionary<string, long?>();
        Rule("planCode", () => CatalogRules.ValidatePlan(request.PlanCode, request.Name, features, limits, request.TrialDays));
        var row = await store.CreatePlanAsync(new NewPlanVersion(request.PlanCode, request.Name.Trim(), features, limits, request.TrialDays),
            Change(caller, reason), ct);
        return new PlanVersionView(Dto(row, clock.GetUtcNow()), row.Revision);
    }

    public async Task<(PriceDto Price, PlanVersionView Plan)> AddPriceAsync(PlatformCaller caller, Guid planVersionId, int ifMatch,
        CreatePriceVersionRequest request, CancellationToken ct)
    {
        caller.RequireBilling();
        var reason = RequireReason(request.Reason);
        if (request.Amount is null)
            throw AppException.Validation("amount", "An amount is required.");
        var amount = Rule("amount", () => Money.Of(request.Amount.Currency, request.Amount.MinorUnits, request.Amount.Exponent));
        Rule("amount", () => CatalogRules.ValidatePrice(amount, options.SimulatorOnly));
        if (request.ProviderPriceRef is { } reference && (reference.Length is < 1 or > 200 || string.IsNullOrWhiteSpace(reference)))
            throw AppException.Validation("providerPriceRef", "A provider price reference is 1 to 200 characters.");
        var price = await store.AddPriceAsync(planVersionId, ifMatch,
            new NewPriceVersion(amount.Currency, amount.MinorUnits, amount.Exponent, request.Interval, request.ActiveFrom,
                request.ProviderPriceRef is null ? null : options.Provider, request.ProviderPriceRef),
            Change(caller, reason), ct);
        var plan = await store.GetAsync(planVersionId, ct) ?? throw AppException.NotFound("Plan version");
        return (PriceDto(price), new PlanVersionView(Dto(plan, clock.GetUtcNow()), plan.Revision));
    }

    public async Task<PlanVersionView> PublishAsync(PlatformCaller caller, Guid planVersionId, int ifMatch, PublishPlanVersionRequest request,
        CancellationToken ct)
    {
        caller.RequireBilling();
        var reason = RequireReason(request.Reason);
        var row = await store.PublishAsync(planVersionId, ifMatch, Change(caller, reason), ct);
        return new PlanVersionView(Dto(row, clock.GetUtcNow()), row.Revision);
    }

    public static PlanDto Dto(CatalogPlanRow r, DateTimeOffset now) =>
        new(r.Id, r.PlanCode, r.Version, r.Name, CatalogRules.Status(r.PublishedAt, r.Prices.Select(p => (p.ActiveFrom, p.ActiveTo)), now).ToString(),
            r.Features, r.Limits, r.TrialDays, r.PublishedAt, r.Prices.Select(PriceDto).ToList(), r.Revision);

    public static PriceDto PriceDto(CatalogPriceRow p) =>
        new(p.Id, p.PlanVersionId, new MoneyDto(p.Currency, p.MinorUnits, p.Exponent), p.Interval.ToString(), p.ActiveFrom, p.ActiveTo,
            p.DisplayApproved);

    private CatalogChange Change(PlatformCaller caller, string reason) => new(caller.Subject, reason, caller.RequestId, clock.GetUtcNow());

    private static string RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000
            ? throw AppException.Validation("reason", "A reason of 1 to 1000 characters is required.")
            : reason.Trim();

    private static void Rule(string field, Action check) => Rule(field, () => { check(); return 0; });

    private static T Rule<T>(string field, Func<T> check)
    {
        try
        {
            return check();
        }
        catch (DomainException ex)
        {
            throw AppException.Validation(field, ex.Message);
        }
    }
}
