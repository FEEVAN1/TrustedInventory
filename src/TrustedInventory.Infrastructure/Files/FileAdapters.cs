using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrustedInventory.Application.Operations;

namespace TrustedInventory.Infrastructure.Files;

/// <summary>
/// SIMULATED object store (AGT-004): files under <c>Storage:Directory</c>, one sub-folder per tenant prefix. Keys must
/// start with a tenant id (TEN-005); paths are never built from anything else.
/// </summary>
public sealed class SimulatedFileObjectStore(string directory) : IObjectStore
{
    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task<int> DeleteTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var root = Path.Combine(directory, tenantId.ToString("D"));
        if (!Directory.Exists(root))
            return Task.FromResult(0);
        var count = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(root, recursive: true);
        return Task.FromResult(count);
    }

    private string PathFor(string key)
    {
        var parts = key.Split('/');
        if (parts.Length < 2 || !Guid.TryParse(parts[0], out _) || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Object keys are '<tenant id>/<name>' with safe path segments.", nameof(key));
        return Path.Combine([directory, .. parts]);
    }
}

/// <summary>
/// Token = base64url(expiry seconds) + "." + base64url(HMAC-SHA256(Storage:LinkKey, tenant|kind|id|subject|expiry))
/// (DEC-061). The expiry is visible but cannot be changed without the key.
/// </summary>
public sealed class HmacDownloadLinks : IDownloadLinks
{
    private readonly byte[] _key;

    public HmacDownloadLinks(byte[] key)
    {
        if (key.Length < 32)
            throw new ArgumentException("Storage:LinkKey must be at least 32 bytes.", nameof(key));
        _key = key;
    }

    public string Issue(Guid tenantId, DownloadKind kind, Guid resourceId, string subject, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeSeconds();
        return $"{Base64Url.EncodeToString(BitConverter.GetBytes(expiry))}.{Base64Url.EncodeToString(Mac(tenantId, kind, resourceId, subject, expiry))}";
    }

    public bool IsValid(string token, Guid tenantId, DownloadKind kind, Guid resourceId, string subject, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 2)
            return false;
        try
        {
            var expiryBytes = Base64Url.DecodeFromChars(parts[0]);
            if (expiryBytes.Length != sizeof(long))
                return false;
            var expiry = BitConverter.ToInt64(expiryBytes);
            if (DateTimeOffset.FromUnixTimeSeconds(expiry) <= now)
                return false;
            return CryptographicOperations.FixedTimeEquals(Base64Url.DecodeFromChars(parts[1]), Mac(tenantId, kind, resourceId, subject, expiry));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] Mac(Guid tenantId, DownloadKind kind, Guid resourceId, string subject, long expiry) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{tenantId:D}|{kind}|{resourceId:D}|{subject}|{expiry}"));
}

/// <summary>SIMULATED malware scanner (AGT-004): only the EICAR test string is "infected".</summary>
public sealed class SimulatedAttachmentScanner : IAttachmentScanner
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public Task<bool> IsCleanAsync(byte[] content, CancellationToken ct) => Task.FromResult(content.AsSpan().IndexOf(Eicar) < 0);
}

public static class FileRegistration
{
    /// <summary>Object store, download links and scanner. Both settings are required (fail closed, section 18).</summary>
    public static IServiceCollection AddFileInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IObjectStore>(new SimulatedFileObjectStore(DependencyInjection.Required(configuration, "Storage:Directory")));
        services.AddSingleton<IDownloadLinks>(new HmacDownloadLinks(Convert.FromBase64String(DependencyInjection.Required(configuration, "Storage:LinkKey"))));
        services.AddSingleton<IAttachmentScanner, SimulatedAttachmentScanner>();
        services.AddSingleton<TrustedInventory.Application.Billing.IInvoiceDocuments, PdfInvoiceDocuments>();
        return services;
    }
}
