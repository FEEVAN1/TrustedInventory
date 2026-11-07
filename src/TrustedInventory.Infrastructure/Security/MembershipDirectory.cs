using Npgsql;
using TrustedInventory.Application.Security;
using TrustedInventory.Domain.Saas;
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
        // Enum columns are read as text so new labels never depend on the pool's cached type mapping.
        await using var cmd = dataSource.Source.CreateCommand("""
            SELECT tenant_id, tenant_name, tenant_slug, lifecycle::text, security_suspended, membership_id, role::text, warehouse_id
            FROM ti.user_memberships_v2(@s)
            """);
        cmd.Parameters.AddWithValue("s", subject);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<MembershipRole>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MembershipRole(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<TenantLifecycle>(reader.GetString(3)),
                reader.GetBoolean(4),
                reader.GetGuid(5),
                reader.IsDBNull(6) ? null : Enum.Parse<RoleName>(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetGuid(7)));
        }
        return rows;
    }
}
