# Host Gateway Pulse locally with Demo Mode + LP-100A mock, using isolated C:\PWM\demo paths.
# Does NOT touch C:\PWM\*.json production files. Does NOT build the installer.
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$Demo = "C:\PWM\demo"
$Preview = Join-Path $PSScriptRoot "ui-preview"
$ServiceProject = Join-Path $Root "GatewayPulse.Service\GatewayPulse.Service.csproj"
$Lp100Project = Join-Path $Root "GatewayPulse.Lp100Monitor\GatewayPulse.Lp100Monitor.csproj"
$Port = 8080

# Prefer 8080; fall back to 8081 if busy.
$busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($busy) {
    $Port = 8081
    $busy2 = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($busy2) { throw "Ports 8080 and 8081 are both in use." }
}

# Seed / refresh isolated demo JSON from repo preview data (never touches C:\PWM\*.json production files).
$SeedDir = Join-Path $Preview "demo-data"
New-Item -ItemType Directory -Force -Path $Demo | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Demo "logs") | Out-Null
$seedFiles = @(
    "PowerTelemetry.json",
    "PowerHistory.json",
    "RfTransmissionHistory.json",
    "RfAnalysis.json",
    "RfSwrByFrequency.json"
)
foreach ($name in $seedFiles) {
    $src = Join-Path $SeedDir $name
    $dst = Join-Path $Demo $name
    if (Test-Path $src) {
        if (-not (Test-Path $dst) -or ((Get-Item $dst).Length -lt 32)) {
            Copy-Item $src $dst -Force
            Write-Host "Seeded $dst"
        }
    }
}
if (-not (Test-Path (Join-Path $Demo "RfTransmissionHistory.json"))) {
    throw "Demo seed data missing under $Demo (expected $SeedDir)."
}
if (-not (Test-Path (Join-Path $Demo "RfTelemetry.json"))) {
    @'
{
  "schemaVersion": 1,
  "updatedAt": "2026-08-04T00:00:00Z",
  "lastUpdate": "2026-08-04T00:00:00Z",
  "connected": true,
  "provider": "mock",
  "device": "Mock LP-100A",
  "connectionState": "Connected",
  "protocolStatus": "OK",
  "stale": false,
  "transmitting": false,
  "forwardPowerWatts": 0,
  "reflectedPowerWatts": 0,
  "lastPeakForwardPowerWatts": 95.4,
  "powerRange": "Mid",
  "meterMode": "Peak",
  "comPort": "MOCK",
  "baudRate": 115200
}
'@ | Set-Content -Path (Join-Path $Demo "RfTelemetry.json") -Encoding UTF8
}

# Stop prior mockup hosts for this project.
Get-CimInstance Win32_Process |
  Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*GatewayPulse.Service*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep 1

Write-Host "Building LP-100A + Victron monitors (Debug) for Demo Mode mock..."
dotnet build $Lp100Project -c Debug --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Lp100Monitor build failed." }
$lp100Exe = Get-ChildItem -Path (Join-Path $Root "GatewayPulse.Lp100Monitor\bin\Debug") -Recurse -Filter "GatewayPulse.Lp100Monitor.exe" |
  Select-Object -First 1
if ($null -eq $lp100Exe) { throw "GatewayPulse.Lp100Monitor.exe not found after build." }

$VictronProject = Join-Path $Root "GatewayPulse.VictronMonitor\GatewayPulse.VictronMonitor.csproj"
dotnet build $VictronProject -c Debug --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "VictronMonitor build failed." }
$victronExe = Get-ChildItem -Path (Join-Path $Root "GatewayPulse.VictronMonitor\bin\Debug") -Recurse -Filter "GatewayPulse.VictronMonitor.exe" |
  Select-Object -First 1
if ($null -eq $victronExe) { throw "GatewayPulse.VictronMonitor.exe not found after build." }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Dashboard__DemoMode = "true"
$env:Dashboard__RefreshSeconds = "3"

# Isolate all PWM paths under C:\PWM\demo (do not use production C:\PWM\*.json).
$env:PowerMonitoring__TelemetryPath = (Join-Path $Demo "PowerTelemetry.json")
$env:PowerMonitoring__HistoryPath = (Join-Path $Demo "PowerHistory.json")
$env:PowerMonitoring__StaleAfterSeconds = "300"

# DemoMode forces Victron mock even when Enabled=false — point it at the Debug build + demo paths.
$env:VictronMonitor__Enabled = "true"
$env:VictronMonitor__ExecutablePath = $victronExe.FullName
$env:VictronMonitor__OutputPath = (Join-Path $Demo "PowerTelemetry.json")
$env:VictronMonitor__LogsPath = (Join-Path $Demo "logs")

$env:RfMonitoring__TelemetryPath = (Join-Path $Demo "RfTelemetry.json")
$env:RfMonitoring__HistoryPath = (Join-Path $Demo "RfHistory.json")
$env:RfMonitoring__AnalysisPath = (Join-Path $Demo "RfAnalysis.json")
$env:RfMonitoring__SwrByFrequencyPath = (Join-Path $Demo "RfSwrByFrequency.json")
$env:RfMonitoring__TransmissionHistoryPath = (Join-Path $Demo "RfTransmissionHistory.json")
$env:RfMonitoring__StaleAfterSeconds = "120"

$env:Lp100Monitor__Enabled = "true"
$env:Lp100Monitor__ExecutablePath = $lp100Exe.FullName
$env:Lp100Monitor__OutputPath = (Join-Path $Demo "RfTelemetry.json")
$env:Lp100Monitor__LogsPath = (Join-Path $Demo "logs")
$env:Lp100Monitor__HistoryEnabled = "true"
$env:Lp100Monitor__IntervalMs = "80"
$env:Lp100Monitor__IdleIntervalMs = "1000"
$env:Lp100Monitor__SessionCoalesceMs = "6000"
$env:Lp100Monitor__TxEndDebounceMs = "6000"
$env:Lp100Monitor__SwrMinForwardWatts = "0.5"
$env:Lp100Monitor__TxThresholdWatts = "0.05"

$url = "http://127.0.0.1:$Port"
Write-Host ""
Write-Host "Gateway Pulse RF UI mockup (Demo Mode + LP-100A mock)"
Write-Host "  Dashboard:    $url/"
Write-Host "  RF Analysis:  $url/rf-analysis.html"
Write-Host "  Settings:     $url/settings.html"
Write-Host "  Demo data:    $Demo"
Write-Host "  Static preview copies: $Preview\demo-data and $Preview\rf-analysis-preview.html"
Write-Host "Press Ctrl+C to stop."
Write-Host ""

Set-Location (Join-Path $Root "GatewayPulse.Service")
dotnet run --project $ServiceProject --no-launch-profile --urls $url
