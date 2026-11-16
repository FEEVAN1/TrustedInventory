using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Api.Security;
using TrustedInventory.IntegrationTests.Support;
using YamlDotNet.RepresentationModel;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>
/// Every implemented endpoint matches docs/openapi.yaml (operationId, method, path, x-permission), and every
/// tenant endpoint denies a member who lacks the permission (IAM-001 authorization sweep).
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed partial class ContractConformanceTests(ApiFixture fx)
{
    /// <param name="Public">The operation declares <c>security: []</c>.</param>
    private sealed record Operation(string Id, string Method, string Path, string Permission, string Access, bool Public);

    /// <param name="Path">Contract-relative path (no /api/v1 prefix).</param>
    /// <param name="Url">Path as served, e.g. /api/v1/... or /platform/v1/... (DEC-037).</param>
    private sealed record Implemented(string Name, string Method, string Path, string Url, string? Permission, bool TenantScoped);

    [Fact]
    [Trait("Requirement", "API-16")]
    [Trait("Requirement", "API-17")]
    [Trait("Requirement", "AGT-002")]
    public void Implemented_endpoints_match_the_contract()
    {
        var contract = LoadContract().ToDictionary(o => o.Id);
        var endpoints = ImplementedEndpoints();
        Assert.NotEmpty(endpoints);

        var mismatches = new List<string>();
        foreach (var e in endpoints)
        {
            if (!contract.TryGetValue(e.Name, out var op))
            {
                mismatches.Add($"{e.Name}: not in contract");
                continue;
            }
            if (op.Method != e.Method || op.Path != e.Path)
                mismatches.Add($"{e.Name}: implemented {e.Method} {e.Path}, contract {op.Method} {op.Path}");
            if (e.TenantScoped && op.Permission != e.Permission)
                mismatches.Add($"{e.Name}: permission {e.Permission}, contract {op.Permission}");
        }
        Assert.Empty(mismatches);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public async Task Every_tenant_endpoint_denies_a_member_without_the_permission()
    {
        var contract = LoadContract().ToDictionary(o => o.Id);
        var noRoles = fx.Client(fx.Seed.A.NoRoles, fx.Seed.A.TenantId);
        var failures = new List<string>();
        foreach (var e in ImplementedEndpoints().Where(e => e.TenantScoped))
        {
            // x-permission: member admits any active member (DEC-041); non-members are covered by the cross-tenant sweep.
            if (contract.TryGetValue(e.Name, out var op) && op.Permission == "member")
                continue;
            var response = await noRoles.SendAsync(Request(e));
            if (response.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"{e.Name}: {(int)response.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    [Trait("Requirement", "AT-TEN-01")]
    [Trait("Requirement", "API-001")]
    public async Task Every_tenant_endpoint_rejects_another_tenants_header_and_a_missing_header()
    {
        var foreign = fx.Client(fx.Seed.A.Admin, fx.Seed.B.TenantId);
        var headerless = fx.Client(fx.Seed.A.Admin, tenantId: null);
        var failures = new List<string>();
        foreach (var e in ImplementedEndpoints().Where(e => e.TenantScoped))
        {
            var cross = await foreign.SendAsync(Request(e));
            if (cross.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"{e.Name} cross-tenant: {(int)cross.StatusCode}");
            var missing = await headerless.SendAsync(Request(e));
            if (missing.StatusCode != HttpStatusCode.BadRequest)
                failures.Add($"{e.Name} no header: {(int)missing.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    [Trait("Requirement", "IAM-002")]
    public async Task Every_api_endpoint_requires_authentication()
    {
        var contract = LoadContract().ToDictionary(o => o.Id);
        var anonymous = fx.Anonymous();
        var failures = new List<string>();
        foreach (var e in ImplementedEndpoints())
        {
            // Only operations the contract declares public on both counts (security: [] and x-access: none, DEC-041).
            if (contract.TryGetValue(e.Name, out var op) && op.Public && op.Access == "none")
                continue;
            var response = await anonymous.SendAsync(Request(e));
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                failures.Add($"{e.Name}: {(int)response.StatusCode}");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static HttpRequestMessage Request(Implemented e)
    {
        var url = RouteParameter().Replace(e.Url, Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(new HttpMethod(e.Method), url);
        if (e.Method is "POST" or "PATCH")
        {
            // Deliberately invalid body: authorization must be decided before the body is even read.
            request.Content = new StringContent("{\"unexpected\":true}", System.Text.Encoding.UTF8,
                e.Method == "PATCH" ? "application/merge-patch+json" : "application/json");
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        }
        return request;
    }

    private List<Implemented> ImplementedEndpoints() =>
        fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>() is not null)
            .Select(e => new Implemented(
                e.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName,
                e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single(),
                NormalisePath(e.RoutePattern.RawText!),
                ServedPath(e.RoutePattern.RawText!),
                e.Metadata.GetMetadata<PermissionMetadata>()?.Permission,
                e.Metadata.GetMetadata<TenantScopedMetadata>() is not null))
            .ToList();

    private static string ServedPath(string raw)
    {
        var path = RouteConstraint().Replace(raw, "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string NormalisePath(string raw)
    {
        var path = raw.StartsWith("/api/v1", StringComparison.Ordinal) ? raw["/api/v1".Length..] : raw;
        path = RouteConstraint().Replace(path, "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static List<Operation> LoadContract()
    {
        // TI_REPO_ROOT lets builds with a separate --artifacts-path still find docs/openapi.yaml.
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("TI_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "openapi.yaml")))
            dir = dir.Parent;
        using var reader = new StreamReader(Path.Combine(dir!.FullName, "docs", "openapi.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var paths = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("paths")];

        var operations = new List<Operation>();
        foreach (var (pathNode, item) in paths.Children)
        {
            foreach (var (methodNode, opNode) in ((YamlMappingNode)item).Children)
            {
                var method = ((YamlScalarNode)methodNode).Value!;
                if (method is not ("get" or "post" or "patch" or "put" or "delete") || opNode is not YamlMappingNode op)
                    continue;
                string Field(string name) => op.Children.TryGetValue(new YamlScalarNode(name), out var v) ? ((YamlScalarNode)v).Value! : "";
                var isPublic = op.Children.TryGetValue(new YamlScalarNode("security"), out var security)
                    && security is YamlSequenceNode { Children.Count: 0 };
                operations.Add(new Operation(Field("operationId"), method.ToUpperInvariant(), ((YamlScalarNode)pathNode).Value!,
                    Field("x-permission"), Field("x-access"), isPublic));
            }
        }
        return operations;
    }

    [GeneratedRegex(@"\{(\w+)(:[^}]*)?\}")]
    private static partial Regex RouteConstraint();

    [GeneratedRegex(@"\{\w+\}")]
    private static partial Regex RouteParameter();
}
