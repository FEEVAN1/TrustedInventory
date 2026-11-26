using System.Net;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Medical;

/// <summary>Recalls: immediate blocking, impact, outreach and closure (EXC-006, EXC-007, AT-OUT-02, DEC-076).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RecallTests(ApiFixture fx)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    [Trait("Requirement", "EXC-006")]
    [Trait("Requirement", "AT-OUT-02")]
    [Trait("Requirement", "OUT-006")]
    public async Task A_recall_after_pick_blocks_dispatch_allocation_skips_the_lot_and_the_impact_names_stock_and_customers()
    {
        var w = await MedicalWorld.CreateAsync(fx, "recall-block");
        var recalledLot = await w.LotAsync(w.Medical, "L-BAD", Today.AddDays(60));   // earliest expiry: FEFO takes it first
        var goodLot = await w.LotAsync(w.Medical, "L-GOOD", Today.AddDays(200));
        var bad = await w.BucketAsync(w.Medical, w.C01, 10, lot: recalledLot);
        await w.BucketAsync(w.Medical, w.C02, 5, lot: goodLot);

        // Before the recall: 4 shipped to the customer, 2 picked for another order.
        var shipped = await w.OrderAsync(w.Medical, "4");
        await (await w.AllocateAsync(shipped)).ExpectAsync(HttpStatusCode.OK);
        await (await w.PickAllAsync(shipped, w.C01, "L-BAD")).ExpectAsync(HttpStatusCode.OK);
        await (await w.DispatchAsync(shipped)).ExpectAsync(HttpStatusCode.OK);
        var picked = await w.OrderAsync(w.Medical, "2");
        await (await w.AllocateAsync(picked)).ExpectAsync(HttpStatusCode.OK);
        await (await w.PickAllAsync(picked, w.C01, "L-BAD")).ExpectAsync(HttpStatusCode.OK);

        var recall = await (await w.PostAsync("/api/v1/recalls", Guid.NewGuid(), null, new
        {
            productId = w.Medical, lotIds = new[] { recalledLot }, reason = "Supplier reports sterility failure", effectiveAt = DateTimeOffset.UtcNow,
        })).ExpectAsync(HttpStatusCode.Created);
        var impact = recall["impact"]!;
        Assert.Equal(["Recalled", "Recalled"], impact["onHand"]!.AsArray().Select(b => (string)b!["qualityStatus"]!).ToArray());
        Assert.Equal(6m, impact["onHand"]!.AsArray().Sum(b => decimal.Parse((string)b!["quantity"]!, System.Globalization.CultureInfo.InvariantCulture)));
        var reservation = Assert.Single(impact["reservations"]!.AsArray())!;
        Assert.Equal("Picked", (string?)reservation["state"]);
        var shipment = Assert.Single(impact["shipments"]!.AsArray())!;
        Assert.Equal(w.Customer, (string?)shipment["customerId"]);
        Assert.Equal("4", (string?)shipment["quantity"]);
        Assert.Equal("0|0|Available", await w.StateAsync(bad));

        // AT-OUT-02: the picked stock cannot be dispatched; nothing ships.
        var blocked = await (await w.DispatchAsync(picked)).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        Assert.NotNull(blocked["affectedPicks"]);
        Assert.Equal(1L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.shipment WHERE tenant_id = @t", ("t", w.Tenant)));

        // New orders are served only from the lot that is not recalled.
        var next = await w.OrderAsync(w.Medical, "3");
        await (await w.AllocateAsync(next)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(goodLot, await w.ScalarAsync<Guid>("""
            SELECT b.lot_id FROM ti.reservation r JOIN ti.outbound_order_line l ON l.id = r.order_line_id JOIN ti.stock_bucket b ON b.id = r.bucket_id
            WHERE l.order_id = @o
            """, ("o", Guid.Parse(next))));
        var tooMuch = await w.OrderAsync(w.Medical, "3");
        await (await w.AllocateAsync(tooMuch)).ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");

        // Lot traceability: the shipment to the customer, the open recall and the quality changes, from the ledger (section 02).
        var trace = await w.GetAsync($"/api/v1/traceability/lots/{recalledLot}");
        Assert.Equal("L-BAD", (string?)trace["lot"]!["lotNumber"]);
        Assert.Equal(w.Customer, (string?)Assert.Single(trace["shipments"]!.AsArray())!["customerId"]);
        Assert.Equal("Open", (string?)Assert.Single(trace["recalls"]!.AsArray())!["status"]);
        Assert.Contains(trace["qualityHistory"]!.AsArray(), q => (string?)q!["to"] == "Recalled");
        Assert.All(trace["currentStock"]!.AsArray(), b => Assert.Equal("Recalled", (string?)b!["qualityStatus"]));
        Assert.Contains(trace["movements"]!.AsArray(), m => (string?)m!["type"] == "Dispatch");
    }

    [Fact]
    [Trait("Requirement", "EXC-007")]
    [Trait("Requirement", "EXC-006")]
    [Trait("Requirement", "SOD-2")]
    public async Task Recalled_stock_cannot_be_released_and_closure_needs_disposition_and_an_independent_QA_approval()
    {
        var w = await MedicalWorld.CreateAsync(fx, "recall-close");
        var lot = await w.LotAsync(w.Medical, "L-RCL", Today.AddDays(90));
        var bucket = await w.BucketAsync(w.Medical, w.C01, 5, lot: lot);
        var created = await (await w.PostAsync("/api/v1/recalls", Guid.NewGuid(), null, new
        {
            productId = w.Medical, lotIds = new[] { lot }, reason = "Mislabelled strength", effectiveAt = DateTimeOffset.UtcNow,
        })).ExpectAsync(HttpStatusCode.Created);
        var id = (string)created["id"]!;
        var recalled = await w.SiblingAsync(bucket, "Recalled");
        Assert.Equal("5|0|Recalled", await w.StateAsync(recalled));

        // No ordinary release; closure is refused while recalled stock remains.
        await (await w.PostAsync("/api/v1/quality/release", Guid.NewGuid(), null, new { bucketId = recalled, approvalId = Guid.NewGuid() }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RELEASE_INELIGIBLE");
        await (await w.PostAsync($"/api/v1/recalls/{id}/close", Guid.NewGuid(), $"\"{created["version"]}\"", new { approvalId = Guid.NewGuid(), reason = "done" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        // The workspace cannot close while the recall is open.
        Assert.Equal(1L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.recall WHERE tenant_id = @t AND status = 'Open'", ("t", w.Tenant)));

        // QA dispositions the recalled stock (approval required), and outreach is recorded by hand.
        var disposal = await w.ApproveBucketAsync("QualityDisposition", recalled, action: "Dispose");
        await (await w.PostAsync("/api/v1/quality/disposition", Guid.NewGuid(), null,
            new { bucketId = recalled, quantity = "5", disposition = "Disposal", reason = "Destroyed under recall", approvalId = disposal })).ExpectAsync(HttpStatusCode.OK);
        var withOutreach = await (await w.Operator.PostJsonAsync($"/api/v1/recalls/{id}/outreach",
            new { customerId = w.Customer, status = "Contacted", note = "Phoned the pharmacist" }, $"\"{created["version"]}\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Contacted", (string?)withOutreach["outreach"]![0]!["status"]);
        var version = (int)withOutreach["version"]!;

        // The opener cannot sign the closure (SOD-1); an independent QA officer can.
        var approval = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "RecallClosure", subjectId = id, subjectVersion = version, action = "Close", reason = "All stock dispositioned",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Operator.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.OperatorSubject), meaning = "Approval", reason = "mine" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.SignerSubject), meaning = "Approval", reason = "Destruction certificate reviewed" }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        var closed = await (await w.PostAsync($"/api/v1/recalls/{id}/close", Guid.NewGuid(), $"\"{version}\"", new { approvalId = approval, reason = "Recall complete" }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Closed", (string?)closed["status"]);
        Assert.Equal(approval, (string?)closed["closureApprovalId"]);
        Assert.Equal(lot.ToString(), (string?)closed["lotIds"]![0]);   // the recall flag and history are kept
        Assert.Single(closed["outreach"]!.AsArray());
        await (await w.Operator.PostJsonAsync($"/api/v1/recalls/{id}/outreach", new { customerId = w.Customer, status = "Completed" }, $"\"{closed["version"]}\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
    }
}
