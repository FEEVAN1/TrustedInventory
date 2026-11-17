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

        // Each bootstrap script creates roles; the first role it creates marks it as applied.
        foreach (var script in SqlScripts.ClusterBootstrap)
        {
            var marker = script.Id switch
            {
                "0000_cluster" => "ti_owner",
                "0001_control_plane" => "ti_control",
                "0002_purge" => "ti_purger",
                "0003_metrics" => "ti_metrics_reader",
                _ => throw new InvalidOperationException($"Bootstrap script {script.Id} has no applied-marker role."),
            };
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @r)", conn);
            exists.Parameters.AddWithValue("r", marker);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!)
                continue;
            await using var cmd = new NpgsqlCommand(script.Sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
            await log.WriteLineAsync($"cluster bootstrap {script.Id} applied");
        }

        foreach (var (role, password) in rolePasswords)
        {
            if (role is not ("ti_migrator" or "ti_app" or "ti_platform" or "ti_control"))
                throw new ArgumentException($"Unexpected login role {role}.", nameof(rolePasswords));
            // Identifiers come from the allowlist above; the password is a quoted literal.
            await using var cmd = new NpgsqlCommand(
                $"ALTER ROLE {role} PASSWORD {QuoteLiteral(password)}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
