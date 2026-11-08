using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using TrustedInventory.Application.Common;
using TrustedInventory.Application.Persistence;
using TrustedInventory.Application.Security;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.Infrastructure.Security;

namespace TrustedInventory.Infrastructure;

/// <summary>Schema version this build requires; a test keeps it equal to the newest file in db/migrations.</summary>
public static class SchemaVersion
{
    public const string Required = "0004_saas_control_plane";
}

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence and security adapters. Missing security settings stop startup (fail closed, section 18).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = Required(configuration, "ConnectionStrings:App");
        var platformConnection = Required(configuration, "ConnectionStrings:Platform");
        var controlConnection = Required(configuration, "ConnectionStrings:Control");
        var cursorKey = Convert.FromBase64String(Required(configuration, "Paging:CursorKey"));

        services.AddSingleton(_ => new AppDataSource(BuildDataSource(appConnection)));
        services.AddSingleton(_ => new PlatformDataSource(BuildDataSource(platformConnection)));
        services.AddSingleton(_ => new ControlDataSource(BuildDataSource(controlConnection)));
        services.AddSingleton<Application.Organization.IPlatformDirectory, Organization.PlatformDirectory>();
        services.AddSingleton<Application.Organization.IWorkspaceStatusReader, Organization.WorkspaceStatusReader>();
        services.AddSingleton<ICursorCodec>(new HmacCursorCodec(cursorKey));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<ITenantUnitOfWork, TenantUnitOfWork>();
        services.AddSingleton<Application.Saas.ITenantAccessReader, Saas.TenantAccessReader>();
        services.AddSingleton<Application.Saas.IQuotaGate, Saas.QuotaGate>();
        services.AddScoped<Application.Organization.IPlatformUnitOfWork, Organization.PlatformUnitOfWork>();
        services.AddSingleton<Application.Audit.IAuditSupport, Audit.AuditSupport>();
        Saas.ProvisioningRegistration.AddProvisioning(services, configuration);

        services.AddHealthChecks().AddCheck<DatabaseReadyCheck>("database", tags: ["ready"]);
        return services;
    }

    public static NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        PgEnums.Map(builder);
        return builder.Build();
    }

    public static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");
}

/// <summary>Readiness (ARC-004): the database answers and carries the schema version this build expects.</summary>
internal sealed class DatabaseReadyCheck(AppDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.Source.CreateCommand("SELECT EXISTS (SELECT 1 FROM ti.schema_migration WHERE id = @id)");
            cmd.Parameters.AddWithValue("id", SchemaVersion.Required);
            return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Schema {SchemaVersion.Required} is not applied.");
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.");
        }
    }
}
