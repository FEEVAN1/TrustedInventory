using TrustedInventory.Domain.Saas;

namespace TrustedInventory.UnitTests;

/// <summary>The "Background work by mode" table (docs/saas-state-matrix.md) cell by cell (DEC-043).</summary>
public class BackgroundWorkTests
{
    // Rows: implemented jobs; columns: Normal, Provisioning, BillingRestricted, SecuritySuspended, Closed, Deleted.
    // Values: Y runs, N does not. Copied from the document's table.
    private static readonly Dictionary<BackgroundJob, string> Table = new()
    {
        [BackgroundJob.OutboxDelivery] = "YYYYYN",
        [BackgroundJob.IdempotencyCleanup] = "YYYYYN",
        [BackgroundJob.AuditCheckpoint] = "YYYYYN", // Deleted: deletion evidence only, arrives with S3
        [BackgroundJob.LedgerReconciliation] = "YYYYYN", // same document row as checkpoints
        [BackgroundJob.ReservationExpiry] = "YYYYYN",
        [BackgroundJob.ExportGeneration] = "YYYYYN",
        [BackgroundJob.BillingNotices] = "YYYYYN", // row "Billing reminders and reconciliation"
    };

    private static readonly AccessMode[] Columns =
        [AccessMode.Normal, AccessMode.Provisioning, AccessMode.BillingRestricted, AccessMode.SecuritySuspended, AccessMode.Closed, AccessMode.Deleted];

    public static TheoryData<BackgroundJob, AccessMode, char> Cells()
    {
        var data = new TheoryData<BackgroundJob, AccessMode, char>();
        foreach (var (job, row) in Table)
            for (var i = 0; i < Columns.Length; i++)
                data.Add(job, Columns[i], row[i]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    [Trait("Requirement", "SAS-005")]
    [Trait("Requirement", "LFC-001")]
    public void Table_cell_matches_the_document(BackgroundJob job, AccessMode mode, char expected) =>
        Assert.Equal(expected == 'Y', BackgroundWork.Runs(job, mode));

    [Fact]
    [Trait("Requirement", "SAS-005")]
    public void Every_job_has_a_row() =>
        Assert.Equal(Enum.GetValues<BackgroundJob>().Order(), Table.Keys.Order());

    /// <summary>The precondition that lets the worker decide from lifecycle without reading billing state.</summary>
    [Theory]
    [MemberData(nameof(Jobs))]
    [Trait("Requirement", "SAS-004")]
    [Trait("Requirement", "SAS-005")]
    public void No_job_depends_on_billing_or_security_state(BackgroundJob job)
    {
        var normal = BackgroundWork.Runs(job, AccessMode.Normal);
        Assert.Equal(normal, BackgroundWork.Runs(job, AccessMode.BillingRestricted));
        Assert.Equal(normal, BackgroundWork.Runs(job, AccessMode.SecuritySuspended));
    }

    [Theory]
    [InlineData(TenantLifecycle.Provisioning, AccessMode.Provisioning)]
    [InlineData(TenantLifecycle.Ready, AccessMode.Normal)]
    [InlineData(TenantLifecycle.Archived, AccessMode.Closed)]
    [InlineData(TenantLifecycle.DeletionPending, AccessMode.Closed)]
    [InlineData(TenantLifecycle.Deleted, AccessMode.Deleted)]
    [Trait("Requirement", "LFC-001")]
    public void Lifecycle_decides_like_its_access_mode_column(TenantLifecycle lifecycle, AccessMode column)
    {
        foreach (var job in Enum.GetValues<BackgroundJob>())
            Assert.Equal(BackgroundWork.Runs(job, column), BackgroundWork.RunsFor(job, lifecycle));
    }

    [Fact]
    [Trait("Requirement", "LFC-001")]
    public void An_unknown_lifecycle_fails_closed()
    {
        foreach (var job in Enum.GetValues<BackgroundJob>())
            Assert.False(BackgroundWork.RunsFor(job, (TenantLifecycle)999));
    }

    public static TheoryData<BackgroundJob> Jobs() => new(Enum.GetValues<BackgroundJob>());
}
