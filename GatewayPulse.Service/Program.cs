using GatewayPulse.Core;
using GatewayPulse.PowerMonitoring;
using GatewayPulse.RfMonitoring;
using GatewayPulse.ServiceHosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "GatewayPulse";
});

builder.Services.Configure<GatewayPulseOptions>(builder.Configuration.GetSection("GatewayPulse"));
builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection("Pushover"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.Configure<NetworkMapOptions>(builder.Configuration.GetSection("NetworkMap"));

var demoMode = builder.Configuration.GetValue("Dashboard:DemoMode", false);
var configuredTelemetryPath = builder.Configuration["PowerMonitoring:TelemetryPath"] ?? "PowerTelemetry.json";
var telemetryPath = Path.IsPathRooted(configuredTelemetryPath)
    ? configuredTelemetryPath
    : Path.Combine(builder.Environment.ContentRootPath, configuredTelemetryPath);
var configuredHistoryPath = builder.Configuration["PowerMonitoring:HistoryPath"];
var historyPath = string.IsNullOrWhiteSpace(configuredHistoryPath)
    ? Path.Combine(Path.GetDirectoryName(telemetryPath) ?? builder.Environment.ContentRootPath, "PowerHistory.json")
    : (Path.IsPathRooted(configuredHistoryPath)
        ? configuredHistoryPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredHistoryPath));
var telemetryStaleSeconds = builder.Configuration.GetValue("PowerMonitoring:StaleAfterSeconds", 30);
var historySampleSeconds = builder.Configuration.GetValue("PowerMonitoring:HistorySampleSeconds", PowerHistoryStore.DefaultMinSampleSeconds);
var powerThresholds = builder.Configuration.GetSection("VictronMonitor:Thresholds").Get<PowerThresholds>();
builder.Services.AddSingleton<IPowerMonitor>(
    new JsonFilePowerMonitor(
        telemetryPath,
        staleAfter: TimeSpan.FromSeconds(telemetryStaleSeconds),
        thresholds: powerThresholds,
        allowMockProvider: demoMode));
builder.Services.AddSingleton(new PowerHistoryStore(
    historyPath,
    minSampleInterval: TimeSpan.FromSeconds(Math.Max(5, historySampleSeconds))));
builder.Services.AddHostedService(provider =>
    new PowerHistoryCollector(
        provider.GetRequiredService<IPowerMonitor>(),
        provider.GetRequiredService<PowerHistoryStore>(),
        provider.GetRequiredService<ILogger<PowerHistoryCollector>>(),
        interval: TimeSpan.FromSeconds(Math.Max(5, historySampleSeconds))));

var configuredRfTelemetryPath = builder.Configuration["RfMonitoring:TelemetryPath"]
    ?? builder.Configuration["Lp100Monitor:OutputPath"]
    ?? @"C:\PWM\RfTelemetry.json";
var rfTelemetryPath = Path.IsPathRooted(configuredRfTelemetryPath)
    ? configuredRfTelemetryPath
    : Path.Combine(builder.Environment.ContentRootPath, configuredRfTelemetryPath);
var configuredRfHistoryPath = builder.Configuration["RfMonitoring:HistoryPath"];
var rfHistoryPath = string.IsNullOrWhiteSpace(configuredRfHistoryPath)
    ? Path.Combine(Path.GetDirectoryName(rfTelemetryPath) ?? builder.Environment.ContentRootPath, "RfHistory.json")
    : (Path.IsPathRooted(configuredRfHistoryPath)
        ? configuredRfHistoryPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredRfHistoryPath));
var rfStaleSeconds = builder.Configuration.GetValue("RfMonitoring:StaleAfterSeconds", 10);
var rfHistorySampleSeconds = builder.Configuration.GetValue("RfMonitoring:HistorySampleSeconds", RfHistoryStore.DefaultMinSampleSeconds);
builder.Services.AddSingleton<IRfMonitor>(
    new JsonFileRfMonitor(
        rfTelemetryPath,
        staleAfter: TimeSpan.FromSeconds(Math.Max(2, rfStaleSeconds)),
        allowMockProvider: demoMode));
builder.Services.AddSingleton(new RfHistoryStore(
    rfHistoryPath,
    minSampleInterval: TimeSpan.FromSeconds(Math.Max(2, rfHistorySampleSeconds))));
var configuredTxHistoryPath = builder.Configuration["RfMonitoring:TransmissionHistoryPath"];
var txHistoryPath = string.IsNullOrWhiteSpace(configuredTxHistoryPath)
    ? Path.Combine(Path.GetDirectoryName(rfTelemetryPath) ?? builder.Environment.ContentRootPath, "RfTransmissionHistory.json")
    : (Path.IsPathRooted(configuredTxHistoryPath)
        ? configuredTxHistoryPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredTxHistoryPath));
builder.Services.AddSingleton(new RfTransmissionHistoryStore(txHistoryPath));
builder.Services.AddHostedService(provider =>
    new RfHistoryCollector(
        provider.GetRequiredService<IRfMonitor>(),
        provider.GetRequiredService<RfHistoryStore>(),
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Lp100MonitorOptions>>(),
        provider.GetRequiredService<ILogger<RfHistoryCollector>>()));

builder.Services.AddSingleton<GatewayPulseService>();
builder.Services.AddSingleton<PushoverService>();
builder.Services.AddVictronMonitorSupervision(builder.Configuration);
builder.Services.AddLp100MonitorSupervision(builder.Configuration);

var app = builder.Build();
var appsettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var isSensitiveApi =
        context.Request.Path.StartsWithSegments("/api/settings") ||
        context.Request.Path.StartsWithSegments("/api/testalert") ||
        context.Request.Path.StartsWithSegments("/api/rf/test-connection");
    if (isSensitiveApi && !LocalRequestPolicy.IsAllowed(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Settings and test-alert APIs are available only from the gateway PC."
        });
        return;
    }

    await next(context);
});

app.MapGet("/api/status", (GatewayPulseService pulse, Microsoft.Extensions.Options.IOptions<DashboardOptions> dashboard) =>
{
    var status = pulse.GetStatus();
    status.DemoMode = dashboard.Value.DemoMode;
    return Results.Json(status);
});

app.MapGet("/api/power", async (IPowerMonitor powerMonitor, PowerHistoryStore historyStore) =>
{
    var telemetry = await powerMonitor.GetTelemetryAsync();
    // Opportunistic sample on live reads keeps history moving even if the collector is delayed.
    historyStore.Record(telemetry);
    return Results.Json(telemetry);
});

app.MapGet("/api/power/history", (
    PowerHistoryStore historyStore,
    string? metric,
    string? range) =>
{
    if (!PowerHistoryStore.TryParseMetric(string.IsNullOrWhiteSpace(metric) ? "soc" : metric, out var parsedMetric))
    {
        return Results.BadRequest(new { error = "Unsupported metric. Use soc, voltage, current, watts, or runtime." });
    }

    if (!PowerHistoryStore.TryParseRange(string.IsNullOrWhiteSpace(range) ? "24h" : range, out var parsedRange))
    {
        return Results.BadRequest(new { error = "Unsupported range. Use 1h, 6h, 12h, 24h, 48h, 5d, 10d, 14d, 20d, 30d, 40d, 60d, 80d, 90d, 160d, or 360d." });
    }

    return Results.Json(historyStore.Query(parsedMetric, parsedRange));
});

app.MapGet("/api/rf", async (
    IRfMonitor rfMonitor,
    RfHistoryStore rfHistoryStore,
    RfTransmissionHistoryStore txHistoryStore,
    RfTransmissionMonitor txMonitor) =>
{
    var telemetry = await rfMonitor.GetTelemetryAsync();
    rfHistoryStore.Record(telemetry);
    return Results.Json(new
    {
        telemetry,
        activeTransmission = txMonitor.ActiveTransmission,
        transmissions = txHistoryStore.List(40)
    });
});

app.MapGet("/api/rf/transmissions", (RfTransmissionHistoryStore txHistoryStore, int? take) =>
    Results.Json(new { transmissions = txHistoryStore.List(take ?? 100) }));

app.MapGet("/api/rf/history", (
    RfHistoryStore rfHistoryStore,
    string? metric,
    string? range) =>
{
    if (!RfHistoryStore.TryParseMetric(string.IsNullOrWhiteSpace(metric) ? "forward" : metric, out var parsedMetric))
        return Results.BadRequest(new { error = "Unsupported metric. Use forward, peak, reflected, or swr." });
    if (!RfHistoryStore.TryParseRange(string.IsNullOrWhiteSpace(range) ? "1h" : range, out _))
        return Results.BadRequest(new { error = "Unsupported range. Use 15m, 1h, 6h, 24h, or 7d." });
    return Results.Json(rfHistoryStore.Query(parsedMetric, range ?? "1h"));
});

app.MapPost("/api/rf/test-connection", async (
    IRfMonitor rfMonitor,
    IHostEnvironment environment,
    Microsoft.Extensions.Options.IOptionsMonitor<Lp100MonitorOptions> lpOptions) =>
{
    var options = lpOptions.CurrentValue;

    // If the collector already owns the COM port, report live telemetry instead of opening a second handle.
    if (options.Enabled)
    {
        var live = await rfMonitor.GetTelemetryAsync();
        return Results.Json(new
        {
            ok = live.Connected,
            telemetry = live,
            mode = "live-collector"
        });
    }

    ProcessStartInfo startInfo;
    try
    {
        startInfo = Lp100MonitorLaunchSpec.Create(options, environment.ContentRootPath, demoMode: false);
        startInfo.ArgumentList.Add("--test");
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }

    if (!File.Exists(startInfo.FileName))
        return Results.BadRequest(new { ok = false, error = "LP-100A monitor executable is not installed." });

    using var process = Process.Start(startInfo);
    if (process is null)
        return Results.Json(new { ok = false, error = "Unable to start LP-100A test process." });

    var stdout = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    RfTelemetry? telemetry = null;
    try { telemetry = RfTelemetryJson.Deserialize(stdout); } catch { }

    return Results.Json(new
    {
        ok = process.ExitCode == 0 && telemetry?.Connected == true,
        exitCode = process.ExitCode,
        telemetry,
        mode = "oneshot"
    });
});

app.MapPost("/api/testalert", async (PushoverService pushover) =>
{
    var ok = await pushover.SendAsync(
        "Gateway Pulse Test",
        "Gateway Pulse is successfully connected to Pushover.");

    return Results.Json(new { ok });
});

app.MapPost("/api/testalert/settings", async (PushoverSettingsEditModel settings, PushoverService pushover) =>
{
    var ok = await pushover.SendAsync(
        "Gateway Pulse Test",
        "Gateway Pulse is successfully connected to Pushover.",
        new PushoverOptions
        {
            Enabled = true,
            UserKey = settings.UserKey.Trim(),
            ApiToken = settings.ApiToken.Trim()
        });

    return Results.Json(new { ok });
});

app.MapGet("/api/settings/alerts", (IConfiguration configuration) =>
{
    var alerts = configuration.GetSection("Alerts").Get<AlertOptions>() ?? new AlertOptions();
    return Results.Json(alerts);
});

app.MapPost("/api/settings/alerts", async (AlertOptions alerts) =>
{
    if (!File.Exists(appsettingsPath))
        return Results.NotFound(new { error = "appsettings.json was not found." });

    var json = await File.ReadAllTextAsync(appsettingsPath);
    var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

    root["Alerts"] = JsonSerializer.SerializeToNode(alerts, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    await File.WriteAllTextAsync(appsettingsPath, root.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    return Results.Json(alerts);
});

app.MapGet("/api/preferences", (IConfiguration configuration) =>
{
    var preferences = DashboardPreferences.Normalize(
        configuration.GetSection("Dashboard:Preferences").Get<DashboardPreferences>());
    return Results.Json(preferences);
});

app.MapGet("/api/network-map", (IConfiguration configuration) =>
{
    var options = NetworkMapOptions.Normalize(
        configuration.GetSection("NetworkMap").Get<NetworkMapOptions>());
    return Results.Json(new
    {
        options.ServiceCode,
        options.RememberServiceCode,
        options.AutoRefresh,
        options.AutoRefreshMinutes,
        options.AutoOpenInBrowser,
        options.MapUrl,
        ResolvedMapUrl = NetworkMapOptions.BuildMapUrl(options.MapUrl, options.ServiceCode)
    });
});

app.MapGet("/api/settings", (IConfiguration configuration) =>
{
    var settings = new AppSettingsEditModel
    {
        GatewayPulse = configuration.GetSection("GatewayPulse").Get<GatewayPulseSettingsEditModel>() ?? new GatewayPulseSettingsEditModel(),
        Pushover = configuration.GetSection("Pushover").Get<PushoverSettingsEditModel>() ?? new PushoverSettingsEditModel(),
        Alerts = configuration.GetSection("Alerts").Get<AlertOptions>() ?? new AlertOptions(),
        Preferences = DashboardPreferences.Normalize(
            configuration.GetSection("Dashboard:Preferences").Get<DashboardPreferences>()),
        NetworkMap = NetworkMapOptions.Normalize(
            configuration.GetSection("NetworkMap").Get<NetworkMapOptions>()),
        Lp100Monitor = configuration.GetSection("Lp100Monitor").Get<Lp100MonitorOptions>() ?? new Lp100MonitorOptions()
    };

    return Results.Json(settings);
});

app.MapPost("/api/settings", async (AppSettingsEditModel settings) =>
{
    if (!File.Exists(appsettingsPath))
        return Results.NotFound(new { error = "appsettings.json was not found." });

    var json = await File.ReadAllTextAsync(appsettingsPath);
    var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

    var gatewayPulse = root["GatewayPulse"] as JsonObject;
    if (gatewayPulse is null)
    {
        gatewayPulse = new JsonObject();
        root["GatewayPulse"] = gatewayPulse;
    }

    gatewayPulse["GatewayName"] = settings.GatewayPulse.GatewayName.Trim();
    gatewayPulse["Callsign"] = settings.GatewayPulse.Callsign.Trim().ToUpperInvariant();

    var radioCat = settings.GatewayPulse.RadioCat ?? new RadioCatOptions();
    if (radioCat.Port <= 0) radioCat.Port = 4532;
    if (radioCat.TimeoutMs < 100) radioCat.TimeoutMs = 400;
    if (string.IsNullOrWhiteSpace(radioCat.Host)) radioCat.Host = "127.0.0.1";
    gatewayPulse["RadioCat"] = JsonSerializer.SerializeToNode(radioCat, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    var pushover = root["Pushover"] as JsonObject;
    if (pushover is null)
    {
        pushover = new JsonObject();
        root["Pushover"] = pushover;
    }

    pushover["Enabled"] = settings.Pushover.Enabled;
    pushover["UserKey"] = settings.Pushover.UserKey.Trim();
    pushover["ApiToken"] = settings.Pushover.ApiToken.Trim();

    root["Alerts"] = JsonSerializer.SerializeToNode(settings.Alerts, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    var dashboard = root["Dashboard"] as JsonObject;
    if (dashboard is null)
    {
        dashboard = new JsonObject();
        root["Dashboard"] = dashboard;
    }

    settings.Preferences = DashboardPreferences.Normalize(settings.Preferences);
    dashboard["Preferences"] = JsonSerializer.SerializeToNode(settings.Preferences, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    settings.NetworkMap = NetworkMapOptions.ForPersistence(settings.NetworkMap);
    root["NetworkMap"] = JsonSerializer.SerializeToNode(settings.NetworkMap, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    var lp = settings.Lp100Monitor ?? new Lp100MonitorOptions();
    if (lp.BaudRate <= 0) lp.BaudRate = 115200;
    if (lp.IntervalMs < 100) lp.IntervalMs = 250;
    if (lp.IdleIntervalMs < 250) lp.IdleIntervalMs = 1000;
    if (lp.RestartDelaySeconds < 1) lp.RestartDelaySeconds = 10;
    if (lp.TxThresholdWatts <= 0) lp.TxThresholdWatts = 0.05m;
    if (lp.TxEndDebounceMs < 100) lp.TxEndDebounceMs = 750;
    lp.Port = (lp.Port ?? "").Trim().ToUpperInvariant();
    lp.Alerts ??= new Lp100AlertOptions();
    root["Lp100Monitor"] = JsonSerializer.SerializeToNode(lp, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    var rfMonitoring = root["RfMonitoring"] as JsonObject ?? new JsonObject();
    root["RfMonitoring"] = rfMonitoring;
    rfMonitoring["TelemetryPath"] = string.IsNullOrWhiteSpace(lp.OutputPath) ? @"C:\PWM\RfTelemetry.json" : lp.OutputPath;
    if (rfMonitoring["HistoryPath"] is null)
        rfMonitoring["HistoryPath"] = @"C:\PWM\RfHistory.json";
    if (rfMonitoring["TransmissionHistoryPath"] is null)
        rfMonitoring["TransmissionHistoryPath"] = @"C:\PWM\RfTransmissionHistory.json";
    if (rfMonitoring["StaleAfterSeconds"] is null)
        rfMonitoring["StaleAfterSeconds"] = 10;

    await File.WriteAllTextAsync(appsettingsPath, root.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    }));

    return Results.Json(settings);
});

app.Run();

public sealed class AppSettingsEditModel
{
    public GatewayPulseSettingsEditModel GatewayPulse { get; set; } = new();
    public PushoverSettingsEditModel Pushover { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public DashboardPreferences Preferences { get; set; } = DashboardPreferences.CreateDefaults();
    public NetworkMapOptions NetworkMap { get; set; } = NetworkMapOptions.CreateDefaults();
    public Lp100MonitorOptions Lp100Monitor { get; set; } = new();
}

public sealed class GatewayPulseSettingsEditModel
{
    public string GatewayName { get; set; } = "";
    public string Callsign { get; set; } = "";
    public RadioCatOptions RadioCat { get; set; } = new();
}

public sealed class PushoverSettingsEditModel
{
    public bool Enabled { get; set; }
    public string UserKey { get; set; } = "";
    public string ApiToken { get; set; } = "";
}
