using TrustedInventory.Domain.Common;

namespace TrustedInventory.Domain.Operations;

public enum TransferStatus
{
    Draft, Approved, InTransit, PartReceived, Received, Cancelled,
}

public enum TransitDispositionType
{
    Lost, Damaged,
}

/// <summary>
/// A warehouse-to-warehouse transfer through the source warehouse's Transit bin (EXC-001, STK-003, DEC-010, DEC-058).
/// Draft, Approved and Cancelled are stored decisions; once dispatched the status follows the line quantities.
/// </summary>
public sealed class Transfer : TenantEntity
{
    private Transfer() { }

    public Guid SourceWarehouseId { get; private set; }
    public Guid DestinationWarehouseId { get; private set; }
    public string Reference { get; private set; } = "";
    public TransferStatus Status { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public string LastModifiedBy { get; private set; } = "";

    /// <summary>Approved and not finished: stock can still be dispatched, received or disposed.</summary>
    public bool IsMoving => Status is TransferStatus.Approved or TransferStatus.InTransit or TransferStatus.PartReceived;

    public static Transfer Create(Guid tenantId, Guid source, Guid destination, string reference, string actor, DateTimeOffset now)
    {
        if (source == destination)
            throw new DomainException("A transfer moves stock between two different warehouses.");
        var transfer = new Transfer
        {
            SourceWarehouseId = source, DestinationWarehouseId = destination, Reference = reference, Status = TransferStatus.Draft, LastModifiedBy = actor,
        };
        transfer.Stamp(tenantId, actor, now);
        return transfer;
    }

    public void Approve(string actor, DateTimeOffset now)
    {
        if (Status != TransferStatus.Draft)
            throw new DomainException("Only a draft transfer can be approved.");
        Status = TransferStatus.Approved;
        ApprovedBy = actor;
        ApprovedAt = now;
        Modified(actor, now);
    }

    /// <summary>Only before anything has been dispatched (DEC-058).</summary>
    public void Cancel(string actor, IEnumerable<TransferLine> lines, DateTimeOffset now)
    {
        if (Status is not (TransferStatus.Draft or TransferStatus.Approved) || lines.Any(l => l.DispatchedQty > 0))
            throw new DomainException("Only a transfer with nothing dispatched can be cancelled.");
        Status = TransferStatus.Cancelled;
        Modified(actor, now);
    }

    /// <summary>Re-derives the status after a dispatch, receipt or disposition, and records who made the change.</summary>
    public void Recalculate(IReadOnlyCollection<TransferLine> lines, string actor, DateTimeOffset now)
    {
        if (!IsMoving)
            throw new DomainException("Only an approved, unfinished transfer moves stock.");
        var dispatched = lines.Any(l => l.DispatchedQty > 0);
        var settled = lines.Any(l => l.ReceivedQty > 0 || l.DisposedQty > 0);
        var complete = lines.Count > 0 && lines.All(l => l.DispatchedQty == l.RequestedQty && l.InTransitQty == 0);
        Status = complete ? TransferStatus.Received : settled ? TransferStatus.PartReceived : dispatched ? TransferStatus.InTransit : TransferStatus.Approved;
        if (dispatched)
            DispatchedAt ??= now;
        if (complete)
            ReceivedAt = now;
        Modified(actor, now);
    }

    private void Modified(string actor, DateTimeOffset now)
    {
        LastModifiedBy = actor;
        Touch(now);
    }
}

/// <summary>A transfer line: product and optional lot; quantities satisfy <c>dispatched ≤ requested</c> and <c>received + disposed ≤ dispatched</c>.</summary>
public sealed class TransferLine : TenantEntity
{
    private TransferLine() { }

    public Guid TransferId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid? LotId { get; private set; }
    public decimal RequestedQty { get; private set; }
    public decimal DispatchedQty { get; private set; }
    public decimal ReceivedQty { get; private set; }
    public decimal DisposedQty { get; private set; }

    public decimal InTransitQty => DispatchedQty - ReceivedQty - DisposedQty;

    public decimal RemainingToDispatch => RequestedQty - DispatchedQty;

    public static TransferLine Create(Guid tenantId, Guid transferId, int lineNumber, Guid productId, Guid? lotId, decimal requested, string actor,
        DateTimeOffset now)
    {
        if (lineNumber < 1 || requested <= 0)
            throw new DomainException("Invalid transfer line.");
        var line = new TransferLine { TransferId = transferId, LineNumber = lineNumber, ProductId = productId, LotId = lotId, RequestedQty = requested };
        line.Stamp(tenantId, actor, now);
        return line;
    }

    public void Dispatch(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > RemainingToDispatch)
            throw new DomainException("A dispatch cannot exceed what remains to dispatch on the line.");
        DispatchedQty += qty;
        Touch(now);
    }

    public void Receive(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > InTransitQty)
            throw new DomainException("A receipt cannot exceed what is in transit on the line.");
        ReceivedQty += qty;
        Touch(now);
    }

    public void Dispose(decimal qty, DateTimeOffset now)
    {
        if (qty <= 0 || qty > InTransitQty)
            throw new DomainException("A disposition cannot exceed what is in transit on the line.");
        DisposedQty += qty;
        Touch(now);
    }
}
