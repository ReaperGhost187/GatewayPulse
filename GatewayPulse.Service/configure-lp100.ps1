param(
    [string]$AppSettingsPath = "",
    [string]$Port = "",
    [switch]$AutoDetect,
    [int]$BaudRate = 115200,
    [switch]$Enable,
    [switch]$Disable
)

$ErrorActionPreference = "Stop"

function Normalize-SerialPortName {
    param([string]$PortName)
    if ([string]::IsNullOrWhiteSpace($PortName)) { return "" }
    $trimmed = $PortName.Trim().ToUpperInvariant()
    if ($trimmed.StartsWith('\\.\')) { $trimmed = $trimmed.Substring(4) }
    if ($trimmed -match '^COM\d+$') { return $trimmed }
    if ($trimmed -match '^\d+$' -and [int]$trimmed -gt 0) { return "COM$trimmed" }
    return $trimmed
}

if (-not $AppSettingsPath) {
    $AppSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
}
if (-not (Test-Path $AppSettingsPath)) {
    throw "appsettings.json not found: $AppSettingsPath"
}

New-Item -ItemType Directory -Force -Path "C:\PWM" | Out-Null
New-Item -ItemType Directory -Force -Path "C:\PWM\logs" | Out-Null

$json = Get-Content $AppSettingsPath -Raw | ConvertFrom-Json
if (-not $json.Lp100Monitor) { $json | Add-Member -NotePropertyName Lp100Monitor -NotePropertyValue ([pscustomobject]@{}) }
if (-not $json.RfMonitoring) { $json | Add-Member -NotePropertyName RfMonitoring -NotePropertyValue ([pscustomobject]@{}) }

$json.Lp100Monitor.ExecutablePath = "Lp100Monitor\GatewayPulse.Lp100Monitor.exe"
$json.Lp100Monitor.OutputPath = "C:\PWM\RfTelemetry.json"
$json.Lp100Monitor.LogsPath = "C:\PWM\logs"
$json.Lp100Monitor.BaudRate = $BaudRate
$json.Lp100Monitor.IntervalMs = 250
$json.Lp100Monitor.IdleIntervalMs = 1000
$json.Lp100Monitor.RestartDelaySeconds = 10
$json.Lp100Monitor.HistoryEnabled = $true
if ($PSBoundParameters.ContainsKey("Port")) { $json.Lp100Monitor.Port = (Normalize-SerialPortName $Port) }
if ($AutoDetect) { $json.Lp100Monitor.AutoDetect = $true }
if ($Disable) { $json.Lp100Monitor.Enabled = $false }
elseif ($Enable) { $json.Lp100Monitor.Enabled = $true }

$json.RfMonitoring.TelemetryPath = "C:\PWM\RfTelemetry.json"
$json.RfMonitoring.HistoryPath = "C:\PWM\RfHistory.json"
$json.RfMonitoring.TransmissionHistoryPath = "C:\PWM\RfTransmissionHistory.json"
$json.RfMonitoring.HistorySampleSeconds = 5
$json.RfMonitoring.StaleAfterSeconds = 10

$json | ConvertTo-Json -Depth 32 | Set-Content -Path $AppSettingsPath -Encoding UTF8
Write-Host "Gateway Pulse LP-100A configuration updated."
Write-Host "Enabled: $($json.Lp100Monitor.Enabled)  Port: $($json.Lp100Monitor.Port)  Baud: $($json.Lp100Monitor.BaudRate)"
Write-Host "Telemetry: C:\PWM\RfTelemetry.json"
