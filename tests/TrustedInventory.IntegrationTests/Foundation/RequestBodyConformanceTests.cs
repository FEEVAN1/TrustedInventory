using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.IntegrationTests.Support;
using YamlDotNet.RepresentationModel;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>
/// The request body each endpoint binds matches its contract schema (API-16, AGT-002, DEC-003): every property the server reads is
/// in the schema, and every property the schema requires is one the server reads. Together with the route, method and permission checks
/// this keeps the contract and the implementation from drifting apart.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RequestBodyConformanceTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "API-16")]
    [Trait("Requirement", "AGT-002")]
    public void Every_bound_request_body_matches_its_contract_schema()
    {
        var (bodies, schemas) = LoadContract();
        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var endpoint in fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            var bound = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
            if (name is null || bound is null || !bodies.TryGetValue(name, out var schemaName))
                continue;
            if (!schemas.TryGetValue(schemaName, out var schema))
            {
                failures.Add($"{name}: schema {schemaName} not found");
                continue;
            }
            checkedCount++;
            var properties = JsonProperties(bound);
            foreach (var extra in properties.Except(schema.Properties, StringComparer.Ordinal))
                failures.Add($"{name}: {bound.Name}.{extra} is not in {schemaName}");
            foreach (var missing in schema.Required.Except(properties, StringComparer.Ordinal))
                failures.Add($"{name}: required {schemaName}.{missing} is not read by {bound.Name}");
        }
        Assert.True(checkedCount > 80, $"only {checkedCount} bodies were compared; the endpoint metadata changed");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The JSON names the server binds: public settable or constructor properties, camelCase unless renamed.</summary>
    private static HashSet<string> JsonProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.GetIndexParameters().Length == 0)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    private sealed record Schema(HashSet<string> Properties, HashSet<string> Required);

    private static (Dictionary<string, string> Bodies, Dictionary<string, Schema> Schemas) LoadContract()
    {
        var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("TI_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "openapi.yaml")))
            dir = dir.Parent;
        using var reader = new StreamReader(Path.Combine(dir!.FullName, "docs", "openapi.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var rootNode = (YamlMappingNode)yaml.Documents[0].RootNode;
        static YamlNode? Child(YamlNode node, string key) =>
            node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, item) in ((YamlMappingNode)Child(rootNode, "paths")!).Children)
            foreach (var (_, op) in ((YamlMappingNode)item).Children)
            {
                if (Child(op, "operationId") is not YamlScalarNode id || Child(Child(op, "requestBody") ?? new YamlMappingNode(), "content") is not YamlMappingNode content)
                    continue;
                foreach (var (_, media) in content.Children)
                    if (Child(Child(media, "schema") ?? new YamlMappingNode(), "$ref") is YamlScalarNode reference)
                        bodies[id.Value!] = reference.Value!.Split('/')[^1];
            }

        var schemas = new Dictionary<string, Schema>(StringComparer.Ordinal);
        foreach (var (key, node) in ((YamlMappingNode)Child(Child(rootNode, "components")!, "schemas")!).Children)
        {
            var properties = Child(node, "properties") is YamlMappingNode p ? p.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToHashSet(StringComparer.Ordinal) : [];
            var required = Child(node, "required") is YamlSequenceNode r ? r.Children.Select(k => ((YamlScalarNode)k).Value!).ToHashSet(StringComparer.Ordinal) : [];
            schemas[((YamlScalarNode)key).Value!] = new Schema(properties, required);
        }
        return (bodies, schemas);
    }
}
