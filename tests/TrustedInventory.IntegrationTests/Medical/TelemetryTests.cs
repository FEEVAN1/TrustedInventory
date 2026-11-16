using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Medical;

/// <summary>Devices, readings, excursions, stale telemetry and late readings (TEL-001 to TEL-005, AT-TEL-01, AT-TEL-02, DEC-077).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class TelemetryTests(ApiFixture fx)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    [Trait("Requirement", "AT-TEL-01")]
    [Trait("Requirement", "TEL-001")]
    [Trait("Requirement", "TEL-003")]
    [Trait("Requirement", "TEL-005")]
    public async Task An_abnormal_reading_holds_once_duplicates_do_nothing_and_normal_readings_never_release()
    {
        var w = await MedicalWorld.CreateAsync(fx, "tel-hold");
        var (device, token) = await DeviceAsync(w, w.ColdZone, "SENSOR-1");
        var lot = await w.LotAsync(w.Medical, "L-T1", Today.AddDays(120));
        var c01 = await w.BucketAsync(w.Medical, w.C01, 10, lot: lot);
        var c02 = await w.BucketAsync(w.Medical, w.C02, 4, "Quarantine", lot);
        var general = await w.BucketAsync(w.General, w.C01, 3);   // no storage profile: not affected

        Assert.Equal("Stored", (string?)(await (await Ingest(token, device, "e-1", 5.0m)).ExpectAsync(HttpStatusCode.OK))["processingStatus"]);
        var abnormal = await (await Ingest(token, device, "e-2", 12.4m)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("ExcursionOpened", (string?)abnormal["processingStatus"]);
        Assert.Equal(["0|0|Available", "0|0|Quarantine", "3|0|Available"], new[] { await w.StateAsync(c01), await w.StateAsync(c02), await w.StateAsync(general) });
        Assert.Equal("10|0|Hold", await w.StateAsync(await w.SiblingAsync(c01, "Hold")));
        Assert.Equal("4|0|Hold", await w.StateAsync(await w.SiblingAsync(c02, "Hold")));

        // A duplicate eventId is a no-op; a second abnormal reading extends the same excursion and alert, holding nothing twice.
        var duplicate = await (await Ingest(token, device, "e-2", 12.4m)).ExpectAsync(HttpStatusCode.OK);
        Assert.True((bool)duplicate["duplicate"]!);
        Assert.Equal((string?)abnormal["readingId"], (string?)duplicate["readingId"]);
        await (await Ingest(token, device, "e-3", 13.1m)).ExpectAsync(HttpStatusCode.OK);
        var excursions = (await w.GetAsync("/api/v1/excursions"))["items"]!.AsArray();
        var excursion = Assert.Single(excursions)!;
        Assert.Equal([12.4m, 13.1m], new[] { (decimal)excursion["firstAbnormalCelsius"]!, (decimal)excursion["lastAbnormalCelsius"]! });
        Assert.Equal(2, excursion["affectedBucketIds"]!.AsArray().Count);
        var alert = Assert.Single((await w.GetAsync("/api/v1/alerts?type=Excursion"))["items"]!.AsArray())!;
        Assert.Equal(w.ColdZone, (string?)alert["zoneId"]);
        Assert.Equal(13.1m, (decimal)alert["lastAbnormalCelsius"]!);
        Assert.Contains(lot.ToString(), alert["lotIds"]!.AsArray().Select(l => (string)l!));
        Assert.Equal(2L, await w.ScalarAsync<long>(
            "SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND document_type = 'Excursion'", ("t", w.Tenant)));
        Assert.True(await w.ScalarAsync<long>("SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t AND event_type = 'AlertRaised'", ("t", w.Tenant)) == 1);

        // Back in range: the excursion records its end, but nothing is released or resolved (section 13).
        await (await Ingest(token, device, "e-4", 4.0m)).ExpectAsync(HttpStatusCode.OK);
        var ended = await w.GetAsync($"/api/v1/excursions/{excursion["id"]}");
        Assert.Equal("Open", (string?)ended["status"]);
        Assert.NotNull(ended["endedAt"]);
        Assert.Equal("10|0|Hold", await w.StateAsync(await w.SiblingAsync(c01, "Hold")));

        // QA resolves it with an independent signature; release returns the stock.
        var acknowledged = await (await w.Operator.PostJsonAsync($"/api/v1/excursions/{excursion["id"]}/acknowledge", new { note = "On it" },
            $"\"{ended["version"]}\"")).ExpectAsync(HttpStatusCode.OK);
        var approval = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "ExcursionDisposition", subjectId = excursion["id"]!.ToString(), subjectVersion = (int)acknowledged["version"]!, action = "Dispose",
            reason = "Stability data supports release",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.SignerSubject), meaning = "Disposition", reason = "Reviewed stability data" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var resolved = await (await w.PostAsync($"/api/v1/excursions/{excursion["id"]}/resolve", Guid.NewGuid(), $"\"{acknowledged["version"]}\"",
            new { disposition = "Release", reason = "Excursion within stability budget", approvalId = approval })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Resolved", (string?)resolved["status"]);
        Assert.Equal("10|0|Available", await w.StateAsync(await w.SiblingAsync(c01, "Available")));
        Assert.Equal("Closed", (string?)(await w.GetAsync($"/api/v1/alerts/{alert["id"]}"))["status"]);
    }

    [Fact]
    [Trait("Requirement", "TEL-001")]
    [Trait("Requirement", "TEL-002")]
    [Trait("Requirement", "ENT-003")]
    public async Task Credentials_rotate_without_gaps_a_device_reports_only_for_itself_and_future_readings_are_refused()
    {
        var w = await MedicalWorld.CreateAsync(fx, "tel-cred");
        var (device, first) = await DeviceAsync(w, w.ColdZone, "SENSOR-A");
        var (other, _) = await DeviceAsync(w, w.ColdZone, "SENSOR-B");

        await (await Ingest("ti_dev_" + Guid.NewGuid().ToString("N") + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAA", device, "x", 5)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await (await Ingest(first, other, "sub-1", 5)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await Ingest(first, device, "future", 5, DateTimeOffset.UtcNow.AddMinutes(3))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(0L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.telemetry_reading WHERE tenant_id = @t", ("t", w.Tenant)));

        // Rotation: the old credential keeps working until the new one is first used, then stops.
        var version = (await w.GetAsync($"/api/v1/devices/{device}"))["version"]!.ToString();
        var second = (string)(await (await RotateAsync(w, device, version)).ExpectAsync(HttpStatusCode.OK))["secret"]!;
        await (await Ingest(first, device, "old-1", 5)).ExpectAsync(HttpStatusCode.OK);
        await (await Ingest(second, device, "new-1", 5)).ExpectAsync(HttpStatusCode.OK);
        await (await Ingest(first, device, "old-2", 5)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal(2L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.telemetry_reading WHERE tenant_id = @t", ("t", w.Tenant)));

        // Devices count against the quota; retiring frees the slot and ends every credential.
        Assert.Equal(2L, await w.ScalarAsync<long>("SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'active_devices'", ("t", w.Tenant)));
        var current = (await w.GetAsync($"/api/v1/devices/{device}"))["version"]!.ToString();
        var retire = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/devices/{device}/retire");
        retire.Headers.TryAddWithoutValidation("If-Match", $"\"{current}\"");
        Assert.Equal(HttpStatusCode.NoContent, (await w.Operator.SendAsync(retire)).StatusCode);
        Assert.Equal(1L, await w.ScalarAsync<long>("SELECT used FROM ti.usage_counter WHERE tenant_id = @t AND metric = 'active_devices'", ("t", w.Tenant)));
        await (await Ingest(second, device, "after-retire", 5)).ExpectProblemAsync(HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    [Trait("Requirement", "AT-TEL-02")]
    [Trait("Requirement", "TEL-003")]
    public async Task Stale_telemetry_blocks_allocation_of_monitored_stock_until_the_device_reports()
    {
        var w = await MedicalWorld.CreateAsync(fx, "tel-stale");
        var (device, token) = await DeviceAsync(w, w.ColdZone, "SENSOR-S");
        await w.BucketAsync(w.Medical, w.C01, 5, lot: await w.LotAsync(w.Medical, "L-S", Today.AddDays(90)));
        await w.ExecAsync("UPDATE ti.device SET last_reading_at = now() - interval '10 minutes' WHERE id = @d", ("d", device));

        var order = await w.OrderAsync(w.Medical, "2");
        await (await w.AllocateAsync(order)).ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.True((bool)(await w.GetAsync($"/api/v1/devices/{device}"))["stale"]!);
        await RunStaleJobAsync();
        var stale = Assert.Single((await w.GetAsync("/api/v1/alerts?type=StaleDevice&status=Open"))["items"]!.AsArray())!;
        Assert.Equal(device.ToString(), (string?)stale["subjectId"]);
        await (await Ingest(token, device, "fresh", 5)).ExpectAsync(HttpStatusCode.OK);
        await (await w.AllocateAsync(order)).ExpectAsync(HttpStatusCode.OK);
        await RunStaleJobAsync();
        Assert.Equal("Closed", (string?)(await w.GetAsync($"/api/v1/alerts/{stale["id"]}"))["status"]);
    }

    [Fact]
    [Trait("Requirement", "AT-TEL-02")]
    [Trait("Requirement", "TEL-004")]
    [Trait("Requirement", "TEL-002")]
    public async Task A_late_abnormal_reading_flags_shipments_dispatched_since_it_was_observed_without_claiming_they_were_blocked()
    {
        var w = await MedicalWorld.CreateAsync(fx, "tel-late");
        var (device, token) = await DeviceAsync(w, w.ColdZone, "SENSOR-L");
        await w.ExecAsync("UPDATE ti.device_zone_assignment SET effective_from = now() - interval '3 hours' WHERE device_id = @d", ("d", device));
        var lot = await w.LotAsync(w.Medical, "L-LATE", Today.AddDays(90));
        await w.BucketAsync(w.Medical, w.C01, 6, lot: lot);
        await (await Ingest(token, device, "now", 5)).ExpectAsync(HttpStatusCode.OK);
        var order = await w.OrderAsync(w.Medical, "4");
        await (await w.AllocateAsync(order)).ExpectAsync(HttpStatusCode.OK);
        await (await w.PickAllAsync(order, w.C01, "L-LATE")).ExpectAsync(HttpStatusCode.OK);
        var shipment = (string)(await (await w.DispatchAsync(order)).ExpectAsync(HttpStatusCode.OK))["shipmentId"]!;

        // An abnormal reading observed two hours ago arrives now: stored as late, reviewed, never claimed to have blocked anything.
        var late = await (await Ingest(token, device, "gateway-backlog-1", 15.5m, DateTimeOffset.UtcNow.AddHours(-2))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("LateReviewQueued", (string?)late["processingStatus"]);
        Assert.True(await w.ScalarAsync<bool>("SELECT is_late FROM ti.telemetry_reading WHERE id = @r", ("r", Guid.Parse((string)late["readingId"]!))));
        var excursion = Assert.Single((await w.GetAsync("/api/v1/excursions"))["items"]!.AsArray())!;
        Assert.True((bool)excursion["late"]!);
        Assert.Equal([shipment], excursion["potentiallyAffectedShipmentIds"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal(1L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.shipment WHERE tenant_id = @t AND status::text <> 'Cancelled'", ("t", w.Tenant)));
        // The stock still on hand in the zone is held now.
        Assert.Equal(2m, await w.ScalarAsync<decimal>("SELECT sum(on_hand_qty) FROM ti.stock_bucket WHERE tenant_id = @t AND quality_status = 'Hold'", ("t", w.Tenant)));
        // A late normal reading neither ends nor clears anything.
        await (await Ingest(token, device, "gateway-backlog-2", 5, DateTimeOffset.UtcNow.AddHours(-1))).ExpectAsync(HttpStatusCode.OK);
        Assert.Null((await w.GetAsync($"/api/v1/excursions/{excursion["id"]}"))["endedAt"]?.GetValue<string>());
    }

    // ---- helpers ----

    private async Task RunStaleJobAsync()
    {
        await using var app = TrustedInventory.Infrastructure.DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = TrustedInventory.Infrastructure.DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TrustedInventory.Infrastructure.Jobs.TenantJobRunner(new TrustedInventory.Infrastructure.Persistence.PlatformDataSource(platform),
            new TrustedInventory.Infrastructure.Persistence.AppDataSource(app), TimeProvider.System);
        await new TrustedInventory.Infrastructure.Jobs.StaleDeviceJob(runner,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TrustedInventory.Application.Audit.IAuditSupport>(fx.Factory.Services),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TrustedInventory.Infrastructure.Jobs.StaleDeviceJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

    private static async Task<(Guid Device, string Token)> DeviceAsync(MedicalWorld w, string zone, string externalId)
    {
        var created = await (await w.Operator.PostJsonAsync("/api/v1/devices", new { zoneId = zone, externalId, name = $"Logger {externalId}" }))
            .ExpectAsync(HttpStatusCode.Created);
        var token = (string)(await (await RotateAsync(w, Guid.Parse((string)created["id"]!), created["version"]!.ToString())).ExpectAsync(HttpStatusCode.OK))["secret"]!;
        return (Guid.Parse((string)created["id"]!), token);
    }

    private static Task<HttpResponseMessage> RotateAsync(MedicalWorld w, Guid device, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/devices/{device}/rotate-credential");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return w.Operator.SendAsync(request);
    }

    private Task<HttpResponseMessage> Ingest(string token, Guid device, string eventId, decimal celsius, DateTimeOffset? observedAt = null)
    {
        var client = fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.PostAsJsonAsync("/api/v1/telemetry/readings",
            new { deviceId = device, eventId, observedAt = observedAt ?? DateTimeOffset.UtcNow, temperatureCelsius = celsius });
    }
}
