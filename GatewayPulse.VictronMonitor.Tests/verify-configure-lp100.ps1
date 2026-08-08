$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ConfigureScript = Join-Path $Root 'GatewayPulse.Service\configure-lp100.ps1'
$TestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('gateway-pulse-lp100-config-' + [guid]::NewGuid().ToString('N'))
$ConfigPath = Join-Path $TestRoot 'appsettings.json'

New-Item -ItemType Directory -Path $TestRoot | Out-Null
try {
    @'
{
  "GatewayPulse": {
    "GatewayName": "TestGW",
    "Callsign": "N0TEST",
    "RadioCat": {
      "Enabled": true,
      "Mode": "CivCom",
      "PortName": "COM5",
      "BaudRate": 19200,
      "CivAddress": "94"
    }
  },
  "VictronMonitor": { "Enabled": false, "KeepMe": true },
  "Unrelated": { "PreserveMe": "yes" }
}
'@ | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

    & $ConfigureScript -AppSettingsPath $ConfigPath -Port 4 -Enable -AutoDetect
    if ($LASTEXITCODE -notin @(0, $null)) { throw "configure-lp100 failed: $LASTEXITCODE" }

    $configured = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($configured.Unrelated.PreserveMe -ne 'yes') { throw 'Unrelated settings were not preserved.' }
    if ($configured.VictronMonitor.KeepMe -ne $true) { throw 'VictronMonitor was wiped.' }
    if ($configured.GatewayPulse.RadioCat.PortName -ne 'COM5') { throw 'RadioCat PortName was wiped.' }
    if ($configured.GatewayPulse.RadioCat.Enabled -ne $true) { throw 'RadioCat Enabled was wiped.' }
    if ($configured.Lp100Monitor.Port -ne 'COM4') { throw 'LP-100 port was not normalized to COM4.' }
    if ($configured.Lp100Monitor.Enabled -ne $true) { throw 'LP-100 was not enabled.' }
    if ($configured.Lp100Monitor.IntervalMs -ne 80) { throw 'LP-100 IntervalMs should be 80.' }
    if ($configured.RfMonitoring.TelemetryPath -ne 'C:\PWM\RfTelemetry.json') { throw 'RfMonitoring TelemetryPath missing.' }

    # Empty Lp100Monitor + RfMonitoring objects (PowerShell missing-property trap).
    @'
{
  "GatewayPulse": { "RadioCat": { "Enabled": true, "PortName": "COM9" } },
  "Lp100Monitor": {},
  "RfMonitoring": {}
}
'@ | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
    & $ConfigureScript -AppSettingsPath $ConfigPath -Port '7' -Enable
    $emptyCase = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($emptyCase.GatewayPulse.RadioCat.PortName -ne 'COM9') { throw 'RadioCat wiped on empty Lp100Monitor case.' }
    if ($emptyCase.Lp100Monitor.Port -ne 'COM7') { throw 'Bare port 7 was not normalized.' }
    if (-not $emptyCase.Lp100Monitor.ExecutablePath) { throw 'ExecutablePath was not created on empty object.' }

    Write-Host 'verify-configure-lp100: PASS'
}
finally {
    Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
}
