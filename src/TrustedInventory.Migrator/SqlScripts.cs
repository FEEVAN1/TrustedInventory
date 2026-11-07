using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TrustedInventory.Migrator;

/// <summary>A reviewed SQL script embedded in this assembly.</summary>
public sealed record SqlScript(string Id, string Sql)
{
    public string Checksum { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

public static class SqlScripts
{
    public static IReadOnlyList<SqlScript> Migrations => Load("migrations/");

    public static SqlScript ClusterBootstrap => Load("bootstrap/").Single();

    private static List<SqlScript> Load(string prefix)
    {
        var assembly = typeof(SqlScripts).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => new SqlScript(Path.GetFileNameWithoutExtension(n[prefix.Length..]), Read(assembly, n)))
            .ToList();
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Normalise line endings so the checksum does not depend on the checkout platform.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
