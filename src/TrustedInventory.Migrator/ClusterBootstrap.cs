using Npgsql;

namespace TrustedInventory.Migrator;

/// <summary>
/// Development and test convenience: runs db/bootstrap as a superuser and sets role passwords.
/// Production clusters are bootstrapped by the platform operator (docs/operations.md).
/// </summary>
public static class ClusterBootstrap
{
    public static async Task ApplyAsync(string superuserConnectionString, IReadOnlyDictionary<string, string> rolePasswords,
        TextWriter log, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(superuserConnectionString);
        await conn.OpenAsync(ct);

        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ti_owner')", conn))
        {
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
            {
                await using var cmd = new NpgsqlCommand(SqlScripts.ClusterBootstrap.Sql, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                await log.WriteLineAsync("cluster bootstrap applied");
            }
        }

        foreach (var (role, password) in rolePasswords)
        {
            if (role is not ("ti_migrator" or "ti_app" or "ti_platform"))
                throw new ArgumentException($"Unexpected login role {role}.", nameof(rolePasswords));
            // Identifiers come from the allowlist above; the password is a quoted literal.
            await using var cmd = new NpgsqlCommand(
                $"ALTER ROLE {role} PASSWORD {QuoteLiteral(password)}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
