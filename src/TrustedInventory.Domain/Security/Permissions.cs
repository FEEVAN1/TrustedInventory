namespace TrustedInventory.Domain.Security;

public enum PermissionScope
{
    Platform,
    Tenant,
    /// <summary>Limited by a role grant's warehouse_id when present (docs/permissions.md "W").</summary>
    Warehouse,
    Device,
}

/// <summary>Permission catalogue (docs/permissions.md, DEC-007). Authorization never checks role names.</summary>
public static class Permissions
{
    public const string PlatformTenantsManage = "platform.tenants.manage";
    public const string PlatformHealthRead = "platform.health.read";
    public const string TenantMembersManage = "tenant.members.manage";
    public const string TenantConfigManage = "tenant.config.manage";
    public const string CatalogManage = "catalog.manage";
    public const string CatalogRead = "catalog.read";
    public const string WarehousesManage = "warehouses.manage";
    public const string StockRead = "stock.read";
    public const string PurchaseOrdersCreate = "purchase-orders.create";
    public const string PurchaseOrdersApprove = "purchase-orders.approve";
    public const string ReceiptsPost = "receipts.post";
    public const string PutawayConfirm = "putaway.confirm";
    public const string OrdersCreate = "orders.create";
    public const string OrdersAllocate = "orders.allocate";
    public const string PicksConfirm = "picks.confirm";
    public const string DispatchPost = "dispatch.post";
    public const string TransfersManage = "transfers.manage";
    public const string CountsPerform = "counts.perform";
    public const string CountsManage = "counts.manage";
    public const string AdjustmentsRequest = "adjustments.request";
    public const string AdjustmentsPost = "adjustments.post";
    public const string ReturnsManage = "returns.manage";
    public const string ReturnsInspect = "returns.inspect";
    public const string ApprovalsRequest = "approvals.request";
    public const string ApprovalsSignInventory = "approvals.sign.inventory";
    public const string ApprovalsSignQuality = "approvals.sign.quality";
    public const string QualityRelease = "quality.release";
    public const string QualityHold = "quality.hold";
    public const string RecallsManage = "recalls.manage";
    public const string RecallsClose = "recalls.close";
    public const string TasksManage = "tasks.manage";
    public const string AlertsRead = "alerts.read";
    public const string AlertsAcknowledge = "alerts.acknowledge";
    public const string DevicesManage = "devices.manage";
    public const string TelemetryIngest = "telemetry.ingest";
    public const string AuditRead = "audit.read";
    public const string ExportsCreate = "exports.create";

    public static readonly IReadOnlyDictionary<string, PermissionScope> Scopes = new Dictionary<string, PermissionScope>
    {
        [PlatformTenantsManage] = PermissionScope.Platform,
        [PlatformHealthRead] = PermissionScope.Platform,
        [TenantMembersManage] = PermissionScope.Tenant,
        [TenantConfigManage] = PermissionScope.Tenant,
        [CatalogManage] = PermissionScope.Tenant,
        [CatalogRead] = PermissionScope.Tenant,
        [WarehousesManage] = PermissionScope.Warehouse,
        [StockRead] = PermissionScope.Warehouse,
        [PurchaseOrdersCreate] = PermissionScope.Warehouse,
        [PurchaseOrdersApprove] = PermissionScope.Warehouse,
        [ReceiptsPost] = PermissionScope.Warehouse,
        [PutawayConfirm] = PermissionScope.Warehouse,
        [OrdersCreate] = PermissionScope.Warehouse,
        [OrdersAllocate] = PermissionScope.Warehouse,
        [PicksConfirm] = PermissionScope.Warehouse,
        [DispatchPost] = PermissionScope.Warehouse,
        [TransfersManage] = PermissionScope.Warehouse,
        [CountsPerform] = PermissionScope.Warehouse,
        [CountsManage] = PermissionScope.Warehouse,
        [AdjustmentsRequest] = PermissionScope.Warehouse,
        [AdjustmentsPost] = PermissionScope.Warehouse,
        [ReturnsManage] = PermissionScope.Warehouse,
        [ReturnsInspect] = PermissionScope.Warehouse,
        [ApprovalsRequest] = PermissionScope.Tenant,
        [ApprovalsSignInventory] = PermissionScope.Tenant,
        [ApprovalsSignQuality] = PermissionScope.Tenant,
        [QualityRelease] = PermissionScope.Warehouse,
        [QualityHold] = PermissionScope.Warehouse,
        [RecallsManage] = PermissionScope.Tenant,
        [RecallsClose] = PermissionScope.Tenant,
        [TasksManage] = PermissionScope.Warehouse,
        [AlertsRead] = PermissionScope.Warehouse,
        [AlertsAcknowledge] = PermissionScope.Warehouse,
        [DevicesManage] = PermissionScope.Tenant,
        [TelemetryIngest] = PermissionScope.Device,
        [AuditRead] = PermissionScope.Tenant,
        [ExportsCreate] = PermissionScope.Tenant,
    };
}
