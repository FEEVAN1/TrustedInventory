using TrustedInventory.Application.Common;
using TrustedInventory.Application.Errors;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;

namespace TrustedInventory.UnitTests;

public class CallerContextTests
{
    private static readonly Guid W1 = Guid.NewGuid();
    private static readonly Guid W2 = Guid.NewGuid();

    private static CallerContext With(params (RoleName, Guid?)[] roles) =>
        new("user", ActorType.User, Guid.NewGuid(), TenantStatus.Active, CallerContext.GrantsFrom(roles));

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public void Warehouse_scoped_grant_limits_only_warehouse_permissions()
    {
        var caller = With((RoleName.WarehouseManager, W1));
        Assert.True(caller.CanAccess(Permissions.StockRead, W1));
        Assert.False(caller.CanAccess(Permissions.StockRead, W2));
        Assert.Throws<AppException>(() => caller.RequireTenantWide(Permissions.StockRead));
        // Tenant-level permissions in the same bundle are not narrowed by the warehouse.
        caller.RequireTenantWide(Permissions.ApprovalsRequest);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public void Tenant_wide_grant_wins_over_a_scoped_grant_of_the_same_permission()
    {
        var caller = With((RoleName.WarehouseManager, W1), (RoleName.Receiver, null));
        Assert.True(caller.CanAccess(Permissions.StockRead, W2));
        Assert.Null(caller.WarehouseFilter(Permissions.StockRead));
        Assert.Equal([W1], caller.WarehouseFilter(Permissions.OrdersCreate)!);
    }

    [Fact]
    [Trait("Requirement", "IAM-001")]
    public void Deny_by_default_without_grants()
    {
        var caller = With();
        var ex = Assert.Throws<AppException>(() => caller.Require(Permissions.CatalogRead));
        Assert.Equal(ErrorCode.Forbidden, ex.Code);
    }

    [Fact]
    [Trait("Requirement", "SEC-SUPPORT")]
    public void Suspended_tenant_rejects_mutations()
    {
        var caller = new CallerContext("user", ActorType.User, Guid.NewGuid(), TenantStatus.Suspended,
            CallerContext.GrantsFrom([(RoleName.TenantAdministrator, null)]));
        Assert.Throws<AppException>(caller.RequireMutable);
    }

    [Fact]
    [Trait("Requirement", "SEC-SUPPORT")]
    public void Platform_administrator_bundle_has_no_tenant_or_stock_permission()
    {
        Assert.All(RoleBundles.PermissionsOf(RoleName.PlatformAdministrator),
            p => Assert.Equal(PermissionScope.Platform, Permissions.Scopes[p]));
    }

    [Fact]
    [Trait("Requirement", "DEC-007")]
    public void Every_bundled_permission_is_in_the_catalogue()
    {
        foreach (var role in Enum.GetValues<RoleName>())
            Assert.All(RoleBundles.PermissionsOf(role), p => Assert.True(Permissions.Scopes.ContainsKey(p), p));
    }

    [Theory]
    [Trait("Requirement", "DEC-014")]
    [InlineData("1", true)]
    [InlineData("0.000001", true)]
    [InlineData("99999999999999.999999", true)]
    [InlineData("0", false)]
    [InlineData("1.0000001", false)]
    [InlineData("100000000000000", false)]
    [InlineData("01", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    [InlineData(" 1", false)]
    public void Positive_quantity_format(string raw, bool valid)
    {
        Assert.Equal(valid, Quantity.IsValidPositive(Quantity.Parse(raw)));
    }

    [Fact]
    [Trait("Requirement", "DEC-014")]
    public void Quantity_formats_without_exponent_or_trailing_zeros()
    {
        Assert.Equal("12.5", Quantity.Format(12.500m));
        Assert.Equal("0.000001", Quantity.Format(0.000001m));
        Assert.Equal("100", Quantity.Format(100m));
    }
}
