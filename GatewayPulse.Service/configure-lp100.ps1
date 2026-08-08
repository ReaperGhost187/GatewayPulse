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

function Set-JsonNoteProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value
    )
    # PowerShell ConvertFrom-Json objects reject assignment of missing properties.
    # Always Add-Member -Force so old/partial production appsettings still configure cleanly.
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

if (-not $AppSettingsPath) {
    $AppSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
}
if (-not (Test-Path $AppSettingsPath)) {
    throw "appsettings.json not found: $AppSettingsPath"
}

New-Item -ItemType Directory -Force -Path "C:\PWM" | Out-Null
New-Item -ItemType Directory -Force -Path "C:\PWM\logs" | Out-Null

$configurationAcl = Get-Acl -LiteralPath $AppSettingsPath
$json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
if ($null -eq $json.Lp100Monitor) {
    $json | Add-Member -NotePropertyName Lp100Monitor -NotePropertyValue ([pscustomobject]@{}) -Force
}
if ($null -eq $json.RfMonitoring) {
    $json | Add-Member -NotePropertyName RfMonitoring -NotePropertyValue ([pscustomobject]@{}) -Force
}

$lp100 = $json.Lp100Monitor
Set-JsonNoteProperty $lp100 'ExecutablePath' "Lp100Monitor\GatewayPulse.Lp100Monitor.exe"
Set-JsonNoteProperty $lp100 'OutputPath' "C:\PWM\RfTelemetry.json"
Set-JsonNoteProperty $lp100 'LogsPath' "C:\PWM\logs"
Set-JsonNoteProperty $lp100 'BaudRate' $BaudRate
# Match service/UI default (PACTOR snapshot cadence). Do not regress to 250 ms.
Set-JsonNoteProperty $lp100 'IntervalMs' 80
Set-JsonNoteProperty $lp100 'IdleIntervalMs' 1000
Set-JsonNoteProperty $lp100 'RestartDelaySeconds' 10
Set-JsonNoteProperty $lp100 'HistoryEnabled' $true
if ($PSBoundParameters.ContainsKey("Port")) {
    Set-JsonNoteProperty $lp100 'Port' (Normalize-SerialPortName $Port)
}
if ($AutoDetect) {
    Set-JsonNoteProperty $lp100 'AutoDetect' $true
}
if ($Disable) {
    Set-JsonNoteProperty $lp100 'Enabled' $false
}
elseif ($Enable) {
    Set-JsonNoteProperty $lp100 'Enabled' $true
}

$rf = $json.RfMonitoring
Set-JsonNoteProperty $rf 'TelemetryPath' "C:\PWM\RfTelemetry.json"
Set-JsonNoteProperty $rf 'HistoryPath' "C:\PWM\RfHistory.json"
Set-JsonNoteProperty $rf 'AnalysisPath' "C:\PWM\RfAnalysis.json"
Set-JsonNoteProperty $rf 'SwrByFrequencyPath' "C:\PWM\RfSwrByFrequency.json"
Set-JsonNoteProperty $rf 'TransmissionHistoryPath' "C:\PWM\RfTransmissionHistory.json"
Set-JsonNoteProperty $rf 'HistorySampleSeconds' 5
Set-JsonNoteProperty $rf 'StaleAfterSeconds' 10

# Atomic write; never touch GatewayPulse.RadioCat / VictronMonitor / other siblings beyond Lp100+Rf paths above.
$temporaryPath = "$AppSettingsPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Set-Acl -LiteralPath $temporaryPath -AclObject $configurationAcl
    Move-Item -LiteralPath $temporaryPath -Destination $AppSettingsPath -Force
    Set-Acl -LiteralPath $AppSettingsPath -AclObject $configurationAcl
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Gateway Pulse LP-100A configuration updated."
Write-Host "Enabled: $($lp100.Enabled)  Port: $($lp100.Port)  Baud: $($lp100.BaudRate)"
Write-Host "Telemetry: C:\PWM\RfTelemetry.json"
