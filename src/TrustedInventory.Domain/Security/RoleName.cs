namespace TrustedInventory.Domain.Security;

/// <summary>Roles are named permission bundles (docs/permissions.md). Values match ti.role_name.</summary>
public enum RoleName
{
    PlatformAdministrator,
    TenantAdministrator,
    WarehouseManager,
    Receiver,
    Picker,
    Dispatcher,
    QaOfficer,
    InventoryApprover,
    Auditor,
    TelemetryIntegration,
}

public static class RoleBundles
{
    private static readonly Dictionary<RoleName, string[]> Bundles = new()
    {
        [RoleName.PlatformAdministrator] = [Permissions.PlatformTenantsManage, Permissions.PlatformHealthRead],
        [RoleName.TenantAdministrator] =
        [
            Permissions.TenantMembersManage, Permissions.TenantConfigManage, Permissions.CatalogManage,
            Permissions.CatalogRead, Permissions.WarehousesManage, Permissions.DevicesManage, Permissions.StockRead,
        ],
        [RoleName.WarehouseManager] =
        [
            Permissions.CatalogRead, Permissions.StockRead, Permissions.PurchaseOrdersCreate,
            Permissions.PurchaseOrdersApprove, Permissions.OrdersCreate, Permissions.OrdersAllocate,
            Permissions.TransfersManage, Permissions.CountsPerform, Permissions.CountsManage,
            Permissions.AdjustmentsRequest, Permissions.AdjustmentsPost, Permissions.ReturnsManage,
            Permissions.ApprovalsRequest, Permissions.TasksManage, Permissions.AlertsRead,
            Permissions.AlertsAcknowledge, Permissions.QualityHold,
        ],
        [RoleName.Receiver] =
        [
            Permissions.CatalogRead, Permissions.StockRead, Permissions.ReceiptsPost, Permissions.PutawayConfirm,
            Permissions.ReturnsManage, Permissions.CountsPerform,
        ],
        [RoleName.Picker] = [Permissions.CatalogRead, Permissions.StockRead, Permissions.PicksConfirm],
        [RoleName.Dispatcher] = [Permissions.CatalogRead, Permissions.StockRead, Permissions.DispatchPost],
        [RoleName.QaOfficer] =
        [
            Permissions.CatalogRead, Permissions.StockRead, Permissions.ApprovalsRequest,
            Permissions.ApprovalsSignQuality, Permissions.QualityRelease, Permissions.QualityHold,
            Permissions.ReturnsInspect, Permissions.RecallsManage, Permissions.RecallsClose,
            Permissions.AlertsRead, Permissions.AlertsAcknowledge,
        ],
        [RoleName.InventoryApprover] =
            [Permissions.CatalogRead, Permissions.StockRead, Permissions.ApprovalsSignInventory],
        [RoleName.Auditor] =
            [Permissions.CatalogRead, Permissions.StockRead, Permissions.AuditRead, Permissions.ExportsCreate, Permissions.AlertsRead],
        [RoleName.TelemetryIntegration] = [Permissions.TelemetryIngest],
    };

    public static IReadOnlyList<string> PermissionsOf(RoleName role) => Bundles[role];
}
