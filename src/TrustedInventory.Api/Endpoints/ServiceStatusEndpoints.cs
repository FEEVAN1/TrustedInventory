using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TrustedInventory.Api.Endpoints;

public sealed record ServiceComponentStatus(string Name, string Status);

public sealed record SupportContact(string Contact, string? StatusPageUrl);

public sealed record ServiceStatus(string Status, DateTimeOffset CheckedAt, IReadOnlyList<ServiceComponentStatus> Components, SupportContact Support);

/// <summary>
/// Public service status and support contact (SOP-006, DEC-074). No token, no tenant, nothing about any customer: the overall state,
/// the state of each internal component and where to get help. The approved customer SLA remains a commercial decision.
/// </summary>
public static class ServiceStatusEndpoints
{
    public static RouteGroupBuilder MapServiceStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/service-status", async (HealthCheckService health, IConfiguration config, TimeProvider clock, CancellationToken ct) =>
            {
                var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"), ct);
                var components = new List<ServiceComponentStatus> { new("api", "Operational") };
                components.AddRange(report.Entries.Select(e => new ServiceComponentStatus(e.Key, e.Value.Status switch
                {
                    HealthStatus.Healthy => "Operational",
                    HealthStatus.Degraded => "Degraded",
                    _ => "Unavailable",
                })));
                var overall = report.Status == HealthStatus.Healthy ? "Operational" : report.Status == HealthStatus.Degraded ? "Degraded" : "Unavailable";
                return Results.Ok(new ServiceStatus(overall, clock.GetUtcNow(), components,
                    new SupportContact(config["Support:Contact"] is { Length: > 0 } contact ? contact : "Not configured",
                        config["Support:StatusPageUrl"] is { Length: > 0 } url ? url : null)));
            })
            .WithName("getServiceStatus")
            .AllowAnonymous();
        return api;
    }
}
