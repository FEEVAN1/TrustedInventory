namespace TrustedInventory.Api.Http;

/// <summary>
/// Swagger UI over the hand-written contract docs/openapi.yaml, in Development only (DEC-103). The contract is served as written
/// (DEC-003), except that its placeholder OpenID Connect discovery address is replaced with the configured identity provider, so
/// "Authorize" signs in through the development PWA client with PKCE and MFA like the web app. Nothing is generated from code, and
/// no other environment maps these routes: health probes stay the only unauthenticated API routes there (API-001).
/// </summary>
public static class ContractDocs
{
    public const string UiPrefix = "swagger";
    public const string ContractPath = "/openapi.yaml";
    private const string PlaceholderDiscovery = "https://identity.example.invalid/.well-known/openid-configuration";

    public static WebApplication UseContractDocs(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return app;

        var authority = app.Configuration["Auth:Authority"]!.TrimEnd('/');
        var contract = Load().Replace(PlaceholderDiscovery, $"{authority}/.well-known/openid-configuration", StringComparison.Ordinal);
        var identityOrigin = new Uri(authority).GetLeftPart(UriPartial.Authority);

        // The API's default policy forbids every page; the UI needs its own scripts and styles, inline styles that Swagger UI sets
        // on elements, and calls to the identity provider for discovery and the token exchange. Still no framing.
        var policy = $"default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self' {identityOrigin}; "
            + "frame-ancestors 'none'";
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/" + UiPrefix) || http.Request.Path.Equals(ContractPath))
                http.Response.Headers.ContentSecurityPolicy = policy;
            await next();
        });

        app.MapGet(ContractPath, () => Results.Text(contract, "application/yaml; charset=utf-8"));
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = UiPrefix;
            o.DocumentTitle = "TrustedInventory API contract (development)";
            o.SwaggerEndpoint(ContractPath, "TrustedInventory API (docs/openapi.yaml)");
            o.OAuthClientId(app.Configuration["ContractDocs:ClientId"] ?? "trustedinventory-pwa");
            o.OAuthScopes("openid");
            o.OAuthUsePkce();
            o.EnableTryItOutByDefault();
        });
        return app;
    }

    private static string Load()
    {
        using var stream = typeof(ContractDocs).Assembly.GetManifestResourceStream("openapi.yaml")
            ?? throw new InvalidOperationException("The contract docs/openapi.yaml is not embedded in the API assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
