# GatewayPulse.VictronMonitor

Independent .NET 8 Windows collector for simultaneous Victron Smart BatteryProtect and SmartShunt Instant Readout telemetry.

## Production multi-device mode

```powershell
.\GatewayPulse.VictronMonitor.exe --multi-device `
  --config "C:\Program Files\Gateway Pulse\Service\appsettings.json" `
  --output "C:\PWM\PowerTelemetry.json" `
  --logs "C:\PWM\logs" `
  --interval 5
```

The config file contains device addresses and absolute key-file paths, never key values. One BLE watcher routes advertisements to independent address-specific decoders. An invalid/offline device does not stop its peers.

## Diagnostic and compatibility modes

```powershell
# Discover BLE advertisers and write JSONL scan records
.\GatewayPulse.VictronMonitor.exe --scan --logs "C:\PWM\logs"

# Backward-compatible single BatteryProtect mode
.\GatewayPulse.VictronMonitor.exe --device `
  --address "AA:BB:CC:DD:EE:FF" `
  --key-file "C:\PWM\victron.key" `
  --output "C:\PWM\PowerTelemetry.json"

# Hardware-free development sample (never write under C:\PWM without --force-demo)
.\GatewayPulse.VictronMonitor.exe --mock --once `
  --output ".\PowerTelemetry.json" `
  --logs ".\logs"
```

For the Gateway Pulse service Demo Mode, set `Dashboard:DemoMode` to `true` in appsettings (off by default). That launches the collector with `--mock --force-demo`. Production builds must leave Demo Mode off.

Direct key values are deliberately rejected on command lines. Mock mode refuses `C:\PWM` unless `--force-demo` or `GATEWAYPULSE_ALLOW_MOCK=1` is set. Single-device mode can alternatively read `GATEWAYPULSE_VICTRON_KEY`, but production multi-device mode requires separate protected key files.
Scan-mode records redact Victron's cleartext one-byte key check before they are written. Production multi-device mode records transition/status logs, not every raw advertisement.

## Configuration

```json
{
  "VictronMonitor": {
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
        "Address": "",
        "KeyFile": "C:\\PWM\\smartshunt.key",
        "Enabled": false
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
}
```

Device type and JSON property names are case-insensitive. Enabled device addresses must be unique and key-file paths absolute. Disabled devices are omitted from telemetry/UI. An enabled but invalid device appears as misconfigured while other usable devices continue.

## Schema-v2 output

The envelope preserves schema-v1 top-level fields and adds:

- `schemaVersion: 2`
- `updatedAt`
- normalized `system`
- per-device `devices`
- bounded transition-only `events`

Unsupported optional fields are omitted rather than reported as zero. File publication uses atomic same-directory replacement.

## Build and test

```powershell
dotnet test .\GatewayPulse.VictronMonitor.Tests\GatewayPulse.VictronMonitor.Tests.csproj -c Release
node .\GatewayPulse.VictronMonitor.Tests\DashboardPowerSystemTests.js
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\GatewayPulse.VictronMonitor.Tests\verify-configure-victron.ps1

dotnet publish .\GatewayPulse.VictronMonitor\GatewayPulse.VictronMonitor.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\Publish\VictronMonitor
```

See `docs/POWER_MONITORING_ARCHITECTURE.md`, `docs/VICTRON_BLE_PROTOCOL.md`, and `docs/GATEWAY_DEPLOYMENT.md`.
