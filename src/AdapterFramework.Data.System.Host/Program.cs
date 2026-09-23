using AdapterFramework.Data.Adapter.HomeAssistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------------------------
// MINIMAL LOCAL HOST (mirrors the sample's AdapterFramework.Data.System.Host role: "runs the
// adapter locally for development"). This bootstrap intentionally does NOT yet integrate the real
// AdapterFramework.Data.Framework.Host package (component registration, the /api/v1/configuration
// REST surface on port 5590, Windows Service/systemd lifecycle, PersistentQueue-backed OMF egress).
// See prep notes §2 and the README "Verify before building" section for what to wire in next -
// the pattern should follow the WeatherGovAdapter sample's own Program.cs closely once you have
// the restored NuGet packages to reference against.
// ---------------------------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    Environment.GetEnvironmentVariable("HA_ADAPTER_DATASOURCE_CONFIG")
        ?? "../../ConfigurationExamples/HomeAssistant.DataSource.json",
    optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "HA_ADAPTER_");

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host");
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

var config = new DataSourceConfiguration();
host.Services.GetRequiredService<IConfiguration>().Bind(config);

var validationErrors = config.Validate();
if (validationErrors.Count > 0)
{
    foreach (var err in validationErrors) logger.LogError("Configuration error: {Error}", err);
    logger.LogError("Exiting due to invalid configuration. See ConfigurationExamples/HomeAssistant.DataSource.json for a template.");
    return 1;
}

await using var adapter = new HomeAssistantAdapter(config, loggerFactory)
{
    // Placeholder sink: replace with a call into the framework's OMF ingestion pipeline
    // (see HomeAssistantAdapter's class-level remarks and prep notes §9.1).
    OnPointValue = value =>
    {
        logger.LogInformation(
            "{PointId} @ {Timestamp:o} = {Value}",
            value.PointId, value.Timestamp,
            value.ValueType switch
            {
                HomeAssistantValueType.Double => value.DoubleValue,
                HomeAssistantValueType.Boolean => value.BooleanValue,
                _ => value.StringValue
            });
        return Task.CompletedTask;
    }
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (args.Length > 0 && args[0] == "discover")
{
    var query = args.Length > 1 ? args[1] : string.Empty;
    var result = await adapter.RunDiscoveryAsync(query, autoSelect: true, cts.Token);
    logger.LogInformation("Discovery complete: {Count} points found.", result.Items.Count);
    foreach (var item in result.Items)
    {
        Console.WriteLine($"{item.PointId}\t{item.ValueType}\t{item.UnitOfMeasurement}");
    }
    return 0;
}

// TODO: load the persisted DataSelection (ConfigurationExamples/HomeAssistant.DataSelection.json
// for local dev) instead of an empty list once the ConfigurationProvider integration is wired in.
// Note: DataSelection normally holds every discovered candidate point, selected or not - only
// items with Selected == true are actually collected (filtering happens inside HomeAssistantAdapter).
var dataSelection = new List<HomeAssistantDataSelectionItem>();

await adapter.StartAsync(dataSelection, checkpointFilePath: "checkpoint.txt", cts.Token);
logger.LogInformation("Home Assistant adapter running. Press Ctrl+C to exit.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // expected on shutdown
}

return 0;
