# Gateway Pulse v1.2 multi-device gateway deployment

This package upgrades the proven BatteryProtect integration without changing the live gateway until the installer is explicitly run. It adds optional SmartShunt 300A support and preserves a working BatteryProtect-only system when no SmartShunt is configured.

## Production paths

```text
Installer:          GatewayPulseSetup_v1.2.19.exe
Service:            C:\Program Files\Gateway Pulse\Service\GatewayPulse.exe
Collector:          C:\Program Files\Gateway Pulse\Service\VictronMonitor\GatewayPulse.VictronMonitor.exe
Configuration:      C:\Program Files\Gateway Pulse\Service\appsettings.json
BatteryProtect key: C:\PWM\victron.key
SmartShunt key:     C:\PWM\smartshunt.key
Telemetry:          C:\PWM\PowerTelemetry.json
Collector logs:     C:\PWM\logs
Dashboard:          http://127.0.0.1:8080
Power API:          http://127.0.0.1:8080/api/power
Service name:       GatewayPulse
```

Key **values** are never included in the installer, configuration, process arguments, telemetry, API, dashboard, or logs. Only absolute key-file paths are configured.

## 1. Back up the working gateway

Open **PowerShell as Administrator**:

```powershell
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$backup = "C:\PWM\GatewayPulse-backup-$stamp"
New-Item -ItemType Directory -Path $backup | Out-Null

sc.exe qc GatewayPulse | Out-File "$backup\service-qc.txt"
sc.exe qfailure GatewayPulse | Out-File "$backup\service-recovery.txt"
Get-CimInstance Win32_Service -Filter "Name='GatewayPulse'" |
    Format-List Name,State,StartName,PathName,StartMode |
    Out-File "$backup\service-cim.txt"

$installed = "C:\Program Files\Gateway Pulse"
if (Test-Path $installed) {
    Copy-Item $installed "$backup\Gateway Pulse" -Recurse
}
Copy-Item C:\PWM\PowerTelemetry.json $backup -ErrorAction SilentlyContinue
icacls C:\PWM\victron.key | Out-File "$backup\victron-key-acl.txt"
```

Do not move or replace the working BatteryProtect key.

## 2. Find the SmartShunt address

The SmartShunt may be discovered before its key is configured. Run the installed v1.2 collector in temporary diagnostic scan mode for about 30 seconds; `Ctrl+C` stops it:

```powershell
& "C:\Program Files\Gateway Pulse\Service\VictronMonitor\GatewayPulse.VictronMonitor.exe" `
    --scan --logs "C:\PWM\logs"
```

Then list only Victron Instant Readout advertisers:

```powershell
$scan = Get-ChildItem C:\PWM\logs\scan-*.jsonl |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Get-Content $scan.FullName | ForEach-Object {
    $row = $_ | ConvertFrom-Json
    $payload = $row.manufacturerData.'0x02E1'
    if ($payload -and $payload.Length -ge 10) {
        [pscustomobject]@{
            Address    = $row.address
            Name       = $row.deviceName
            RSSI       = $row.rssi
            RecordType = $payload.Substring(8, 2)
        }
    }
} | Sort-Object Address -Unique | Format-Table -AutoSize
```

`RecordType 02` is SmartShunt/battery-monitor telemetry; `09` is BatteryProtect. Confirm the address in VictronConnect before configuring it.
The diagnostic scan deliberately replaces Victron's one-byte key check with `00` before logging; this does not affect address or record-type discovery.

## 3. Place the SmartShunt key

In VictronConnect, open the SmartShunt's **Settings → Product Info**, enable Instant Readout, select **Show** beside Instant Readout Details, and record its device-specific key.

Create the exact destination from an elevated PowerShell window without putting the value in shell history:

```powershell
$secure = Read-Host 'Paste the 32-character SmartShunt Instant Readout key' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ($value -notmatch '^[0-9A-Fa-f]{32}$') { throw 'Key must be exactly 32 hexadecimal characters.' }
    [IO.File]::WriteAllText('C:\PWM\smartshunt.key', $value, [Text.Encoding]::ASCII)
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $value = $null
    $secure.Dispose()
}
```

Exact location:

```text
C:\PWM\smartshunt.key
```

The installer validates and applies the protected ACL. It never overwrites this file during an upgrade.

## 4. Copy and verify the release

Copy these two files to a staging folder on the gateway:

```text
GatewayPulseSetup_v1.2.19.exe
GatewayPulseSetup_v1.2.19.sha256.txt
```

Verify before running:

```powershell
Get-FileHash .\GatewayPulseSetup_v1.2.19.exe -Algorithm SHA256
Get-Content .\GatewayPulseSetup_v1.2.19.sha256.txt
```

The values must match exactly.

## 5. Install or upgrade

```powershell
Start-Process .\GatewayPulseSetup_v1.2.19.exe -Verb RunAs -Wait
```

Installer flow:

1. Keep **Install and supervise Victron BatteryProtect and SmartShunt power monitoring** selected.
2. Enter the BatteryProtect Bluetooth address and confirm `C:\PWM\victron.key` (on upgrades, existing BatteryProtect settings are preserved).
3. If the SmartShunt is not installed yet, leave **Configure or update SmartShunt monitoring now** unchecked. BatteryProtect continues alone.
4. If ready, select that option, enter the scanned SmartShunt address, and confirm `C:\PWM\smartshunt.key`.
5. Complete setup.

On an upgrade, the installed BatteryProtect address/key-file path and any existing SmartShunt configuration are preserved; the displayed BatteryProtect defaults are used only for a fresh configuration. Leaving the SmartShunt option unchecked preserves its existing state. Setup stops the service and tray, replaces binaries, merges only power settings, preserves unrelated JSON and the existing appsettings ACL, protects enabled-device key files, configures automatic startup and service recovery, and starts one supervised collector.

## 6. Final configuration format

The installed `VictronMonitor` section is:

```json
{
  "Enabled": true,
  "ExecutablePath": "VictronMonitor\\GatewayPulse.VictronMonitor.exe",
  "ConfigurationPath": "appsettings.json",
  "OutputPath": "C:\\PWM\\PowerTelemetry.json",
  "LogsPath": "C:\\PWM\\logs",
  "IntervalSeconds": 5,
  "Devices": [
    {
      "Type": "BatteryProtect",
      "Address": "D5:11:30:C1:55:16",
      "KeyFile": "C:\\PWM\\victron.key",
      "Enabled": true
    },
    {
      "Type": "SmartShunt",
      "Address": "AA:BB:CC:DD:EE:FF",
      "KeyFile": "C:\\PWM\\smartshunt.key",
      "Enabled": true
    }
  ],
  "Thresholds": {
    "StaleAfterSeconds": 30,
    "StateOfChargeWarningPercent": 30,
    "StateOfChargeCriticalPercent": 15,
    "WeakSignalRssi": -85,
    "IdleCurrentAmps": 0.2,
    "LowVoltageWarning": 11.8,
    "LowVoltageCritical": 11.0,
    "HighVoltageWarning": 15.0
  }
}
```

Replace the example SmartShunt address with the scanned value. Never put a key value in this file.

## 7. Verify service and ACLs

```powershell
Get-Service GatewayPulse
sc.exe qc GatewayPulse
sc.exe qfailure GatewayPulse
Get-CimInstance Win32_Service -Filter "Name='GatewayPulse'" |
    Select-Object Name,State,StartName,StartMode,PathName
Get-Process GatewayPulse.VictronMonitor | Select-Object Id,StartTime,Path

icacls "C:\PWM\victron.key"
icacls "C:\PWM\smartshunt.key"
```

Expected: service `Running`, automatic start, failure-restart actions present, and exactly one collector process. Key ACLs must be protected and grant read only to SYSTEM, Administrators, and any configured service account.

## 8. Verify both devices and updates

```powershell
$p = Invoke-RestMethod http://127.0.0.1:8080/api/power
$p.schemaVersion
$p.system | Format-List *
$p.devices | Format-Table type,connected,stale,connectionState,deviceId,rssi,lastUpdate -AutoSize
```

Expected:

- `schemaVersion` is `2`;
- both device rows exist when both are enabled;
- both report `connected: True`, `stale: False`, and recent `lastUpdate`;
- SmartShunt has real voltage, signed current, SOC, consumed Ah, and runtime when supplied by its Instant Readout packet;
- BatteryProtect has real output and alarm state;
- unsupported fields are absent/null, never fabricated zero.

Confirm timestamps advance independently:

```powershell
$first = Invoke-RestMethod http://127.0.0.1:8080/api/power
Start-Sleep -Seconds 10
$second = Invoke-RestMethod http://127.0.0.1:8080/api/power

foreach ($device in $second.devices) {
    $before = $first.devices | Where-Object type -eq $device.type | Select-Object -First 1
    [pscustomobject]@{
        Type       = $device.type
        Before     = $before.lastUpdate
        After      = $device.lastUpdate
        Connected  = $device.connected
        Stale      = $device.stale
    }
}
```

Inspect the atomic file directly:

```powershell
Get-Content C:\PWM\PowerTelemetry.json -Raw | ConvertFrom-Json |
    ConvertTo-Json -Depth 8
```

## 9. Verify the dashboard

```powershell
Start-Process http://127.0.0.1:8080
```

The adaptive **Power System** card should show battery status and available normalized metrics. With SmartShunt connected it adds SOC, signed current, signed watts, charging/discharging/idle state, consumed Ah, runtime, and an SOC bar. Compact SmartShunt and BatteryProtect rows show independent connection state, RSSI, age, output, and alarm details. Unavailable fields are hidden. Stale and disconnected use distinct labels. Power transitions appear in Recent Activity without advertisement-level flooding.

## 10. Restart and reboot recovery

Collector failure recovery:

```powershell
$before = Get-Process GatewayPulse.VictronMonitor -ErrorAction Stop
Stop-Process -Id $before.Id -Force
Start-Sleep -Seconds 20
$after = Get-Process GatewayPulse.VictronMonitor -ErrorAction Stop
$before.Id
$after.Id
Invoke-RestMethod http://127.0.0.1:8080/api/power | ConvertTo-Json -Depth 8
```

The PID must change and telemetry must recover.

Service restart:

```powershell
Restart-Service GatewayPulse
Start-Sleep -Seconds 20
Get-Service GatewayPulse
Get-Process GatewayPulse.VictronMonitor
Invoke-RestMethod http://127.0.0.1:8080/api/power | ConvertTo-Json -Depth 8
```

Reboot acceptance:

```powershell
Restart-Computer
```

After sign-in, rerun sections 7–9. Gateway Pulse and the collector must start without an interactive login action, and both devices must recover automatically.

## 11. Optional LP-100A RF monitoring (v1.2.4+)

During setup, optionally enable **Install LP-100A RF monitoring collector**. The binary is installed under `Service\Lp100Monitor\` but stays disabled until configured.

```powershell
cd "C:\Program Files\Gateway Pulse\Service"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\configure-lp100.ps1 -Enable -AutoDetect
# Or pin a COM port: -Enable -Port COM3
Restart-Service GatewayPulse
```

Telemetry lands at `C:\PWM\RfTelemetry.json`; TX events at `C:\PWM\RfTransmissionHistory.json`. Confirm on the dashboard Station RF card and `/api/rf`.

Polling timing is editable in Settings → LP-100A / RF Monitoring (`IntervalMs`, `IdleIntervalMs`, `SessionCoalesceMs`). For PACTOR, enable Peak Hold on the LP-100A (operator setting — Gateway Pulse never sends F/A/M), use TX poll ~50–80 ms, and session coalesce ~6000 ms so overs merge into one Transmission History session (new installs default coalesce to 6000; `TxEndDebounceMs` remains a legacy alias). Existing saved values are left unchanged on upgrade. Use **RF Analysis** (`/rf-analysis.html`) for synchronized timelines.

Optional live frequency (Settings → **Radio Frequency (CI-V)**). Preferred path is a dedicated CT-17 / USB CI-V COM port — not Trimode’s radio COM:

```json
"GatewayPulse": {
  "RadioCat": {
    "Enabled": true,
    "Mode": "CivCom",
    "PortName": "COM5",
    "BaudRate": 19200,
    "CivAddress": "94",
    "PollSeconds": 2,
    "Host": "127.0.0.1",
    "Port": 4532,
    "TimeoutMs": 400
  },
  "TrimodeProbe": {
    "CommandPortEnabled": false,
    "MemoryReadEnabled": false
  }
}
```

Use Settings → Test CI-V after the cable is connected. `Mode: "Rigctld"` remains available if you run Hamlib. Keep TrimodeProbe off when using CI-V. Without RadioCat, frequency falls back to Winlink/Trimode observations when those probes are enabled, otherwise Unknown / configured list only.

## 12. Rollback

If acceptance fails:

```powershell
Stop-Service GatewayPulse -Force
Remove-Item "C:\Program Files\Gateway Pulse" -Recurse -Force
Copy-Item "$backup\Gateway Pulse" "C:\Program Files\Gateway Pulse" -Recurse
Start-Service GatewayPulse
```

If the directory restore is blocked or service configuration differs, rerun the previous verified v1.1 installer and restore its `appsettings.json` from `$backup`. Do not delete or replace either key file during rollback. Never run a legacy standalone collector at the same time as the service-supervised collector.
