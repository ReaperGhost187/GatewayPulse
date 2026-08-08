using System.Net;
using System.Text;
using GatewayPulse.ServiceHosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class MobileApiAuthTests
{
    [Theory]
    [InlineData("/api/status", true)]
    [InlineData("/api/live-radio", true)]
    [InlineData("/api/power", true)]
    [InlineData("/api/power/history", true)]
    [InlineData("/api/rf", true)]
    [InlineData("/api/rf/transmissions", true)]
    [InlineData("/api/rf/history", true)]
    [InlineData("/api/rf/analysis", true)]
    [InlineData("/api/rf/analysis/events", true)]
    [InlineData("/api/rf/swr-by-frequency", true)]
    [InlineData("/api/preferences", true)]
    [InlineData("/api/network-map", true)]
    [InlineData("/api/mobile/hello", true)]
    [InlineData("/api/settings", false)]
    [InlineData("/api/testalert", false)]
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    public void IsProtectedPath_MatchesReadTelemetryApis(string path, bool expected)
    {
        Assert.Equal(expected, MobileApiAuthMiddleware.IsProtectedPath(path));
    }

    [Fact]
    public void FixedTimeEqualsUtf8_MatchesEqualStrings()
    {
        Assert.True(MobileApiTokenValidator.FixedTimeEqualsUtf8("abc123", "abc123"));
        Assert.False(MobileApiTokenValidator.FixedTimeEqualsUtf8("abc123", "abc124"));
        Assert.False(MobileApiTokenValidator.FixedTimeEqualsUtf8("short", "longer-token"));
    }

    [Fact]
    public void Validator_RejectsWhenTokenNotConfigured()
    {
        var validator = CreateValidator("");
        Assert.False(validator.IsConfigured);
        Assert.False(validator.IsValid("any-token"));
    }

    [Fact]
    public void Validator_AcceptsExactConfiguredToken()
    {
        var validator = CreateValidator("unit-test-secret");
        Assert.True(validator.IsConfigured);
        Assert.True(validator.IsValid("unit-test-secret"));
        Assert.False(validator.IsValid("wrong-secret"));
        Assert.False(validator.IsValid(null));
        Assert.False(validator.IsValid(""));
    }

    [Fact]
    public async Task Middleware_AllowsLoopbackWithoutToken()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "127.0.0.1",
            path: "/api/status",
            apiToken: "configured-secret",
            authorization: null);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Middleware_AllowsLoopbackIpv6WithoutToken()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "::1",
            path: "/api/mobile/hello",
            apiToken: "configured-secret",
            authorization: null);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Middleware_RejectsRemoteWhenTokenMissing()
    {
        var (status, body) = await InvokeAsync(
            remoteIp: "192.168.1.50",
            path: "/api/status",
            apiToken: "configured-secret",
            authorization: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Contains("Unauthorized", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Middleware_RejectsRemoteWhenTokenNotConfigured()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "10.0.0.8",
            path: "/api/power",
            apiToken: "",
            authorization: "Bearer anything");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Middleware_RejectsRemoteWhenTokenInvalid()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            path: "/api/rf",
            apiToken: "configured-secret",
            authorization: "Bearer wrong-secret");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Middleware_AllowsRemoteWithValidBearerToken()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            path: "/api/mobile/hello",
            apiToken: "configured-secret",
            authorization: "Bearer configured-secret");

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Middleware_SkipsUnprotectedPaths()
    {
        var (status, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            path: "/index.html",
            apiToken: "configured-secret",
            authorization: null);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void EvaluateRemoteAuth_ReportsExpectedReasons()
    {
        var validator = CreateValidator("secret");
        var missing = new DefaultHttpContext();
        Assert.Equal("missing", MobileApiAuthMiddleware.EvaluateRemoteAuth(missing, validator));

        var invalidScheme = new DefaultHttpContext();
        invalidScheme.Request.Headers.Authorization = "Basic secret";
        Assert.Equal("invalid", MobileApiAuthMiddleware.EvaluateRemoteAuth(invalidScheme, validator));

        var wrong = new DefaultHttpContext();
        wrong.Request.Headers.Authorization = "Bearer nope";
        Assert.Equal("invalid", MobileApiAuthMiddleware.EvaluateRemoteAuth(wrong, validator));

        var ok = new DefaultHttpContext();
        ok.Request.Headers.Authorization = "Bearer secret";
        Assert.Null(MobileApiAuthMiddleware.EvaluateRemoteAuth(ok, validator));

        var unconfigured = CreateValidator("");
        Assert.Equal("not_configured", MobileApiAuthMiddleware.EvaluateRemoteAuth(ok, unconfigured));
    }

    private static IMobileApiTokenValidator CreateValidator(string apiToken)
    {
        var options = Options.Create(new MobileApiOptions { ApiToken = apiToken });
        var monitor = new StaticOptionsMonitor(options.Value);
        return new MobileApiTokenValidator(monitor);
    }

    private static async Task<(int StatusCode, string Body)> InvokeAsync(
        string remoteIp,
        string path,
        string apiToken,
        string? authorization)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MobileApiOptions>(o => o.ApiToken = apiToken);
        services.AddSingleton<IMobileApiTokenValidator, MobileApiTokenValidator>();
        await using var provider = services.BuildServiceProvider();

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var middleware = new MobileApiAuthMiddleware(next, NullLogger<MobileApiAuthMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;

        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, provider.GetRequiredService<IMobileApiTokenValidator>());

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        if (context.Response.StatusCode == StatusCodes.Status200OK)
            Assert.True(nextCalled);

        return (context.Response.StatusCode, body);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<MobileApiOptions>
    {
        public StaticOptionsMonitor(MobileApiOptions currentValue) => CurrentValue = currentValue;

        public MobileApiOptions CurrentValue { get; }

        public MobileApiOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<MobileApiOptions, string?> listener) => null;
    }
}
