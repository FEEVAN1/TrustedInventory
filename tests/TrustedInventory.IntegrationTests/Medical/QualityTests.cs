using System.Net;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Medical;

/// <summary>Quality hold, release and disposition, expiry and shelf-life boundaries (MED-001 to MED-003, SOD-2, EXC-007, DEC-075).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class QualityTests(ApiFixture fx)
{
    private static DateOnly Today(string timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    [Fact]
    [Trait("Requirement", "MED-001")]
    [Trait("Requirement", "SOD-2")]
    [Trait("Requirement", "SOD-1")]
    public async Task Quarantined_medical_stock_is_released_whole_only_with_an_independent_QA_signature()
    {
        var w = await MedicalWorld.CreateAsync(fx, "qa-release");
        var lot = await w.LotAsync(w.Medical, "L-REL", Today("UTC").AddDays(200));
        var bucket = await w.BucketAsync(w.Medical, w.C01, 10, "Quarantine", lot);

        // The requester cannot sign their own request (SOD-1); without approval nothing moves.
        var approval = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "QualityRelease", subjectId = bucket, subjectVersion = await w.BucketVersionAsync(bucket), action = "Release", reason = "Release",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Operator.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.OperatorSubject), meaning = "QualityRelease", reason = "mine" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
        await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = bucket, approvalId = approval }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.SignerSubject), meaning = "QualityRelease", reason = "CoA and temperature log checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);

        var key = Guid.NewGuid();
        var released = await (await w.PostAsync("/api/v1/quality/release", key, null, new { bucketId = bucket, approvalId = approval })).ExpectAsync(HttpStatusCode.OK);
        var available = await w.SiblingAsync(bucket, "Available");
        Assert.Equal(available.ToString(), (string?)released["buckets"]![0]!["bucketId"]);
        Assert.Equal(["0|0|Quarantine", "10|0|Available"], new[] { await w.StateAsync(bucket), await w.StateAsync(available) });
        Assert.Equal("Consumed", (string?)(await w.GetAsync($"/api/v1/approvals/{approval}"))["status"]);
        var replay = await (await w.PostAsync("/api/v1/quality/release", key, null, new { bucketId = bucket, approvalId = approval })).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(released, replay));
        Assert.Equal(1L, await w.ScalarAsync<long>(
            "SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'QualityChange' AND source_bucket_id = @b", ("t", w.Tenant), ("b", bucket)));

        // Whoever received the goods cannot sign their release (SOD-1).
        var other = await w.BucketAsync(w.Medical, w.C02, 3, "Quarantine", lot, receivedBy: w.SignerSubject);
        var mine = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "QualityRelease", subjectId = other, subjectVersion = await w.BucketVersionAsync(other), action = "Release", reason = "Release",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{mine}/sign",
            new { reauthToken = TestTokens.Issue(w.SignerSubject), meaning = "QualityRelease", reason = "x" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
    }

    [Fact]
    [Trait("Requirement", "MED-001")]
    [Trait("Requirement", "MED-003")]
    [Trait("Requirement", "EXC-007")]
    public async Task Release_checks_are_reported_together_and_no_signature_can_override_them()
    {
        var w = await MedicalWorld.CreateAsync(fx, "qa-ineligible");
        var expiring = await w.LotAsync(w.Medical, "L-EXP", Today("UTC"));
        var expired = await w.BucketAsync(w.Medical, w.C01, 4, "Quarantine", expiring);
        var problem = await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = expired, approvalId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RELEASE_INELIGIBLE");
        Assert.Contains(problem["ineligibility"]!.AsArray(), p => (string?)p!["code"] == "Expired");

        // Wrong storage (medical cold-chain stock in the ambient zone) and an expired lot are reported together.
        var misplaced = await w.BucketAsync(w.Medical, w.A01, 2, "Hold", expiring);
        var codes = (await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = misplaced, approvalId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RELEASE_INELIGIBLE"))["ineligibility"]!.AsArray().Select(p => (string)p!["code"]!).Order();
        Assert.Equal(["Expired", "StorageIncompatible"], codes);

        // Recalled stock has no ordinary release, and no release approval can even be requested for it (EXC-007).
        var good = await w.LotAsync(w.Medical, "L-RC", Today("UTC").AddDays(100));
        var recalled = await w.BucketAsync(w.Medical, w.C01, 5, "Recalled", good);
        var notReleasable = await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = recalled, approvalId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RELEASE_INELIGIBLE");
        Assert.Equal("NotReleasable", (string?)notReleasable["ineligibility"]![0]!["code"]);
        await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "QualityRelease", subjectId = recalled, subjectVersion = await w.BucketVersionAsync(recalled), action = "Release", reason = "Try",
        })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal("5|0|Recalled", await w.StateAsync(recalled));
    }

    [Fact]
    [Trait("Requirement", "MED-002")]
    [Trait("Requirement", "STK-001")]
    public async Task A_hold_takes_reserved_stock_and_its_reservation_so_nothing_held_can_be_picked()
    {
        var w = await MedicalWorld.CreateAsync(fx, "qa-hold");
        var bucket = await w.BucketAsync(w.General, w.A01, 10);
        var order = await w.OrderAsync(w.General, "4");
        await (await w.AllocateAsync(order)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("10|4|Available", await w.StateAsync(bucket));

        var held = await (await w.PostAsync("/api/v1/quality/hold", Guid.NewGuid(), null, new { bucketIds = new[] { bucket }, reason = "Damaged outer cartons reported" }))
            .ExpectAsync(HttpStatusCode.OK);
        var hold = await w.SiblingAsync(bucket, "Hold");
        Assert.Equal(["0|0|Available", "10|4|Hold"], new[] { await w.StateAsync(bucket), await w.StateAsync(hold) });
        var reservation = Assert.Single(held["affectedReservationIds"]!.AsArray())!.ToString();
        Assert.Equal(hold, await w.ScalarAsync<Guid>("SELECT bucket_id FROM ti.reservation WHERE id = @r", ("r", Guid.Parse(reservation))));

        // The held reservation cannot be picked, and allocation finds nothing else.
        Assert.NotEqual(HttpStatusCode.OK, (await w.PickAllAsync(order, w.A01, null)).StatusCode);
        Assert.Equal("10|4|Hold", await w.StateAsync(hold));
        // A hold release while reservations still point at the stock is refused; the stock stays held.
        var approval = await w.ApproveBucketAsync("HoldRelease", hold);
        await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = hold, approvalId = approval }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        // Holding stock that is already held is refused rather than silently repeated.
        await (await w.PostAsync("/api/v1/quality/hold", Guid.NewGuid(), null, new { bucketIds = new[] { hold }, reason = "again" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "EXC-007")]
    [Trait("Requirement", "SOD-2")]
    public async Task Medical_dispositions_need_a_QA_approval_and_disposal_leaves_the_ledger_with_a_reason()
    {
        var w = await MedicalWorld.CreateAsync(fx, "qa-dispose");
        var lot = await w.LotAsync(w.Medical, "L-DSP", Today("UTC").AddDays(90));
        var held = await w.BucketAsync(w.Medical, w.C01, 6, "Hold", lot);

        await (await w.PostAsync("/api/v1/quality/disposition", Guid.NewGuid(), null,
            new { bucketId = held, quantity = "6", disposition = "Disposal", reason = "Temperature excursion confirmed" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var approval = await w.ApproveBucketAsync("QualityDisposition", held, action: "Dispose");
        var disposed = await (await w.PostAsync("/api/v1/quality/disposition", Guid.NewGuid(), null,
            new { bucketId = held, quantity = "6", disposition = "Disposal", reason = "Temperature excursion confirmed", approvalId = approval }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("0|0|Hold", await w.StateAsync(held));
        Assert.Equal("Disposal|6|Disposal: Temperature excursion confirmed", await w.ScalarAsync<string>("""
            SELECT movement_type::text || '|' || trim_scale(base_qty)::text || '|' || reason FROM ti.stock_movement WHERE id = @m
            """, ("m", Guid.Parse((string)disposed["movementIds"]![0]!))));

        // General held stock may be marked damaged without approval; available stock must be held first.
        var general = await w.BucketAsync(w.General, w.A01, 5, "Hold");
        await (await w.PostAsync("/api/v1/quality/disposition", Guid.NewGuid(), null,
            new { bucketId = general, quantity = "2", disposition = "Damaged", reason = "Crushed" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("2|0|Damaged", await w.StateAsync(await w.SiblingAsync(general, "Damaged")));
        var available = await w.BucketAsync(w.General, w.A01, 1);
        await (await w.PostAsync("/api/v1/quality/disposition", Guid.NewGuid(), null,
            new { bucketId = available, quantity = "1", disposition = "Disposal", reason = "x" })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    [Trait("Requirement", "AT-MED-01")]
    [Trait("Requirement", "MED-003")]
    public async Task On_the_expiry_day_in_the_warehouse_time_zone_a_lot_is_blocked_while_a_day_earlier_it_is_not()
    {
        // Kiritimati (UTC+14) is always one calendar day ahead of Pago Pago (UTC-11): the same lot expires "today" in one and
        // "tomorrow" in the other at the same instant, so the boundary is evaluated in each warehouse's own zone.
        const string ahead = "Pacific/Kiritimati", behind = "Pacific/Pago_Pago";
        var expiry = Today(ahead);
        Assert.Equal(expiry, Today(behind).AddDays(1));

        var late = await MedicalWorld.CreateAsync(fx, "qa-tz-ahead", ahead);
        var early = await MedicalWorld.CreateAsync(fx, "qa-tz-behind", behind);
        var lateBucket = await late.BucketAsync(late.Medical, late.C01, 3, "Quarantine", await late.LotAsync(late.Medical, "L-TZ", expiry));
        var earlyBucket = await early.BucketAsync(early.Medical, early.C01, 3, "Quarantine", await early.LotAsync(early.Medical, "L-TZ", expiry));

        var blocked = await (await late.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = lateBucket, approvalId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RELEASE_INELIGIBLE");
        Assert.Equal("Expired", (string?)blocked["ineligibility"]![0]!["code"]);
        var approval = await early.ApproveBucketAsync("QualityRelease", earlyBucket);
        await (await early.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = earlyBucket, approvalId = approval }))
            .ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Requirement", "AT-MED-02")]
    public async Task Minimum_remaining_shelf_life_is_met_at_exactly_the_boundary_and_not_one_day_short()
    {
        var w = await MedicalWorld.CreateAsync(fx, "qa-shelf");
        // Orders dispatch tomorrow; the lot then has exactly 30 days left.
        var dispatch = Today("UTC").AddDays(1);
        await w.BucketAsync(w.General, w.A01, 5, lot: await w.LotAsync(w.General, "L-SL", dispatch.AddDays(30)));

        var shortOrder = await w.OrderAsync(w.General, "1", minShelfLifeDays: 31);
        await (await w.AllocateAsync(shortOrder)).ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        var exact = await w.OrderAsync(w.General, "1", minShelfLifeDays: 30);
        await (await w.AllocateAsync(exact)).ExpectAsync(HttpStatusCode.OK);
    }
}
