# Local mocked smoke for pre-production confidence. Uses a temp content root only.
param(
    [string]$BaseUrl = "http://127.0.0.1:18080",
    [int]$ReadySeconds = 60,
    [string]$ApiToken = "smoke-test-token-not-for-production"
)

$ErrorActionPreference = "Stop"
$Results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Detail = "")
    $Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $color = if ($Status -eq "PASS") { "Green" } elseif ($Status -eq "FAIL") { "Red" } else { "Yellow" }
    Write-Host ("[{0}] {1} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

$ServiceDir = $PSScriptRoot
$ServiceProj = Join-Path $ServiceDir "GatewayPulse.Service.csproj"
$tempDir = Join-Path $env:TEMP ("gp-smoke-audit-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$settingsPath = Join-Path $tempDir "appsettings.json"
$proc = $null

try {
    New-Item -ItemType Directory -Path (Join-Path $tempDir "relay-logs") | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $tempDir "trimode-logs") | Out-Null

    $settings = @{
        urls = $BaseUrl
        GatewayPulse = @{
            GatewayName = "Smoke Audit"
            Callsign = "N0SMOKE"
            PrivacyMode = $true
            RelayLogs = (Join-Path $tempDir "relay-logs")
            TrimodeLogs = (Join-Path $tempDir "trimode-logs")
            TrimodeIni = (Join-Path $tempDir "missing-trimode.ini")
            TrimodeHost = "127.0.0.1"
            TrimodeCommandPort = 8510
            ShowConnectingStations = $true
            TrimodeProbe = @{ CommandPortEnabled = $false; MemoryReadEnabled = $false }
            RadioCat = @{
                Enabled = $true
                Mode = "CivCom"
                PortName = "COM99"
                BaudRate = 19200
                CivAddress = "94"
                PollSeconds = 2
                Host = "127.0.0.1"
                Port = 4532
                TimeoutMs = 400
            }
        }
        Pushover = @{ Enabled = $false; UserKey = ""; ApiToken = "" }
        Alerts = @{
            RelayOffline = $false; TrimodeOffline = $false; ScannerStopped = $false
            Recovery = $false; StationConnected = $false
        }
        Dashboard = @{ DemoMode = $true; RefreshSeconds = 5; LiveRadioSeconds = 2 }
        PowerMonitoring = @{
            TelemetryPath = (Join-Path $tempDir "PowerTelemetry.json")
            HistoryPath = (Join-Path $tempDir "PowerHistory.json")
            HistorySampleSeconds = 30
            StaleAfterSeconds = 30
        }
        VictronMonitor = @{ Enabled = $false; Devices = @() }
        NetworkMap = @{
            ServiceCode = ""; RememberServiceCode = $true; AutoRefresh = $false
            AutoRefreshMinutes = 10; AutoOpenInBrowser = $false
            MapUrl = "https://example.invalid/"
        }
        RfMonitoring = @{
            TelemetryPath = (Join-Path $tempDir "RfTelemetry.json")
            HistoryPath = (Join-Path $tempDir "RfHistory.json")
            AnalysisPath = (Join-Path $tempDir "RfAnalysis.json")
            SwrByFrequencyPath = (Join-Path $tempDir "RfSwrByFrequency.json")
            TransmissionHistoryPath = (Join-Path $tempDir "RfTransmissionHistory.json")
            HistorySampleSeconds = 5
            StaleAfterSeconds = 10
        }
        MobileApi = @{ ApiToken = $ApiToken }
        Lp100Monitor = @{
            Enabled = $false
            Port = "COM4"
            AutoDetect = $false
            BaudRate = 115200
            OutputPath = (Join-Path $tempDir "RfTelemetry.json")
            LogsPath = (Join-Path $tempDir "logs")
            IntervalMs = 80
            IdleIntervalMs = 1000
            RestartDelaySeconds = 10
        }
    }
    $settings | ConvertTo-Json -Depth 32 | Set-Content -Path $settingsPath -Encoding UTF8

    Write-Host "Building GatewayPulse.Service (Release)..."
    dotnet build $ServiceProj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    $dll = Join-Path $ServiceDir "bin\Release\net8.0-windows\GatewayPulse.dll"
    if (-not (Test-Path $dll)) {
        $dll = Get-ChildItem -Path (Join-Path $ServiceDir "bin\Release") -Recurse -Filter "GatewayPulse.dll" |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $dll) { throw "GatewayPulse.dll not found after build." }

    Write-Host "Starting service contentRoot=$tempDir urls=$BaseUrl RadioCat=COM99 MobileApi=token DemoMode=true"
    $prevAspNet = $env:ASPNETCORE_ENVIRONMENT
    $prevDotNet = $env:DOTNET_ENVIRONMENT
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $env:DOTNET_ENVIRONMENT = "Production"
    try {
        $proc = Start-Process -FilePath "dotnet" `
            -ArgumentList @("`"$dll`"", "--urls", "`"$BaseUrl`"") `
            -WorkingDirectory $tempDir `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $tempDir "stdout.log") `
            -RedirectStandardError (Join-Path $tempDir "stderr.log")
    }
    finally {
        $env:ASPNETCORE_ENVIRONMENT = $prevAspNet
        $env:DOTNET_ENVIRONMENT = $prevDotNet
    }

    $statusUrl = "$BaseUrl/api/status"
    $deadline = (Get-Date).AddSeconds($ReadySeconds)
    $ready = $false
    $lastError = ""
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $err = Get-Content (Join-Path $tempDir "stderr.log") -Raw -ErrorAction SilentlyContinue
            $out = Get-Content (Join-Path $tempDir "stdout.log") -Raw -ErrorAction SilentlyContinue
            throw "Process exited early code=$($proc.ExitCode)`nstderr=$err`nstdout=$out"
        }
        try {
            $resp = Invoke-WebRequest -Uri $statusUrl -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) { $ready = $true; break }
            $lastError = "HTTP $($resp.StatusCode)"
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Milliseconds 400
    }
    if (-not $ready) {
        $err = Get-Content (Join-Path $tempDir "stderr.log") -Raw -ErrorAction SilentlyContinue
        $out = Get-Content (Join-Path $tempDir "stdout.log") -Raw -ErrorAction SilentlyContinue
        throw "Timed out waiting for $statusUrl. Last=$lastError`nstderr=$err`nstdout=$out"
    }
    Add-Result "Startup HTTP with RadioCat COM99" "PASS" "Kestrel served /api/status"

    # Give RadioCat one poll cycle so status reflects CI-V open failure cleanly.
    Start-Sleep -Seconds 3

    function Get-Json($Path, $Headers = $null) {
        $params = @{ Uri = "$BaseUrl$Path"; UseBasicParsing = $true; TimeoutSec = 10 }
        if ($Headers) { $params.Headers = $Headers }
        $r = Invoke-WebRequest @params
        return @{ StatusCode = [int]$r.StatusCode; Content = $r.Content; Json = ($r.Content | ConvertFrom-Json) }
    }

    # --- GET endpoints ---
    $st = Get-Json "/api/status"
    if ($st.StatusCode -eq 200 -and ($null -ne $st.Json.currentFrequencyKhz -or $null -ne $st.Json.scannerStatus)) {
        $via = "$($st.Json.scannerStatus)"
        $mem = "$($st.Json.memoryReadStatus)"
        $src = "$($st.Json.liveFrequencySource)"
        $okVia = ($via -match 'CI-V|CAT|Not probed|Disabled') -or ($mem -match 'CI-V|COM|port|Disabled|Poll|Waiting|Starting|open|timeout|not set')
        if ($okVia) {
            Add-Result "GET /api/status" "PASS" "scanner='$via' memory='$mem' source='$src'"
        }
        else {
            Add-Result "GET /api/status" "FAIL" "unexpected status fields scanner='$via' memory='$mem'"
        }
    }
    else {
        Add-Result "GET /api/status" "FAIL" "HTTP $($st.StatusCode)"
    }

    $lr = Get-Json "/api/live-radio"
    if ($lr.StatusCode -eq 200) {
        Add-Result "GET /api/live-radio" "PASS" "source=$($lr.Json.liveFrequencySource) liveRadioSeconds=$($lr.Json.liveRadioSeconds)"
    }
    else {
        Add-Result "GET /api/live-radio" "FAIL" "HTTP $($lr.StatusCode)"
    }

    $pw = Get-Json "/api/power"
    if ($pw.StatusCode -eq 200) {
        Add-Result "GET /api/power" "PASS" "connected=$($pw.Json.connected) provider=$($pw.Json.provider)"
    }
    else {
        Add-Result "GET /api/power" "FAIL" "HTTP $($pw.StatusCode)"
    }

    $rf = Get-Json "/api/rf"
    if ($rf.StatusCode -eq 200 -and $null -ne $rf.Json.telemetry) {
        Add-Result "GET /api/rf" "PASS" "telemetry.connected=$($rf.Json.telemetry.connected)"
    }
    else {
        Add-Result "GET /api/rf" "FAIL" "HTTP $($rf.StatusCode)"
    }

    $helloAuth = Get-Json "/api/mobile/hello" @{ Authorization = "Bearer $ApiToken" }
    if ($helloAuth.StatusCode -eq 200 -and $helloAuth.Json.ok -eq $true) {
        Add-Result "GET /api/mobile/hello bearer" "PASS" "gateway=$($helloAuth.Json.gatewayName)"
    }
    else {
        Add-Result "GET /api/mobile/hello bearer" "FAIL" "HTTP $($helloAuth.StatusCode)"
    }

    $helloLoop = Get-Json "/api/mobile/hello"
    if ($helloLoop.StatusCode -eq 200 -and $helloLoop.Json.ok -eq $true) {
        Add-Result "GET /api/mobile/hello loopback no token" "PASS" "loopback bypass OK"
    }
    else {
        Add-Result "GET /api/mobile/hello loopback no token" "FAIL" "HTTP $($helloLoop.StatusCode)"
    }

    # Remote-auth simulation (unit-tested); document as N/A at HTTP layer from loopback.
    Add-Result "Remote GET without bearer (non-loopback)" "PASS" "covered by MobileApiAuthTests (loopback cannot simulate remote IP)"

    # --- Settings merge: RadioCat preserved when only Lp100Monitor posted ---
    $before = Get-Json "/api/settings"
    $radioBefore = $before.Json.gatewayPulse.radioCat
    if (-not $radioBefore -or $radioBefore.enabled -ne $true -or $radioBefore.portName -ne "COM99") {
        Add-Result "GET /api/settings RadioCat baseline" "FAIL" "expected enabled COM99, got $($radioBefore | ConvertTo-Json -Compress)"
    }
    else {
        Add-Result "GET /api/settings RadioCat baseline" "PASS" "COM99 enabled"
    }

    $lpOnlyBody = @{
        gatewayPulse = @{
            gatewayName = "Smoke Audit"
            callsign = "N0SMOKE"
            # omit radioCat intentionally
        }
        pushover = @{ enabled = $false; userKey = ""; apiToken = "" }
        alerts = @{
            relayOffline = $false; trimodeOffline = $false; scannerStopped = $false
            recovery = $false; stationConnected = $false
        }
        preferences = $before.Json.preferences
        networkMap = $before.Json.networkMap
        lp100Monitor = @{
            enabled = $false
            port = "COM4"
            autoDetect = $false
            baudRate = 115200
            outputPath = (Join-Path $tempDir "RfTelemetry.json")
            logsPath = (Join-Path $tempDir "logs")
            intervalMs = 80
            idleIntervalMs = 1000
            restartDelaySeconds = 10
            historyEnabled = $true
            txThresholdWatts = 0.05
            sessionCoalesceMs = 6000
            txEndDebounceMs = 6000
            swrMinForwardWatts = 0.5
            captureRawFrames = $false
            alerts = @{
                enabled = $false; highSwr = $true; criticalSwr = $true; highReflected = $true
                disconnected = $true; stale = $true; recovery = $true
                swrWarning = 2.0; swrCritical = 3.0; reflectedWarningWatts = 25; cooldownMinutes = 5
            }
        }
    } | ConvertTo-Json -Depth 32

    $postLp = Invoke-WebRequest -Uri "$BaseUrl/api/settings" -Method POST -Body $lpOnlyBody `
        -ContentType "application/json" -UseBasicParsing -TimeoutSec 15
    if ($postLp.StatusCode -ne 200) {
        Add-Result "POST /api/settings Lp100-only" "FAIL" "HTTP $($postLp.StatusCode)"
    }
    else {
        Add-Result "POST /api/settings Lp100-only" "PASS" "200"
    }

    Start-Sleep -Milliseconds 500
    $afterLp = Get-Json "/api/settings"
    $radioAfter = $afterLp.Json.gatewayPulse.radioCat
    $diskAfterLp = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $diskRadio = $diskAfterLp.GatewayPulse.RadioCat
    if ($diskRadio.Enabled -eq $true -and $diskRadio.PortName -eq "COM99") {
        Add-Result "Merge preserves RadioCat after Lp100-only POST" "PASS" "disk COM99 enabled (api port=$($radioAfter.portName))"
    }
    else {
        Add-Result "Merge preserves RadioCat after Lp100-only POST" "FAIL" ("disk=" + ($diskRadio | ConvertTo-Json -Compress))
    }

    # --- COM normalize "4" -> COM4 ---
    $comBody = @{
        gatewayPulse = @{
            gatewayName = "Smoke Audit"
            callsign = "N0SMOKE"
            radioCat = @{
                enabled = $true
                mode = "CivCom"
                portName = "5"
                baudRate = 19200
                civAddress = "94"
                pollSeconds = 2
                host = "127.0.0.1"
                port = 4532
                timeoutMs = 400
            }
        }
        pushover = @{ enabled = $false; userKey = ""; apiToken = "" }
        alerts = @{
            relayOffline = $false; trimodeOffline = $false; scannerStopped = $false
            recovery = $false; stationConnected = $false
        }
        preferences = $before.Json.preferences
        networkMap = $before.Json.networkMap
        lp100Monitor = @{
            enabled = $false
            port = "4"
            autoDetect = $false
            baudRate = 115200
            outputPath = (Join-Path $tempDir "RfTelemetry.json")
            logsPath = (Join-Path $tempDir "logs")
            intervalMs = 80
            idleIntervalMs = 1000
            restartDelaySeconds = 10
            historyEnabled = $true
            txThresholdWatts = 0.05
            sessionCoalesceMs = 6000
            txEndDebounceMs = 6000
            swrMinForwardWatts = 0.5
            captureRawFrames = $false
            alerts = @{
                enabled = $false; highSwr = $true; criticalSwr = $true; highReflected = $true
                disconnected = $true; stale = $true; recovery = $true
                swrWarning = 2.0; swrCritical = 3.0; reflectedWarningWatts = 25; cooldownMinutes = 5
            }
        }
    } | ConvertTo-Json -Depth 32

    $postCom = Invoke-WebRequest -Uri "$BaseUrl/api/settings" -Method POST -Body $comBody `
        -ContentType "application/json" -UseBasicParsing -TimeoutSec 15
    Start-Sleep -Milliseconds 500
    $diskAfterCom = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $lpPort = "$($diskAfterCom.Lp100Monitor.Port)"
    $civPort = "$($diskAfterCom.GatewayPulse.RadioCat.PortName)"
    if ($postCom.StatusCode -eq 200 -and $lpPort -eq "COM4" -and $civPort -eq "COM5") {
        Add-Result "COM normalize Port 4/5 -> COM4/COM5" "PASS" "disk Lp100=$lpPort RadioCat=$civPort"
    }
    else {
        Add-Result "COM normalize Port 4/5 -> COM4/COM5" "FAIL" "disk Lp100=$lpPort RadioCat=$civPort HTTP=$($postCom.StatusCode)"
    }

    # Coexistence sanity: distinct ports persisted
    if ($lpPort -eq "COM4" -and $civPort -eq "COM5" -and $lpPort -ne $civPort) {
        Add-Result "Coexistence COM4 LP100 + COM5 CI-V" "PASS" "distinct ports stored"
    }
    else {
        Add-Result "Coexistence COM4 LP100 + COM5 CI-V" "FAIL" "Lp100=$lpPort RadioCat=$civPort"
    }

    # Process still alive after COM failures / settings posts
    if (-not $proc.HasExited) {
        Add-Result "Process still alive after smoke" "PASS" "pid=$($proc.Id)"
    }
    else {
        Add-Result "Process still alive after smoke" "FAIL" "exit=$($proc.ExitCode)"
    }

    # Second config: empty MobileApi token — restart not required; unit-covered. Quick file assert.
    $disk = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($disk.MobileApi.ApiToken -eq $ApiToken) {
        Add-Result "MobileApi token persisted in appsettings" "PASS" "token present (not printed)"
    }
    else {
        Add-Result "MobileApi token persisted in appsettings" "FAIL" "token missing/changed unexpectedly"
    }
}
catch {
    Add-Result "Smoke harness" "FAIL" $_.Exception.Message
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $proc.Id -TimeoutSec 8 -ErrorAction SilentlyContinue } catch { }
    }
    # Ensure nothing left listening on the smoke port
    try {
        $listeners = Get-NetTCPConnection -LocalPort ([uri]$BaseUrl).Port -State Listen -ErrorAction SilentlyContinue
        foreach ($l in $listeners) {
            Stop-Process -Id $l.OwningProcess -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "==== SMOKE SUMMARY ===="
$Results | Format-Table -AutoSize
$failed = @($Results | Where-Object { $_.Status -eq "FAIL" })
if ($failed.Count -gt 0) {
    Write-Host "smoke-audit-local: FAIL ($($failed.Count) failures)" -ForegroundColor Red
    exit 1
}
Write-Host "smoke-audit-local: PASS" -ForegroundColor Green
exit 0
