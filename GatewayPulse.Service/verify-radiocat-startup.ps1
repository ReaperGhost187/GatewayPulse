# Smoke-test: with RadioCat enabled against a nonexistent COM port, Kestrel must
# still bind and serve /api/status (HTTP 200) within a short window.
param(
    [string]$Url = "http://127.0.0.1:18080",
    [int]$ReadySeconds = 45
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $PSScriptRoot "GatewayPulse.Service.csproj"))) {
    $Root = $PSScriptRoot
    $ServiceProj = Join-Path $Root "GatewayPulse.Service\GatewayPulse.Service.csproj"
    $ServiceDir = Join-Path $Root "GatewayPulse.Service"
} else {
    $ServiceProj = Join-Path $PSScriptRoot "GatewayPulse.Service.csproj"
    $ServiceDir = $PSScriptRoot
    $Root = Split-Path -Parent $PSScriptRoot
}

$tempDir = Join-Path $env:TEMP ("gp-radiocat-startup-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$settingsPath = Join-Path $tempDir "appsettings.json"

$settings = @{
    urls = $Url
    GatewayPulse = @{
        GatewayName = "Startup Test"
        Callsign = "N0TEST"
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
    Alerts = @{ RelayOffline = $false; TrimodeOffline = $false; ScannerStopped = $false; Recovery = $false; StationConnected = $false }
    Dashboard = @{ DemoMode = $true; RefreshSeconds = 5; LiveRadioSeconds = 2 }
    PowerMonitoring = @{
        TelemetryPath = (Join-Path $tempDir "PowerTelemetry.json")
        HistoryPath = (Join-Path $tempDir "PowerHistory.json")
        HistorySampleSeconds = 30
        StaleAfterSeconds = 30
    }
    VictronMonitor = @{ Enabled = $false; Devices = @() }
    NetworkMap = @{ ServiceCode = ""; RememberServiceCode = $true; AutoRefresh = $false; AutoRefreshMinutes = 10; AutoOpenInBrowser = $false; MapUrl = "https://example.invalid/" }
    RfMonitoring = @{
        TelemetryPath = (Join-Path $tempDir "RfTelemetry.json")
        HistoryPath = (Join-Path $tempDir "RfHistory.json")
        AnalysisPath = (Join-Path $tempDir "RfAnalysis.json")
        SwrByFrequencyPath = (Join-Path $tempDir "RfSwrByFrequency.json")
        TransmissionHistoryPath = (Join-Path $tempDir "RfTransmissionHistory.json")
        HistorySampleSeconds = 5
        StaleAfterSeconds = 10
    }
    MobileApi = @{ ApiToken = "" }
    Lp100Monitor = @{ Enabled = $false; Port = ""; AutoDetect = $false }
}
$settings | ConvertTo-Json -Depth 32 | Set-Content -Path $settingsPath -Encoding UTF8
New-Item -ItemType Directory -Path (Join-Path $tempDir "relay-logs") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $tempDir "trimode-logs") | Out-Null

Write-Host "Building GatewayPulse.Service..."
dotnet build $ServiceProj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $ServiceDir "bin\Release\net8.0-windows\GatewayPulse.dll"
if (-not (Test-Path $dll)) {
    $dll = Get-ChildItem -Path (Join-Path $ServiceDir "bin\Release") -Recurse -Filter "GatewayPulse.dll" |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $dll) { throw "GatewayPulse.dll not found after build." }

Write-Host "Starting service with RadioCat -> COM99 (content root: $tempDir)..."
$prevAspNet = $env:ASPNETCORE_ENVIRONMENT
$prevDotNet = $env:DOTNET_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:DOTNET_ENVIRONMENT = "Production"
try {
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList @("`"$dll`"", "--urls", "`"$Url`"") `
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

$statusUrl = ($Url.TrimEnd('/') + "/api/status")
$deadline = (Get-Date).AddSeconds($ReadySeconds)
$ok = $false
$lastError = ""
try {
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $err = Get-Content (Join-Path $tempDir "stderr.log") -Raw -ErrorAction SilentlyContinue
            $out = Get-Content (Join-Path $tempDir "stdout.log") -Raw -ErrorAction SilentlyContinue
            throw "Process exited early with code $($proc.ExitCode). stderr=`n$err`nstdout=`n$out"
        }
        try {
            $resp = Invoke-WebRequest -Uri $statusUrl -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) {
                Write-Host "OK: /api/status returned 200 while RadioCat points at missing COM99"
                Write-Host $resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length))
                $ok = $true
                break
            }
            $lastError = "HTTP $($resp.StatusCode)"
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ok) {
        $err = Get-Content (Join-Path $tempDir "stderr.log") -Raw -ErrorAction SilentlyContinue
        $out = Get-Content (Join-Path $tempDir "stdout.log") -Raw -ErrorAction SilentlyContinue
        throw "Timed out waiting for $statusUrl. Last error: $lastError`nstderr=`n$err`nstdout=`n$out"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $proc.Id -TimeoutSec 5 -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}

Write-Host "verify-radiocat-startup: PASS"
exit 0
