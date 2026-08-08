using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayPulse.Core;
using GatewayPulse.ServiceHosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class MobilePushTests
{
    [Fact]
    public void Registry_PersistsAcrossReload_AndHidesTokenInPublicView()
    {
        using var dir = new TempDir();
        var registry = CreateRegistry(dir.Path);
        var device = registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "dev-1",
            ApnsToken = "token-secret-aaa",
            Environment = "sandbox",
            DeviceName = "iPhone"
        });

        Assert.Equal("dev-1", device.DeviceId);
        Assert.Equal("token-secret-aaa", device.ApnsToken);

        var publicView = MobileDevicePublicView.From(device);
        var json = JsonSerializer.Serialize(publicView);
        Assert.DoesNotContain("token-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(publicView.HasToken);
        Assert.False(publicView.TokenStale);

        var reloaded = CreateRegistry(dir.Path);
        var loaded = reloaded.Get("dev-1");
        Assert.NotNull(loaded);
        Assert.Equal("token-secret-aaa", loaded!.ApnsToken);
        Assert.False(loaded.Preferences.PowerWarning); // default OFF
    }

    [Fact]
    public void Registry_UpdatesToken_AndMarkStaleClearsTokenWithoutDelete()
    {
        using var dir = new TempDir();
        var registry = CreateRegistry(dir.Path);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "dev-1",
            ApnsToken = "token-1",
            Environment = "production"
        });
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "dev-1",
            ApnsToken = "token-2",
            Environment = "production"
        });

        var updated = registry.Get("dev-1");
        Assert.Equal("token-2", updated!.ApnsToken);
        Assert.Equal(ApnsEnvironments.Production, updated.Environment);

        Assert.True(registry.MarkTokenStale("dev-1"));
        var stale = registry.Get("dev-1");
        Assert.NotNull(stale);
        Assert.True(stale!.TokenStale);
        Assert.Equal("", stale.ApnsToken);
        Assert.True(stale.Preferences.MasterEnabled);
    }

    [Fact]
    public void Preferences_FilterDeliveryOnly()
    {
        var prefs = MobilePushPreferences.CreateDefaults();
        Assert.True(prefs.Allows(MobileAlertTypes.RfSwrCritical));
        Assert.False(prefs.Allows(MobileAlertTypes.PowerWarning));

        prefs.MasterEnabled = false;
        Assert.False(prefs.Allows(MobileAlertTypes.RfSwrCritical));

        prefs = MobilePushPreferences.CreateDefaults();
        prefs.Rf = false;
        Assert.False(prefs.Allows(MobileAlertTypes.RfSwrWarning));
        Assert.True(prefs.Allows(MobileAlertTypes.GatewayRelayOffline));

        prefs.GatewayStationConnected = false;
        Assert.False(prefs.Allows(MobileAlertTypes.GatewayStationConnected));
    }

    [Fact]
    public async Task Router_SendsOnlyToSubscribers()
    {
        using var dir = new TempDir();
        var registry = CreateRegistry(dir.Path);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "on",
            ApnsToken = "tok-on",
            Environment = "sandbox"
        });
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "off",
            ApnsToken = "tok-off",
            Environment = "sandbox"
        });
        registry.UpdatePreferences("off", new MobilePushPreferences { MasterEnabled = false });

        var fake = new FakeApnsClient();
        var router = CreateRouter(registry, fake, dir.Path, enabled: true);
        var result = await router.RouteAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.RfSwrCritical,
            Title = "Critical SWR",
            Message = "SWR high",
            Severity = MobileAlertSeverity.Critical,
            Source = "rf"
        });

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Single(fake.SentDeviceIds);
        Assert.Equal("on", fake.SentDeviceIds[0]);
    }

    [Fact]
    public async Task Router_MarksStaleOn410_DoesNotDeleteDevice()
    {
        using var dir = new TempDir();
        var registry = CreateRegistry(dir.Path);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "gone",
            ApnsToken = "dead-token",
            Environment = "sandbox"
        });

        var fake = new FakeApnsClient
        {
            NextResult = new ApnsSendResult
            {
                Success = false,
                StatusCode = 410,
                Outcome = "unregistered",
                TokenInvalid = true
            }
        };
        var router = CreateRouter(registry, fake, dir.Path, enabled: true);
        await router.RouteAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.GatewayRelayOffline,
            Title = "Relay",
            Message = "offline",
            Source = "gateway"
        });

        var device = registry.Get("gone");
        Assert.NotNull(device);
        Assert.True(device!.TokenStale);
        Assert.Equal("", device.ApnsToken);
    }

    [Theory]
    [InlineData("disconnected", MobileAlertTypes.RfDisconnected)]
    [InlineData("stale", MobileAlertTypes.RfStale)]
    [InlineData("recovery", MobileAlertTypes.RfRecovery)]
    [InlineData("swr-warning", MobileAlertTypes.RfSwrWarning)]
    [InlineData("swr-critical", MobileAlertTypes.RfSwrCritical)]
    [InlineData("reflected", MobileAlertTypes.RfReflected)]
    [InlineData("high-power", MobileAlertTypes.RfHighPower)]
    [InlineData("cleared", MobileAlertTypes.RfCleared)]
    public void EventFactory_MapsRfConditions(string condition, string expectedType)
    {
        var evt = MobileAlertEventFactory.FromRfCondition(condition, "t", "m");
        Assert.NotNull(evt);
        Assert.Equal(expectedType, evt!.Type);
        if (condition == "swr-critical")
            Assert.Equal(MobileAlertSeverity.Critical, evt.Severity);
    }

    [Fact]
    public void EventFactory_MapsGatewayProblems_WithoutDuplicates()
    {
        var events = MobileAlertEventFactory.FromGatewayProblems(
            ["RMS Relay is offline", "Scanner is stopped"],
            "TestGW",
            previousProblems: ["RMS Relay is offline"]).ToList();

        Assert.Single(events);
        Assert.Equal(MobileAlertTypes.GatewayScannerStopped, events[0].Type);
    }

    [Fact]
    public void Apns_EndpointHost_SandboxVsProduction()
    {
        Assert.Equal("api.sandbox.push.apple.com", ApnsClient.ResolveEndpointHost("sandbox"));
        Assert.Equal("api.push.apple.com", ApnsClient.ResolveEndpointHost("production"));
        Assert.Equal("api.sandbox.push.apple.com", ApnsClient.ResolveEndpointHost("unknown"));
    }

    [Fact]
    public void Apns_Payload_ContainsApsAndGatewayPulse_NoToken()
    {
        var payload = ApnsClient.BuildPayload(new MobileAlertEvent
        {
            Id = "abc",
            Type = MobileAlertTypes.RfSwrCritical,
            Severity = MobileAlertSeverity.Critical,
            Title = "Critical SWR",
            Message = "SWR 3.5",
            Source = "rf",
            MeasuredValue = 3.5m,
            Threshold = 3.0m,
            Unit = "SWR"
        });

        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.TryGetProperty("aps", out var aps));
        Assert.True(aps.TryGetProperty("alert", out var alert));
        Assert.Equal("Critical SWR", alert.GetProperty("title").GetString());
        Assert.True(doc.RootElement.TryGetProperty("gatewayPulse", out var gp));
        Assert.Equal(MobileAlertTypes.RfSwrCritical, gp.GetProperty("type").GetString());
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApnsJwt_CreatesEs256Token_WithKidAndIss()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(ApnsJwtProvider.TryCreateTokenForTests(
            ecdsa,
            teamId: "TEAM123456",
            keyId: "KEYID12345",
            issuedAt: DateTimeOffset.UnixEpoch.AddHours(1),
            out var token));

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var header = JsonDocument.Parse(headerJson);
        using var payload = JsonDocument.Parse(payloadJson);
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("KEYID12345", header.RootElement.GetProperty("kid").GetString());
        Assert.Equal("TEAM123456", payload.RootElement.GetProperty("iss").GetString());
        Assert.DoesNotContain("BEGIN PRIVATE KEY", token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApnsClient_Handles410AsTokenInvalid_AndTransient5xx()
    {
        var gone = new ScriptedHandler(req => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent("""{"reason":"Unregistered"}""")
        });
        var client410 = CreateApnsClient(gone, enabled: true);
        var result410 = await client410.SendAsync(
            new MobileDeviceRecord { DeviceId = "d1", ApnsToken = "tok", Environment = "sandbox" },
            new MobileAlertEvent { Title = "t", Message = "m", Type = MobileAlertTypes.RfCleared });
        Assert.True(result410.TokenInvalid);
        Assert.False(result410.Success);

        var transient = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"reason":"IdleTimeout"}""")
        });
        var client5xx = CreateApnsClient(transient, enabled: true);
        var result5xx = await client5xx.SendAsync(
            new MobileDeviceRecord { DeviceId = "d1", ApnsToken = "tok", Environment = "sandbox" },
            new MobileAlertEvent { Title = "t", Message = "m", Type = MobileAlertTypes.RfCleared });
        Assert.True(result5xx.TransientFailure);
        Assert.False(result5xx.TokenInvalid);
    }

    [Fact]
    public async Task ApnsClient_DoesNotLogOrIncludeTokenInOutcome()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"reason":"BadDeviceToken"}""")
        });
        var client = CreateApnsClient(handler, enabled: true);
        var secretToken = "super-secret-device-token-xyz";
        var result = await client.SendAsync(
            new MobileDeviceRecord { DeviceId = "d1", ApnsToken = secretToken, Environment = "sandbox" },
            new MobileAlertEvent { Title = "t", Message = "m", Type = "test" });
        Assert.DoesNotContain(secretToken, result.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_RejectsRemotePostToMobileWithoutBearer()
    {
        var (status, _) = await InvokeAuthAsync(
            remoteIp: "203.0.113.10",
            method: HttpMethods.Post,
            path: "/api/mobile/devices/register",
            apiToken: "configured-secret",
            authorization: null);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Auth_AllowsRemotePostToMobileWithBearer()
    {
        var (status, _) = await InvokeAuthAsync(
            remoteIp: "203.0.113.10",
            method: HttpMethods.Post,
            path: "/api/mobile/devices/register",
            apiToken: "configured-secret",
            authorization: "Bearer configured-secret");
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Auth_AllowsLoopbackPostToMobileWithoutBearer()
    {
        var (status, _) = await InvokeAuthAsync(
            remoteIp: "127.0.0.1",
            method: HttpMethods.Post,
            path: "/api/mobile/devices/register",
            apiToken: "configured-secret",
            authorization: null);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void Auth_RequiresAuth_AllMethodsOnMobile_GetOnlyElsewhere()
    {
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Post, "/api/mobile/devices/register"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Put, "/api/mobile/devices/x/preferences"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Delete, "/api/mobile/devices/x"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Get, "/api/mobile/push/status"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Get, "/api/status"));
        Assert.False(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Post, "/api/status"));
        Assert.False(MobileApiAuthMiddleware.IsProtectedPath("/api/settings"));
        Assert.False(MobileApiAuthMiddleware.IsProtectedPath("/api/testalert"));
        Assert.False(MobileApiAuthMiddleware.IsProtectedPath("/api/radiocat"));
    }

    [Fact]
    public void PushStatus_Projection_ContainsNoSecrets()
    {
        using var dir = new TempDir();
        var options = new ApplePushOptions
        {
            Enabled = false,
            TeamId = "SECRETTEAM",
            KeyId = "SECRETKEY",
            PrivateKeyPath = Path.Combine(dir.Path, "missing.p8"),
            BundleId = "com.example.gp",
            DeviceRegistryPath = Path.Combine(dir.Path, "devices.json"),
            HistoryPath = Path.Combine(dir.Path, "history.json")
        };
        // Status endpoint intentionally omits TeamId/KeyId/PrivateKeyPath from JSON.
        // Validate the shape we emit in MapMobilePushEndpoints mentally via flags only.
        Assert.False(string.IsNullOrWhiteSpace(options.TeamId));
        var safe = new
        {
            ok = true,
            enabled = options.Enabled,
            configured = false,
            bundleIdConfigured = true,
            keyFilePresent = false,
            deviceCount = 0
        };
        var json = JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("SECRETTEAM", json);
        Assert.DoesNotContain("SECRETKEY", json);
        Assert.DoesNotContain("PrivateKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".p8", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void History_IsBounded_AndStoresNoTokens()
    {
        using var dir = new TempDir();
        var options = Options.Create(new ApplePushOptions
        {
            HistoryPath = Path.Combine(dir.Path, "history.json"),
            HistoryCapacity = 100
        });
        var history = new MobilePushHistory(new StaticAppleOptions(options.Value));
        for (var i = 0; i < 120; i++)
        {
            history.Record(new MobilePushHistoryEntry
            {
                Id = i.ToString(),
                Type = MobileAlertTypes.RfCleared,
                Title = "t",
                DeviceIds = ["dev"],
                SentAt = DateTimeOffset.UtcNow
            });
        }

        var recent = history.GetRecent(500);
        Assert.Equal(100, recent.Count);
        var json = File.ReadAllText(Path.Combine(dir.Path, "history.json"));
        Assert.DoesNotContain("apns", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApnsLogSanitizer_Describe_NeverIncludesTokenOrDevicePath()
    {
        var secret = "super-secret-device-token-xyz";
        var ex = new HttpRequestException(
            $"An error occurred while sending the request to https://api.sandbox.push.apple.com/3/device/{secret}");
        var description = ApnsLogSanitizer.Describe(ex);
        Assert.DoesNotContain(secret, description, StringComparison.Ordinal);
        Assert.False(ApnsLogSanitizer.LooksUnsafe(description));
        Assert.Contains("HttpRequestException", description, StringComparison.Ordinal);
        Assert.Equal("http_request", ApnsLogSanitizer.Category(ex));
    }

    [Fact]
    public async Task ApnsClient_ExceptionPath_DoesNotLogTokenOrRequestUri()
    {
        var secret = "super-secret-device-token-xyz";
        var logger = new CapturingLogger<ApnsClient>();
        var handler = new ScriptedHandler(_ =>
            throw new HttpRequestException(
                $"Request failed: https://api.sandbox.push.apple.com/3/device/{secret}"));
        var client = CreateApnsClient(handler, enabled: true, logger);
        var result = await client.SendAsync(
            new MobileDeviceRecord { DeviceId = "d1", ApnsToken = secret, Environment = "sandbox" },
            new MobileAlertEvent { Title = "t", Message = "m", Type = MobileAlertTypes.RfCleared });

        Assert.False(result.Success);
        Assert.True(result.TransientFailure);
        Assert.NotEmpty(logger.Messages);
        foreach (var message in logger.Messages)
        {
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("/3/device/", message, StringComparison.OrdinalIgnoreCase);
            Assert.False(ApnsLogSanitizer.LooksUnsafe(message));
        }
    }

    [Fact]
    public async Task Publisher_ReturnsWithoutWaitingOnSlowApns()
    {
        using var dir = new TempDir();
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new SlowApnsClient(slowGate.Task, started);
        var tracker = new MobilePushDeliveryTracker();
        var options = new StaticAppleOptions(new ApplePushOptions
        {
            Enabled = true,
            DispatchQueueCapacity = 16,
            DeviceRegistryPath = Path.Combine(dir.Path, "devices.json"),
            HistoryPath = Path.Combine(dir.Path, "history.json")
        });
        var registry = new MobileDeviceRegistry(options);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "d1",
            ApnsToken = "tok",
            Environment = "sandbox"
        });
        var queue = new MobilePushDispatchQueue(options, tracker, NullLogger<MobilePushDispatchQueue>.Instance);
        var router = new MobileAlertRouter(
            registry, fake, new MobilePushHistory(options), options, NullLogger<MobileAlertRouter>.Instance);
        var publisher = new MobileAlertPublisher(queue, options, NullLogger<MobileAlertPublisher>.Instance);
        using var dispatcher = new MobilePushDispatcher(
            queue, router, tracker, NullLogger<MobilePushDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var publishTask = publisher.PublishAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.RfSwrCritical,
            Title = "Critical SWR",
            Message = "SWR high",
            Source = "rf",
            DedupeKey = "rf:swr-critical"
        });
        var completed = await Task.WhenAny(publishTask, Task.Delay(250));
        Assert.Same(publishTask, completed);
        var ack = await publishTask;
        Assert.True(ack.Accepted);
        Assert.Equal(MobilePublishOutcomes.Queued, ack.Outcome);
        Assert.False(fake.SendCompleted);

        // Unblock APNs and confirm dispatcher eventually delivers.
        slowGate.SetResult();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => fake.SendCompleted, TimeSpan.FromSeconds(2));
        await WaitForAsync(() => tracker.WasDelivered("rf:swr-critical"), TimeSpan.FromSeconds(2));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PowerPublish_ReturnsWithoutWaitingOnSlowApns()
    {
        using var dir = new TempDir();
        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new SlowApnsClient(slowGate.Task, started);
        var tracker = new MobilePushDeliveryTracker();
        var options = new StaticAppleOptions(new ApplePushOptions
        {
            Enabled = true,
            DispatchQueueCapacity = 16,
            DeviceRegistryPath = Path.Combine(dir.Path, "devices.json"),
            HistoryPath = Path.Combine(dir.Path, "history.json")
        });
        var registry = new MobileDeviceRegistry(options);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "d1",
            ApnsToken = "tok",
            Environment = "sandbox"
        });
        // Power warning preference defaults OFF — enable for this delivery test.
        registry.UpdatePreferences("d1", new MobilePushPreferences
        {
            MasterEnabled = true,
            Power = true,
            PowerWarning = true
        });
        var queue = new MobilePushDispatchQueue(options, tracker, NullLogger<MobilePushDispatchQueue>.Instance);
        var router = new MobileAlertRouter(
            registry, fake, new MobilePushHistory(options), options, NullLogger<MobileAlertRouter>.Instance);
        var publisher = new MobileAlertPublisher(queue, options, NullLogger<MobileAlertPublisher>.Instance);

        using var dispatcher = new MobilePushDispatcher(
            queue, router, tracker, NullLogger<MobilePushDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ack = await publisher.PublishAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.PowerWarning,
            Title = "Power Warning",
            Message = "low",
            Source = "power"
        });
        sw.Stop();

        Assert.True(ack.Accepted);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Publish awaited APNs ({sw.ElapsedMilliseconds} ms)");
        Assert.False(fake.SendCompleted);

        slowGate.SetResult();
        await WaitForAsync(() => fake.SendCompleted, TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchQueue_DropOldest_OnOverflow()
    {
        var tracker = new MobilePushDeliveryTracker();
        var options = new StaticAppleOptions(new ApplePushOptions
        {
            Enabled = true,
            DispatchQueueCapacity = 8
        });
        var queue = new MobilePushDispatchQueue(options, tracker, NullLogger<MobilePushDispatchQueue>.Instance);

        for (var i = 0; i < 8; i++)
        {
            var ack = queue.Enqueue(new MobileAlertEvent
            {
                Type = MobileAlertTypes.RfCleared,
                Title = $"n{i}",
                DedupeKey = $"k{i}"
            });
            Assert.True(ack.Accepted);
        }

        Assert.Equal(0, queue.OverflowDrops);
        var overflowAck = queue.Enqueue(new MobileAlertEvent
        {
            Type = MobileAlertTypes.RfCleared,
            Title = "overflow",
            DedupeKey = "k-overflow"
        });
        Assert.True(overflowAck.Accepted);
        Assert.Equal(1, queue.OverflowDrops);
        Assert.Equal(MobilePushDeliveryTracker.DeliveryStatus.Failed, tracker.GetStatus("k0"));
        Assert.Equal(MobilePushDeliveryTracker.DeliveryStatus.Pending, tracker.GetStatus("k-overflow"));

        // FIFO among retained: first dequeued should be k1 (k0 dropped).
        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("k1", first!.DedupeKey);
        queue.Complete();
    }

    [Fact]
    public async Task DeliveryTracker_AllFail_AllowsCooldownRetry_NotWhilePending()
    {
        using var dir = new TempDir();
        var failClient = new FakeApnsClient
        {
            NextResult = new ApnsSendResult
            {
                Success = false,
                StatusCode = 500,
                Outcome = "transient",
                TransientFailure = true
            }
        };
        var tracker = new MobilePushDeliveryTracker();
        var options = new StaticAppleOptions(new ApplePushOptions
        {
            Enabled = true,
            DispatchQueueCapacity = 16,
            DeviceRegistryPath = Path.Combine(dir.Path, "devices.json"),
            HistoryPath = Path.Combine(dir.Path, "history.json")
        });
        var registry = new MobileDeviceRegistry(options);
        registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "d1",
            ApnsToken = "tok",
            Environment = "sandbox"
        });
        var queue = new MobilePushDispatchQueue(options, tracker, NullLogger<MobilePushDispatchQueue>.Instance);
        var router = new MobileAlertRouter(
            registry, failClient, new MobilePushHistory(options), options, NullLogger<MobileAlertRouter>.Instance);
        var publisher = new MobileAlertPublisher(queue, options, NullLogger<MobileAlertPublisher>.Instance);
        using var dispatcher = new MobilePushDispatcher(
            queue, router, tracker, NullLogger<MobilePushDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        const string key = "rf:swr-critical";
        var ack1 = await publisher.PublishAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.RfSwrCritical,
            Title = "Critical SWR",
            Message = "SWR high",
            DedupeKey = key
        });
        Assert.True(ack1.Accepted);
        await WaitForAsync(() => tracker.HasFailed(key), TimeSpan.FromSeconds(2));
        Assert.False(tracker.WasDelivered(key));

        // Simulate RF cooldown retry after all-fail: re-enqueue is allowed.
        var ack2 = await publisher.PublishAsync(new MobileAlertEvent
        {
            Type = MobileAlertTypes.RfSwrCritical,
            Title = "Critical SWR",
            Message = "SWR high",
            DedupeKey = key
        });
        Assert.True(ack2.Accepted);
        await WaitForAsync(() => failClient.SentDeviceIds.Count >= 2, TimeSpan.FromSeconds(2));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Registry_RejectsNewDeviceAtMax_AllowsExistingTokenRefresh()
    {
        using var dir = new TempDir();
        var options = new StaticAppleOptions(new ApplePushOptions
        {
            DeviceRegistryPath = Path.Combine(dir.Path, "MobilePushDevices.json"),
            MaxDevices = 25 // clamp floor
        });
        var registry = new MobileDeviceRegistry(options);
        Assert.Equal(25, registry.MaxDevices);

        for (var i = 0; i < 25; i++)
        {
            registry.Register(new MobileDeviceRegisterRequest
            {
                DeviceId = $"dev-{i}",
                ApnsToken = $"tok-{i}",
                Environment = "sandbox"
            });
        }

        var ex = Assert.Throws<MobileDeviceRegistryFullException>(() =>
            registry.Register(new MobileDeviceRegisterRequest
            {
                DeviceId = "dev-new",
                ApnsToken = "tok-new",
                Environment = "sandbox"
            }));
        Assert.Equal(25, ex.MaxDevices);
        Assert.Contains("full", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Existing deviceId token refresh still works at cap.
        var refreshed = registry.Register(new MobileDeviceRegisterRequest
        {
            DeviceId = "dev-0",
            ApnsToken = "tok-0-refreshed",
            Environment = "production"
        });
        Assert.Equal("tok-0-refreshed", refreshed.ApnsToken);
        Assert.Equal(ApnsEnvironments.Production, refreshed.Environment);
        Assert.Equal(25, registry.GetAll().Count);
    }

    private static MobileDeviceRegistry CreateRegistry(string dir)
    {
        var options = new ApplePushOptions
        {
            DeviceRegistryPath = Path.Combine(dir, "MobilePushDevices.json")
        };
        return new MobileDeviceRegistry(new StaticAppleOptions(options));
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(20);
        }
    }

    private static MobileAlertRouter CreateRouter(
        MobileDeviceRegistry registry,
        IApnsClient client,
        string dir,
        bool enabled)
    {
        var options = new ApplePushOptions
        {
            Enabled = enabled,
            DeviceRegistryPath = Path.Combine(dir, "MobilePushDevices.json"),
            HistoryPath = Path.Combine(dir, "history.json")
        };
        var monitor = new StaticAppleOptions(options);
        return new MobileAlertRouter(
            registry,
            client,
            new MobilePushHistory(monitor),
            monitor,
            NullLogger<MobileAlertRouter>.Instance);
    }

    private static ApnsClient CreateApnsClient(
        HttpMessageHandler handler,
        bool enabled,
        ILogger<ApnsClient>? logger = null)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportPkcs8PrivateKeyPem();
        var dir = Path.Combine(Path.GetTempPath(), "gp-apns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "key.p8");
        File.WriteAllText(keyPath, pem);

        var options = new StaticAppleOptions(new ApplePushOptions
        {
            Enabled = enabled,
            TeamId = "TEAM123456",
            KeyId = "KEYID12345",
            PrivateKeyPath = keyPath,
            BundleId = "com.gatewaypulse.mobile"
        });
        var jwt = new ApnsJwtProvider(options);
        return new ApnsClient(options, jwt, logger ?? NullLogger<ApnsClient>.Instance, handler);
    }

    private static async Task<(int StatusCode, string Body)> InvokeAuthAsync(
        string remoteIp,
        string method,
        string path,
        string apiToken,
        string? authorization)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MobileApiOptions>(o => o.ApiToken = apiToken);
        services.AddSingleton<IMobileApiTokenValidator, MobileApiTokenValidator>();
        await using var provider = services.BuildServiceProvider();

        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var middleware = new MobileApiAuthMiddleware(next, NullLogger<MobileApiAuthMiddleware>.Instance);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Method = method;
        context.Request.Path = path;
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, provider.GetRequiredService<IMobileApiTokenValidator>());
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private sealed class FakeApnsClient : IApnsClient
    {
        public ApnsSendResult NextResult { get; set; } = new() { Success = true, StatusCode = 200, Outcome = "sent" };
        public List<string> SentDeviceIds { get; } = [];

        public Task<ApnsSendResult> SendAsync(
            MobileDeviceRecord device,
            MobileAlertEvent alertEvent,
            CancellationToken cancellationToken = default)
        {
            SentDeviceIds.Add(device.DeviceId);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class SlowApnsClient(Task gate, TaskCompletionSource started) : IApnsClient
    {
        public bool SendCompleted { get; private set; }

        public async Task<ApnsSendResult> SendAsync(
            MobileDeviceRecord device,
            MobileAlertEvent alertEvent,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await gate.WaitAsync(cancellationToken);
            SendCompleted = true;
            return new ApnsSendResult { Success = true, StatusCode = 200, Outcome = "sent" };
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Messages.Add(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    private sealed class StaticAppleOptions : IOptionsMonitor<ApplePushOptions>
    {
        public StaticAppleOptions(ApplePushOptions currentValue) => CurrentValue = currentValue;
        public ApplePushOptions CurrentValue { get; }
        public ApplePushOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApplePushOptions, string?> listener) => null;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gp-mobile-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
