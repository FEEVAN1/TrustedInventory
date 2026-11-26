using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Domain.Security;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;
using YamlDotNet.RepresentationModel;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>
/// Responses match their contract schemas (API-16, AGT-002, DEC-003). Statically, every endpoint whose response type the server
/// declares is compared with its 200/201 schema (paged lists by their item schema). Live, every parameterless tenant GET is called by a
/// member holding every tenant role, and each JSON object it returns is compared with its schema: every key is in the schema and every
/// required key is present.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ResponseConformanceTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "API-16")]
    [Trait("Requirement", "AGT-002")]
    public void Declared_response_types_match_their_contract_schemas()
    {
        var contract = Contract.Load();
        var failures = new List<string>();
        var compared = 0;
        foreach (var endpoint in Endpoints())
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName;
            foreach (var produced in endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>())
            {
                if (produced.StatusCode is not (200 or 201) || produced.Type is null || produced.Type == typeof(void)
                    || typeof(IResult).IsAssignableFrom(produced.Type) || produced.Type == typeof(string)
                    || !contract.Responses.TryGetValue((name, produced.StatusCode), out var schemaName))
                    continue;
                var (type, schema) = Unwrap(produced.Type, schemaName, contract);
                if (schema is null)
                    continue;
                compared++;
                var properties = JsonProperties(type);
                foreach (var extra in properties.Except(schema.Properties, StringComparer.Ordinal))
                    failures.Add($"{name}: {type.Name}.{extra} is not in {schema.Name}");
                foreach (var missing in schema.Required.Except(properties, StringComparer.Ordinal))
                    failures.Add($"{name}: required {schema.Name}.{missing} is not in {type.Name}");
            }
        }
        Assert.True(compared > 30, $"only {compared} response types were compared; the endpoint metadata changed");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Trait("Requirement", "API-16")]
    [Trait("Requirement", "AGT-002")]
    public async Task Live_list_and_read_responses_carry_exactly_the_contract_fields()
    {
        var contract = Contract.Load();
        var tenant = await TenantProvisioning.SignUpAsync(fx, "response-shape");
        var owner = fx.Client(tenant.Admin, tenant.TenantId);
        // Some records so lists are not empty.
        var unit = (string)(await (await owner.PostJsonAsync("/api/v1/units", new { code = "EA", name = "Each" })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = (string)(await (await owner.PostJsonAsync("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await owner.PostJsonAsync("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" })).ExpectAsync(HttpStatusCode.Created);
        await (await owner.PostJsonAsync("/api/v1/products", new { sku = "P1", name = "Product", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 }))
            .ExpectAsync(HttpStatusCode.Created);
        await (await owner.PostJsonAsync("/api/v1/partners", new { code = "S1", name = "Supplier", type = "Supplier" })).ExpectAsync(HttpStatusCode.Created);
        await (await owner.PostJsonAsync("/api/v1/saas/sample-data", new { timeZone = "UTC" })).ExpectAsync(HttpStatusCode.Created);

        // A member with every tenant role, so permission never hides a response.
        var everything = $"user-every-role-{Guid.NewGuid():N}";
        var membership = (Guid)(await TenantProvisioning.ScalarAsSuperuserAsync(fx,
            "INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id", ("t", tenant.TenantId), ("s", everything)))!;
        foreach (var role in Enum.GetValues<RoleName>().Where(r => r != RoleName.TenantOwner && !r.ToString().StartsWith("Platform", StringComparison.Ordinal)))
            await TenantProvisioning.ExecAsSuperuserAsync(fx, """
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
                VALUES (@t, @m, @s, @r::ti.role_name, 'test seed', 'seed', 'seed')
                """, ("t", tenant.TenantId), ("m", membership), ("s", everything), ("r", role.ToString()));
        var client = fx.Client(everything, tenant.TenantId);

        var failures = new List<string>();
        var compared = 0;
        foreach (var endpoint in Endpoints())
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName;
            var raw = endpoint.RoutePattern.RawText!;
            if (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Single() != "GET" || !raw.StartsWith("/api/v1/", StringComparison.Ordinal)
                || raw.Contains('{', StringComparison.Ordinal) || !contract.Responses.TryGetValue((name, 200), out var schemaName))
                continue;
            var response = await client.GetAsync(raw);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
                continue; // 404 (nothing yet) or a non-JSON body (metrics, documents)
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            compared++;
            Check(name, body, schemaName, contract, failures);
        }
        Assert.True(compared > 40, $"only {compared} live responses were compared");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void Check(string operation, JsonNode? node, string schemaName, Contract contract, List<string> failures)
    {
        if (node is not JsonObject obj || !contract.Schemas.TryGetValue(schemaName, out var schema) || schema.Properties.Count == 0)
            return;
        var keys = obj.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in keys.Except(schema.Properties, StringComparer.Ordinal))
            failures.Add($"{operation}: response field {schemaName}.{extra} is not in the contract");
        foreach (var missing in schema.Required.Except(keys, StringComparer.Ordinal))
            failures.Add($"{operation}: required {schemaName}.{missing} is missing from the response");
        if (schema.ItemsRef is { } itemSchema && obj["items"] is JsonArray items)
            foreach (var item in items.Take(5))
                Check(operation, item, itemSchema, contract, failures);
    }

    private static (Type Type, Contract.Schema? Schema) Unwrap(Type type, string schemaName, Contract contract)
    {
        contract.Schemas.TryGetValue(schemaName, out var schema);
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("Page", StringComparison.Ordinal) && schema?.ItemsRef is { } item)
            return (type.GetGenericArguments()[0], contract.Schemas.GetValueOrDefault(item));
        return (type, schema);
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>() is not null);

    private static HashSet<string> JsonProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.GetIndexParameters().Length == 0)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    private sealed class Contract
    {
        public sealed record Schema(string Name, HashSet<string> Properties, HashSet<string> Required, string? ItemsRef);

        public Dictionary<(string Operation, int Status), string> Responses { get; } = [];
        public Dictionary<string, Schema> Schemas { get; } = new(StringComparer.Ordinal);

        public static Contract Load()
        {
            var dir = new DirectoryInfo(Environment.GetEnvironmentVariable("TI_REPO_ROOT") is { Length: > 0 } root ? root : AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "openapi.yaml")))
                dir = dir.Parent;
            using var reader = new StreamReader(Path.Combine(dir!.FullName, "docs", "openapi.yaml"));
            var yaml = new YamlStream();
            yaml.Load(reader);
            var rootNode = (YamlMappingNode)yaml.Documents[0].RootNode;
            static YamlNode? Child(YamlNode? node, string key) =>
                node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
            static string? Ref(YamlNode? node) => Child(node, "$ref") is YamlScalarNode r ? r.Value!.Split('/')[^1] : null;

            var contract = new Contract();
            foreach (var (_, item) in ((YamlMappingNode)Child(rootNode, "paths")!).Children)
                foreach (var (_, op) in ((YamlMappingNode)item).Children)
                {
                    if (Child(op, "operationId") is not YamlScalarNode id || Child(op, "responses") is not YamlMappingNode responses)
                        continue;
                    foreach (var (code, response) in responses.Children)
                        if (int.TryParse(((YamlScalarNode)code).Value, out var status) && status is 200 or 201
                            && Ref(Child(Child(Child(response, "content"), "application/json"), "schema")) is { } schemaName)
                            contract.Responses[(id.Value!, status)] = schemaName;
                }
            foreach (var (key, node) in ((YamlMappingNode)Child(Child(rootNode, "components"), "schemas")!).Children)
            {
                var name = ((YamlScalarNode)key).Value!;
                var properties = Child(node, "properties") is YamlMappingNode p ? p.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToHashSet(StringComparer.Ordinal) : [];
                var required = Child(node, "required") is YamlSequenceNode r ? r.Children.Select(k => ((YamlScalarNode)k).Value!).ToHashSet(StringComparer.Ordinal) : [];
                var itemsRef = Ref(Child(Child(Child(node, "properties"), "items"), "items"));
                contract.Schemas[name] = new Schema(name, properties, required, itemsRef);
            }
            return contract;
        }
    }
}
