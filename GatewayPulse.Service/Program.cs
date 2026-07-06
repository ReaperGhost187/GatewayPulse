using GatewayPulse.Core;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Gateway Pulse";
});

builder.Services.Configure<GatewayPulseOptions>(builder.Configuration.GetSection("GatewayPulse"));
builder.Services.Configure<PushoverOptions>(builder.Configuration.GetSection("Pushover"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));

builder.Services.AddSingleton<GatewayPulseService>();
builder.Services.AddSingleton<PushoverService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (GatewayPulseService pulse) =>
{
    return Results.Json(pulse.GetStatus());
});

app.MapPost("/api/testalert", async (PushoverService pushover) =>
{
    var ok = await pushover.SendAsync(
        "Gateway Pulse Test",
        "Gateway Pulse is successfully connected to Pushover.");

    return Results.Json(new { ok });
});

app.Run();
