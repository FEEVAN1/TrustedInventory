using System.Net;
using Npgsql;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Medical;

/// <summary>
/// Lot expiry correction (spec section 07, MED-003, IAM-003, DEC-090) through the API on real PostgreSQL: the expiry changes only with
/// all stock of the lot held and a QA approval signed for that exact date by someone independent, used once, audited; the database
/// refuses any other change of the expiry or of the approval's proposal.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class LotExpiryCorrectionTests(ApiFixture fx)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    [Trait("Requirement", "MED-003")]
    [Trait("Requirement", "IAM-003")]
    [Trait("Requirement", "SOD-1")]
    [Trait("Requirement", "STK-007")]
    public async Task An_expiry_changes_only_with_held_stock_and_an_independent_approval_signed_for_that_date()
    {
        var w = await MedicalWorld.CreateAsync(fx, "lot-fix");
        var printed = Today.AddDays(120);
        var corrected = Today.AddDays(60);
        var lot = await w.LotAsync(w.Medical, "L-FIX", printed);
        var bucket = await w.BucketAsync(w.Medical, w.C01, 5, lot: lot);
        object Request(DateOnly? date) => date is { } d
            ? new { subjectType = "ExpiryCorrection", subjectId = lot, subjectVersion = 1, action = "Correct", reason = "Supplier certificate shows an earlier date", proposal = new { expiryDate = d } }
            : new { subjectType = "ExpiryCorrection", subjectId = lot, subjectVersion = 1, action = "Correct", reason = "Supplier certificate shows an earlier date" };

        // The proposal is required, and approval is refused while any of the lot's stock is available.
        await (await w.Operator.PostJsonAsync("/api/v1/approvals", Request(null))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await w.Operator.PostJsonAsync("/api/v1/approvals", Request(corrected))).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.PostAsync("/api/v1/quality/hold", Guid.NewGuid(), null, new { bucketIds = new[] { bucket }, reason = "Expiry under review" }))
            .ExpectAsync(HttpStatusCode.OK);
        await (await w.Operator.PostJsonAsync("/api/v1/approvals", Request(printed))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var requested = await (await w.Operator.PostJsonAsync("/api/v1/approvals", Request(corrected))).ExpectAsync(HttpStatusCode.Created);
        var approval = (string)requested["id"]!;
        Assert.Equal(Iso(corrected), (string?)requested["proposal"]!["expiryDate"]);
        async Task<HttpResponseMessage> Correct(DateOnly date, string approvalId, Guid? key = null, int version = 1) =>
            await w.PostAsync($"/api/v1/lots/{lot}/expiry-correction", key ?? Guid.NewGuid(), $"\"{version}\"",
                new { expiryDate = date, reason = "Corrected to the certificate", approvalId });

        // Not signed yet; the requester cannot sign her own request; an independent QA officer signs.
        await (await Correct(corrected, approval)).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        await (await w.Operator.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.OperatorSubject), meaning = "Approval", reason = "mine" }, "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
        await (await w.Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.SignerSubject), meaning = "Approval", reason = "Certificate checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);

        // The signature covers this date only; the lot's version is checked; then the correction applies once.
        await (await Correct(corrected.AddDays(1), approval)).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");
        await (await Correct(corrected, approval, version: 2)).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        var key = Guid.NewGuid();
        var done = await (await Correct(corrected, approval, key)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(Iso(corrected), (string?)done["expiryDate"]);
        Assert.Equal(2, (int)done["version"]!);
        Assert.True((bool)done["expiryCorrected"]!);
        var replay = await (await Correct(corrected, approval, key)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)done["version"]?.ToString(), (string?)replay["version"]?.ToString());
        await (await Correct(corrected.AddDays(-1), approval, version: 2)).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE"); // used once

        Assert.Equal(1L, await w.ScalarAsync<long>("SELECT count(*) FROM ti.lot_expiry_correction WHERE lot_id = @l", ("l", lot)));
        Assert.Equal(1L, await w.ScalarAsync<long>(
            "SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'LotExpiryCorrected'", ("t", w.Tenant)));
        Assert.Equal("Consumed", await w.ScalarAsync<string>("SELECT status::text FROM ti.approval WHERE id = @a", ("a", Guid.Parse(approval))));
        Assert.Equal(Iso(corrected), (string?)(await w.GetAsync($"/api/v1/lots/{lot}"))["expiryDate"]);
        Assert.Equal("5|0|Hold", await w.StateAsync(await w.SiblingAsync(bucket, "Hold"))); // the held stock is untouched
    }

    [Fact]
    [Trait("Requirement", "MED-003")]
    [Trait("Requirement", "IAM-003")]
    public async Task The_database_refuses_an_expiry_change_without_a_correction_and_any_change_to_a_proposal()
    {
        var w = await MedicalWorld.CreateAsync(fx, "lot-guard");
        var lot = await w.LotAsync(w.Medical, "L-GUARD", Today.AddDays(90));
        var expiry = await Assert.ThrowsAsync<PostgresException>(() =>
            w.ExecAsync("UPDATE ti.lot SET expiry_date = expiry_date + 1 WHERE id = @l", ("l", lot)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, expiry.SqlState);
        var identity = await Assert.ThrowsAsync<PostgresException>(() =>
            w.ExecAsync("UPDATE ti.lot SET lot_number = 'L-OTHER' WHERE id = @l", ("l", lot)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, identity.SqlState);

        var bucket = await w.BucketAsync(w.Medical, w.C01, 3, quality: "Hold", lot: lot);
        Assert.NotEqual(Guid.Empty, bucket);
        var approval = (string)(await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "ExpiryCorrection", subjectId = lot, subjectVersion = 1, action = "Correct", reason = "Label misprint",
            proposal = new { expiryDate = Today.AddDays(30) },
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var proposal = await Assert.ThrowsAsync<PostgresException>(() =>
            w.ExecAsync("UPDATE ti.approval SET proposal = '{\"expiryDate\":\"2099-01-01\"}' WHERE id = @a", ("a", Guid.Parse(approval))));
        Assert.Equal(PostgresErrorCodes.CheckViolation, proposal.SqlState);
        // Other subject types never carry a proposal.
        await (await w.Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "QualityRelease", subjectId = bucket, subjectVersion = 1, action = "Release", reason = "x", proposal = new { expiryDate = Today },
        })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }
}
