namespace TrustedInventory.Domain.Saas;

// Values match cp.provisioning_status / cp.provisioning_step (docs/enums.md); never reorder or rename.

public enum ProvisioningStatus
{
    Pending,
    Provisioning,
    Ready,
    Failed,
}

/// <summary>Provisioning steps, run strictly in this order (ONB-002).</summary>
public enum ProvisioningStep
{
    AllocateTenant,
    CreateOwnerMembership,
    CreateBillingAccount,
    AttachTrial,
    InitializeConfiguration,
    MarkReady,
}
