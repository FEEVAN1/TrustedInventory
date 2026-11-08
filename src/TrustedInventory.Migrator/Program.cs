using TrustedInventory.Migrator;

// Usage:
//   migrate     applies db/migrations          needs TI_MIGRATOR_CONNECTION
//   bootstrap   development/test cluster setup needs TI_SUPERUSER_CONNECTION, TI_MIGRATOR_PASSWORD,
//                                                    TI_APP_PASSWORD, TI_PLATFORM_PASSWORD, TI_CONTROL_PASSWORD
//   seed        SYNTHETIC development data        needs TI_CONTROL_CONNECTION (never production)
var command = args.FirstOrDefault();
try
{
    switch (command)
    {
        case "migrate":
            await new MigrationRunner(Required("TI_MIGRATOR_CONNECTION"), Console.Out).ApplyAsync();
            Console.WriteLine($"schema at {MigrationRunner.LatestMigrationId}");
            return 0;
        case "bootstrap":
            await ClusterBootstrap.ApplyAsync(Required("TI_SUPERUSER_CONNECTION"), new Dictionary<string, string>
            {
                ["ti_migrator"] = Required("TI_MIGRATOR_PASSWORD"),
                ["ti_app"] = Required("TI_APP_PASSWORD"),
                ["ti_platform"] = Required("TI_PLATFORM_PASSWORD"),
                ["ti_control"] = Required("TI_CONTROL_PASSWORD"),
            }, Console.Out);
            return 0;
        case "seed":
            await DevSeed.ApplyAsync(Required("TI_CONTROL_CONNECTION"), Console.Out);
            return 0;
        case "seed-catalog":
            // SYNTHETIC fixture plans for development stacks (D09); path defaults to docs/entitlement-catalog.json.
            await CatalogSeed.ApplyAsync(Required("TI_CONTROL_CONNECTION"),
                args.ElementAtOrDefault(1) ?? Path.Combine("docs", "entitlement-catalog.json"), Console.Out);
            return 0;
        default:
            await Console.Error.WriteLineAsync("usage: TrustedInventory.Migrator migrate|bootstrap|seed|seed-catalog [catalog.json]");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Environment variable {name} is required.");
