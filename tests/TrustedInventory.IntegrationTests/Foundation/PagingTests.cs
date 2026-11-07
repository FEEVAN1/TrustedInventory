using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Cursor pagination (API-004): stable order, bounded limits, tamper-evident cursors bound to the filter.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PagingTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "API-004")]
    public async Task Cursor_walks_every_item_once_in_creation_order_and_rejects_tampering()
    {
        var admin = fx.Client(fx.Seed.A.Admin, fx.Seed.A.TenantId);
        var wh = await (await admin.PostJsonAsync("/api/v1/warehouses", new
        {
            code = "PG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), name = "Paging", timeZone = "UTC",
        })).ExpectAsync(HttpStatusCode.Created);
        var zone = await (await admin.PostJsonAsync("/api/v1/zones", new { warehouseId = wh["id"], code = "Z", name = "Zone" }))
            .ExpectAsync(HttpStatusCode.Created);
        var created = new List<string>();
        for (var i = 0; i < 5; i++)
            created.Add((string)(await (await admin.PostJsonAsync("/api/v1/bins", new { zoneId = zone["id"], code = $"B{i}", kind = "Storage" }))
                .ExpectAsync(HttpStatusCode.Created))["id"]!);

        var seen = new List<string>();
        string? cursor = null;
        string? firstCursor = null;
        do
        {
            var url = $"/api/v1/bins?zoneId={zone["id"]}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = await (await admin.GetAsync(url)).ExpectAsync(HttpStatusCode.OK);
            seen.AddRange(page["items"]!.AsArray().Select(b => (string)b!["id"]!));
            cursor = (string?)page["nextCursor"];
            firstCursor ??= cursor;
        }
        while (cursor is not null);

        created.Reverse(); // default sort is -createdAt
        Assert.Equal(created, seen);

        await (await admin.GetAsync($"/api/v1/bins?zoneId={zone["id"]}&limit=2&cursor={firstCursor![..^2]}AA"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");
        // A cursor issued for one filter cannot be replayed against another.
        await (await admin.GetAsync($"/api/v1/bins?warehouseId={wh["id"]}&limit=2&cursor={firstCursor}"))
            .ExpectProblemAsync(HttpStatusCode.BadRequest, "INVALID_CURSOR");
        await (await admin.GetAsync("/api/v1/bins?limit=201")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await admin.GetAsync("/api/v1/bins?sort=code")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }
}
