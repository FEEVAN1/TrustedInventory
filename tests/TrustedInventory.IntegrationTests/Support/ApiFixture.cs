using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Testcontainers.PostgreSql;
using TrustedInventory.Migrator;

namespace TrustedInventory.IntegrationTests.Support;

/// <summary>
/// Real PostgreSQL 16 (section 19: in-memory providers are not evidence), bootstrapped and migrated with the
/// production migrator, plus the API hosted in-process with the test token issuer.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private const string AppPassword = "test-app-password";
    private const string PlatformPassword = "test-platform-password";
    private const string MigratorPassword = "test-migrator-password";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("trustedinventory")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public string SuperuserConnection => _db.GetConnectionString();
    public string AppConnection => Connection("ti_app", AppPassword);
    public string PlatformConnection => Connection("ti_platform", PlatformPassword);
    public string MigratorConnection => Connection("ti_migrator", MigratorPassword);

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not started.");

    public SeedData Seed { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        await ClusterBootstrap.ApplyAsync(SuperuserConnection, new Dictionary<string, string>
        {
            ["ti_migrator"] = MigratorPassword,
            ["ti_app"] = AppPassword,
            ["ti_platform"] = PlatformPassword,
        }, TextWriter.Null);
        await new MigrationRunner(MigratorConnection, TextWriter.Null).ApplyAsync();
        Seed = await SeedData.CreateAsync(SuperuserConnection);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:App", AppConnection);
            web.UseSetting("ConnectionStrings:Platform", PlatformConnection);
            web.UseSetting("Paging:CursorKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
            web.UseSetting("Auth:Authority", TestTokens.Issuer);
            web.UseSetting("Auth:Audience", TestTokens.Audience);
            web.UseSetting("Platform:AdministratorSubjects:0", SeedData.PlatformAdmin);
            web.UseSetting("RateLimit:PermitsPerMinute", "100000");
            web.ConfigureServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    // Static metadata instead of discovery; every other validation rule stays as in production.
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                    o.Configuration.SigningKeys.Add(TestTokens.SigningKey);
                    o.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
                }));
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    /// <summary>HTTP client acting as <paramref name="subject"/> in <paramref name="tenantId"/> (null: no tenant header).</summary>
    public HttpClient Client(string subject, Guid? tenantId, bool mfa = true)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(subject, mfa));
        if (tenantId is { } t)
            client.DefaultRequestHeaders.Add("X-Tenant-Id", t.ToString());
        return client;
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    private string Connection(string user, string password) =>
        new NpgsqlConnectionStringBuilder(SuperuserConnection) { Username = user, Password = password }.ConnectionString;
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
