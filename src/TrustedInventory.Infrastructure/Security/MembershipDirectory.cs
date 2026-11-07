using Npgsql;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Organization;
using TrustedInventory.Domain.Security;
using TrustedInventory.Infrastructure.Persistence;

namespace TrustedInventory.Infrastructure.Security;

/// <summary>
/// Calls the SECURITY DEFINER function ti.user_memberships with the validated token subject. It runs without a
/// tenant context and returns membership metadata only, never stock (docs/migration-design.md section 4).
/// </summary>
public sealed class MembershipDirectory(AppDataSource dataSource) : IMembershipDirectory
{
    public async Task<IReadOnlyList<MembershipRole>> ForSubjectAsync(string subject, CancellationToken ct)
    {
        await using var cmd = dataSource.Source.CreateCommand(
            "SELECT tenant_id, tenant_name, tenant_status, membership_id, role, warehouse_id FROM ti.user_memberships(@s)");
        cmd.Parameters.AddWithValue("s", subject);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<MembershipRole>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MembershipRole(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<TenantStatus>(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<RoleName>(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5)));
        }
        return rows;
    }
}
