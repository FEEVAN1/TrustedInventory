using System.Diagnostics;
using System.Net;
using TrustedInventory.IntegrationTests.Medical;
using TrustedInventory.IntegrationTests.Support;
using Xunit.Abstractions;

namespace TrustedInventory.IntegrationTests.Foundation;

/// <summary>Runs only when TI_RUN_PERF=1; otherwise reported as skipped, never as passing evidence.</summary>
public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TI_RUN_PERF") != "1")
            Skip = "Performance benchmark: set TI_RUN_PERF=1 to run (NFR-PERF). Staging results are the release evidence.";
    }
}

/// <summary>
/// Section 18 workload benchmark (NFR-PERF, SOP-002, DEC-081): one million stock movements, 50 concurrent virtual users for a fixed
/// period (80 % stock queries, 20 % stock commands), p95 per category against the proposed budgets (queries 1 s, commands 2 s), and
/// allocation over 100 candidate lots measured separately. This runs in-process against the test container on a workstation; the
/// release evidence is the same harness against staging.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class PerformanceTests(ApiFixture fx, ITestOutputHelper output)
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("TI_PERF_SECONDS"), out var s) ? s : 60);

    [PerformanceFact]
    [Trait("Requirement", "NFR-PERF")]
    [Trait("Requirement", "SOP-002")]
    public async Task Fifty_users_on_a_million_movements_stay_within_the_p95_budgets()
    {
        var w = await MedicalWorld.CreateAsync(fx, "perf");
        // Bin transfers need putaway.confirm (Receiver).
        await w.ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            SELECT tenant_id, id, user_subject, 'Receiver', 'perf', 'seed', 'seed' FROM ti.membership WHERE tenant_id = @t AND user_subject = @s
            """, ("t", w.Tenant), ("s", w.OperatorSubject));
        var products = new List<string>();
        for (var i = 0; i < 20; i++)
            products.Add((string)(await (await w.Operator.PostJsonAsync("/api/v1/products", new
            {
                sku = $"PERF-{i:00}", name = $"Perf {i}", handlingProfile = "General", trackingMode = "Quantity",
                baseUomId = (string)(await w.GetAsync("/api/v1/units"))["items"]![0]!["id"]!, basePrecision = 0, rotationRule = "FIFO",
            })).ExpectAsync(HttpStatusCode.Created))["id"]!);
        var buckets = new List<Guid>();
        foreach (var p in products)
            buckets.Add(await w.BucketAsync(p, w.A01, 1_000_000));

        // One million movements spread over the products, as the superuser in one statement (synthetic history).
        var seed = Stopwatch.StartNew();
        await using (var conn = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(fx.SuperuserConnection) { CommandTimeout = 1200 }.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("""
                WITH b AS (SELECT array_agg(id ORDER BY id) AS ids, array_agg(product_id ORDER BY id) AS products
                           FROM ti.stock_bucket WHERE tenant_id = @t AND on_hand_qty = 1000000)
                INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty, document_type,
                                               document_id, actor_subject, occurred_at)
                SELECT @t, 'Receipt', 'perf:' || g, b.products[1 + g % 20], b.ids[1 + g % 20], 1, 'Receipt', gen_random_uuid(), 'perf-seed',
                       now() - (g || ' seconds')::interval
                FROM generate_series(1, 1000000) g, b
                """, conn);
            cmd.Parameters.AddWithValue("t", w.Tenant);
            await cmd.ExecuteNonQueryAsync();
            await using var analyze = new Npgsql.NpgsqlCommand("ANALYZE ti.stock_movement", conn);
            await analyze.ExecuteNonQueryAsync();
        }
        output.WriteLine($"seeded 1,000,000 movements in {seed.Elapsed.TotalSeconds:0} s");

        var queries = new System.Collections.Concurrent.ConcurrentBag<double>();
        var commands = new System.Collections.Concurrent.ConcurrentBag<double>();
        var errors = 0;
        var statuses = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var until = DateTimeOffset.UtcNow + Duration;
        var users = Enumerable.Range(0, 50).Select(async user =>
        {
            var random = new Random(user);
            var bucket = buckets[user % buckets.Count];
            var toBin = user % 2 == 0 ? w.C01 : w.C02;
            while (DateTimeOffset.UtcNow < until)
            {
                var product = products[random.Next(products.Count)];
                var sw = Stopwatch.StartNew();
                HttpResponseMessage response;
                var isCommand = random.Next(100) < 20;
                if (!isCommand)
                    response = random.Next(2) == 0
                        ? await w.Operator.GetAsync($"/api/v1/stock?productId={product}&limit=50")
                        : await w.Operator.GetAsync($"/api/v1/stock/movements?productId={product}&limit=50");
                else
                    response = await w.PostAsync("/api/v1/inventory/bin-transfers", Guid.NewGuid(), null,
                        new { sourceBucketId = bucket, targetBinId = toBin, quantity = "1", reason = "perf" });
                sw.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref errors);
                    statuses.AddOrUpdate((int)response.StatusCode, 1, (_, n) => n + 1);
                }
                (isCommand ? commands : queries).Add(sw.Elapsed.TotalMilliseconds);
            }
        });
        await Task.WhenAll(users);

        // Allocation over 100 candidate lots for one line (section 18).
        var lotted = (string)(await (await w.Operator.PostJsonAsync("/api/v1/products", new
        {
            sku = "PERF-LOTS", name = "Lots", handlingProfile = "General", trackingMode = "Lot",
            baseUomId = (string)(await w.GetAsync("/api/v1/units"))["items"]![0]!["id"]!, basePrecision = 0, rotationRule = "FEFO",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        for (var i = 0; i < 100; i++)
            await w.BucketAsync(lotted, w.A01, 1, lot: await w.LotAsync(lotted, $"L{i:000}", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30 + i)));
        var order = await w.OrderAsync(lotted, "100");
        var alloc = Stopwatch.StartNew();
        await (await w.AllocateAsync(order)).ExpectAsync(HttpStatusCode.OK);
        alloc.Stop();

        static double P95(IEnumerable<double> xs) { var a = xs.Order().ToArray(); return a.Length == 0 ? 0 : a[(int)Math.Ceiling(a.Length * 0.95) - 1]; }
        var qp95 = P95(queries);
        var cp95 = P95(commands);
        output.WriteLine($"queries n={queries.Count} p95={qp95:0} ms; commands n={commands.Count} p95={cp95:0} ms; errors={errors}; "
            + $"allocation over 100 lots {alloc.Elapsed.TotalMilliseconds:0} ms; error statuses {string.Join(", ", statuses.Select(x => $"{x.Key}x{x.Value}"))}");
        Assert.Equal(0, errors);
        Assert.True(qp95 <= 1000, $"query p95 {qp95:0} ms exceeds 1 s");
        Assert.True(cp95 <= 2000, $"command p95 {cp95:0} ms exceeds 2 s");
    }
}
