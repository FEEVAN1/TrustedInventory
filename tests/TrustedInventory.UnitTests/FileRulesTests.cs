using TrustedInventory.Domain.Common;
using TrustedInventory.Domain.Operations;

namespace TrustedInventory.UnitTests;

/// <summary>Attachment and export job rules (TEN-005, AUD-004, DEC-061).</summary>
public class FileRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    [Trait("Requirement", "TEN-005")]
    public void Attachments_live_under_their_tenant_prefix_and_respect_the_size_limit()
    {
        var id = Guid.NewGuid();
        var attachment = Attachment.Create(id, Tenant, "a.pdf", "application/pdf", 10, new byte[32], clean: true, null, null, "clerk", Now);
        Assert.Equal($"{Tenant:D}/attachments/{id:D}", attachment.ObjectKey);
        Assert.Throws<DomainException>(() => Attachment.Create(id, Tenant, "a.pdf", "application/pdf", Attachment.MaxBytes + 1, new byte[32], true, null, null, "clerk", Now));
        Assert.Throws<DomainException>(() => Attachment.Create(id, Tenant, "a.pdf", "application/pdf", 10, new byte[32], true, "Adjustment", null, "clerk", Now));
        Assert.Equal(AttachmentScanStatus.Rejected,
            Attachment.Create(id, Tenant, "a.pdf", "application/pdf", 10, new byte[32], clean: false, null, null, "clerk", Now).ScanStatus);
    }

    [Fact]
    [Trait("Requirement", "AUD-004")]
    [Trait("Requirement", "NFR-EXPORTS")]
    public void An_export_runs_once_completes_with_a_tenant_key_and_is_kept_30_days()
    {
        var job = ExportJob.Queue(Tenant, ExportType.Movements, "{}", new byte[32], "auditor", Now);
        Assert.Throws<DomainException>(() => job.Complete(1, 2, "{}", Now));
        job.Start(Now);
        Assert.Throws<DomainException>(() => job.Start(Now));
        job.Complete(1, 2, "{}", Now);
        Assert.Equal((ExportStatus.Completed, $"{Tenant:D}/exports/{job.Id:D}.jsonl", Now.AddDays(30)), (job.Status, job.ObjectKey!, job.RetainUntil!.Value));
        Assert.Throws<DomainException>(() => job.Fail("late", Now));
    }
}
