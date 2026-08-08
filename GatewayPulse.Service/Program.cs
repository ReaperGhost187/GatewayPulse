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

// A single BackgroundService fault must never take down Kestrel / the Windows service.
// RadioCat CI-V COM hangs and collector supervisor errors are recovered in-process.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// Upgrade installs preserve existing appsettings.json. If an older file lost `urls`,
// still bind the dashboard on 8080 so tray health checks keep working.
var configuredUrls = builder.Configuration["urls"] ?? builder.Configuration["Urls"];
if (string.IsNullOrWhiteSpace(configuredUrls))
    builder.WebHost.UseUrls("http://0.0.0.0:8080");

builder.Services.Configure<GatewayPulseOptions>(builder.Configuration.GetSection("GatewayPulse"));
builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection("Pushover"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.Configure<NetworkMapOptions>(builder.Configuration.GetSection("NetworkMap"));
builder.Services.AddMobileApiAuth(builder.Configuration);

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
var configuredRfAnalysisPath = builder.Configuration["RfMonitoring:AnalysisPath"];
var rfAnalysisPath = string.IsNullOrWhiteSpace(configuredRfAnalysisPath)
    ? Path.Combine(Path.GetDirectoryName(rfTelemetryPath) ?? builder.Environment.ContentRootPath, "RfAnalysis.json")
    : (Path.IsPathRooted(configuredRfAnalysisPath)
        ? configuredRfAnalysisPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredRfAnalysisPath));
builder.Services.AddSingleton(new RfAnalysisStore(rfAnalysisPath));
var configuredSwrByFreqPath = builder.Configuration["RfMonitoring:SwrByFrequencyPath"];
var swrByFreqPath = string.IsNullOrWhiteSpace(configuredSwrByFreqPath)
    ? Path.Combine(Path.GetDirectoryName(rfTelemetryPath) ?? builder.Environment.ContentRootPath, "RfSwrByFrequency.json")
    : (Path.IsPathRooted(configuredSwrByFreqPath)
        ? configuredSwrByFreqPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredSwrByFreqPath));
builder.Services.AddSingleton(new RfSwrByFrequencyStore(swrByFreqPath));
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
        provider.GetRequiredService<IPowerMonitor>(),
        provider.GetRequiredService<FrequencySnapshotProvider>(),
        provider.GetRequiredService<RfHistoryStore>(),
        provider.GetRequiredService<RfAnalysisStore>(),
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Lp100MonitorOptions>>(),
        provider.GetRequiredService<ILogger<RfHistoryCollector>>()));

builder.Services.AddSingleton<GatewayPulseService>();
builder.Services.AddSingleton<PushoverService>();
builder.Services.AddVictronMonitorSupervision(builder.Configuration);
builder.Services.AddLp100MonitorSupervision(builder.Configuration);
builder.Services.AddHostedService<RfAnalysisEventBridge>();
// TrimodeLivePoller intentionally not registered — TCP :8510 / memory probes can recycle Trimode.
// Re-enable only behind GatewayPulse:TrimodeProbe when explicitly opted in.

var app = builder.Build();
var appsettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var isSensitiveApi =
        context.Request.Path.StartsWithSegments("/api/settings") ||
        context.Request.Path.StartsWithSegments("/api/testalert") ||
        context.Request.Path.StartsWithSegments("/api/rf/test-connection") ||
        context.Request.Path.StartsWithSegments("/api/radiocat");
    if (isSensitiveApi && !LocalRequestPolicy.IsAllowed(context.Connection))
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
app.UseMobileApiAuth();

app.MapGet("/api/mobile/hello", (Microsoft.Extensions.Options.IOptions<GatewayPulseOptions> gatewayOptions) =>
{
    var options = gatewayOptions.Value;
    return Results.Json(new
    {
        ok = true,
        service = "GatewayPulse",
        gatewayName = options.GatewayName,
        callsign = options.Callsign,
        apiVersion = MobileApiConstants.ApiVersion
    });
});

app.MapGet("/api/status", (
    GatewayPulseService pulse,
    RadioCatFrequencyCache radioCatCache,
    Microsoft.Extensions.Options.IOptions<DashboardOptions> dashboard) =>
{
    var status = pulse.GetStatus();
    status.DemoMode = dashboard.Value.DemoMode;
    status.RefreshSeconds = Math.Clamp(dashboard.Value.RefreshSeconds, 2, 60);
    status.LiveRadioSeconds = Math.Clamp(dashboard.Value.LiveRadioSeconds, 1, 5);

    // Prefer live CI-V / rigctld over Trimode memory when available.
    var (catKhz, catSource, catUpdated, catStatus) = radioCatCache.Snapshot();
    if (catKhz is > 0)
    {
        status.CurrentFrequencyKhz = catKhz.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        // Dial approx for display (USB/LSB offset not known precisely from CI-V alone).
        status.DialFrequencyKhz = (catKhz.Value - 1.500m).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        status.LiveFrequencySource = string.IsNullOrWhiteSpace(catSource) ? "CI-V" : catSource;
        status.FrequencyUpdatedAt = catUpdated;
        status.MemoryReadStatus = catStatus;
    }
    else if (!string.IsNullOrWhiteSpace(catStatus) &&
             !catStatus.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
    {
        status.MemoryReadStatus = catStatus;
    }

    return Results.Json(status);
});

app.MapGet("/api/live-radio", (
    GatewayPulseService pulse,
    RadioCatFrequencyCache radioCatCache,
    Microsoft.Extensions.Options.IOptions<DashboardOptions> dashboard) =>
{
    var live = pulse.GetLiveRadioSnapshot();
    live.DemoMode = dashboard.Value.DemoMode;
    live.RefreshSeconds = Math.Clamp(dashboard.Value.RefreshSeconds, 2, 60);
    live.LiveRadioSeconds = Math.Clamp(dashboard.Value.LiveRadioSeconds, 1, 5);

    var (catKhz, catSource, catUpdated, catStatus) = radioCatCache.Snapshot();
    if (catKhz is > 0)
    {
        live.CurrentFrequencyKhz = catKhz.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        live.DialFrequencyKhz = (catKhz.Value - 1.500m).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        live.LiveFrequencySource = string.IsNullOrWhiteSpace(catSource) ? "CI-V" : catSource;
        live.FrequencyUpdatedAt = catUpdated;
        live.MemoryReadStatus = catStatus;
    }

    return Results.Json(live);
});

app.MapGet("/api/settings/com-ports", () =>
{
    try
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Results.Json(new { ports });
    }
    catch
    {
        return Results.Json(new { ports = Array.Empty<string>() });
    }
});

app.MapPost("/api/radiocat/test", async (
    RadioCatFrequencyClient client,
    RadioCatFrequencyCache cache,
    CancellationToken cancellationToken) =>
{
    var (khz, source, status) = await client.TryGetFrequencyAsync(cancellationToken);
    if (khz is > 0)
        cache.Set(khz, source, status);
    else
        cache.SetStatus(status);

    return Results.Json(new
    {
        ok = khz is > 0,
        frequencyKhz = khz,
        source,
        status
    });
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

app.MapGet("/api/rf/analysis", (
    RfAnalysisStore analysisStore,
    RfTransmissionHistoryStore txHistoryStore,
    string? range,
    DateTimeOffset? from,
    DateTimeOffset? to,
    string? transmissionId) =>
{
    DateTimeOffset? resolvedFrom = from;
    DateTimeOffset? resolvedTo = to;
    var resolvedRange = range;

    if (string.Equals(range, "last", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(range, "lasttx", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(transmissionId))
    {
        var list = txHistoryStore.List(50);
        RfTransmissionEvent? tx = null;
        if (!string.IsNullOrWhiteSpace(transmissionId))
            tx = list.FirstOrDefault(t => string.Equals(t.Id, transmissionId, StringComparison.OrdinalIgnoreCase));
        tx ??= list.FirstOrDefault(t => !t.InProgress) ?? list.FirstOrDefault();
        if (tx is not null)
        {
            resolvedFrom = tx.StartTime.AddSeconds(-5);
            resolvedTo = (tx.EndTime ?? DateTimeOffset.UtcNow).AddSeconds(5);
            resolvedRange = "last";
            transmissionId = tx.Id;
        }
    }

    return Results.Json(analysisStore.Query(resolvedFrom, resolvedTo, resolvedRange, transmissionId));
});

app.MapGet("/api/rf/analysis/events", (
    RfAnalysisStore analysisStore,
    string? range,
    DateTimeOffset? from,
    DateTimeOffset? to,
    int? take) =>
    Results.Json(analysisStore.QueryEvents(from, to, range, take ?? 200)));

app.MapGet("/api/rf/swr-by-frequency", (
    RfSwrByFrequencyStore swrByFrequencyStore,
    string? range,
    DateTimeOffset? from,
    DateTimeOffset? to,
    string? source,
    string? confidence,
    long? minFrequencyHz,
    long? maxFrequencyHz,
    decimal? minForwardWatts,
    string? metric,
    bool? aggregate,
    long? bucketHz,
    string? compare) =>
{
    return Results.Json(swrByFrequencyStore.Query(
        range: range,
        from: from,
        to: to,
        source: source,
        confidence: confidence,
        minFrequencyHz: minFrequencyHz,
        maxFrequencyHz: maxFrequencyHz,
        minForwardWatts: minForwardWatts,
        metric: metric ?? "max",
        aggregate: aggregate == true,
        bucketHz: bucketHz ?? RfSwrByFrequencyStore.DefaultBucketHz,
        compare: compare));
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
    if (radioCat.BaudRate <= 0) radioCat.BaudRate = 19200;
    if (radioCat.PollSeconds < 1) radioCat.PollSeconds = 2;
    if (radioCat.PollSeconds > 30) radioCat.PollSeconds = 30;
    if (string.IsNullOrWhiteSpace(radioCat.Mode)) radioCat.Mode = "CivCom";
    if (string.IsNullOrWhiteSpace(radioCat.CivAddress)) radioCat.CivAddress = "94";
    radioCat.PortName = (radioCat.PortName ?? "").Trim().ToUpperInvariant();
    radioCat.CivAddress = radioCat.CivAddress.Trim();
    radioCat.Mode = radioCat.Mode.Trim();
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
    if (lp.IntervalMs < 50) lp.IntervalMs = 80;
    if (lp.IdleIntervalMs < 250) lp.IdleIntervalMs = 1000;
    if (lp.RestartDelaySeconds < 1) lp.RestartDelaySeconds = 10;
    if (lp.TxThresholdWatts <= 0) lp.TxThresholdWatts = 0.05m;
    if (lp.SwrMinForwardWatts <= 0) lp.SwrMinForwardWatts = 0.5m;
    // Prefer SessionCoalesceMs; keep TxEndDebounceMs as a synced legacy alias.
    if (lp.SessionCoalesceMs < 100 && lp.TxEndDebounceMs >= 100)
        lp.SessionCoalesceMs = lp.TxEndDebounceMs;
    if (lp.SessionCoalesceMs < 100) lp.SessionCoalesceMs = 6000;
    lp.TxEndDebounceMs = lp.SessionCoalesceMs;
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
    if (rfMonitoring["AnalysisPath"] is null)
        rfMonitoring["AnalysisPath"] = @"C:\PWM\RfAnalysis.json";
    if (rfMonitoring["SwrByFrequencyPath"] is null)
        rfMonitoring["SwrByFrequencyPath"] = @"C:\PWM\RfSwrByFrequency.json";
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
