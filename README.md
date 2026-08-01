# Gateway Pulse v1.2 Multi-Device Power

Gateway Pulse is a read-only Windows monitoring dashboard for RMS Relay and RMS Trimode gateways. This cloned development line preserves the proven Smart BatteryProtect integration and adds simultaneous Victron SmartShunt 300A Instant Readout support.

The original working source remains untouched at:

```text
E:\GP-fromgpt\GatewayPulse_v1_Simple_Operator_Dashboard
```

This extension is developed at:

```text
E:\GP-fromgpt\GatewayPulse_v2_Multi_Device_Power
```

## Projects

- `GatewayPulse.Core` — gateway monitoring, memory/log parsing, and Pushover
- `GatewayPulse.Service` — Windows Service, Kestrel API, dashboard, and collector supervision
- `GatewayPulse.Tray` — notification-area client
- `GatewayPulse.PowerMonitoring` — provider-neutral schema-v2 telemetry, composition, atomic JSON, and file reader
- `GatewayPulse.VictronMonitor` — shared BLE scanner, BatteryProtect/SmartShunt decoders, multi-device manager, scan/device/mock modes
- `GatewayPulse.VictronMonitor.Tests` — protocol, provider, manager, configuration, API/file, supervisor, and dashboard-state tests

## Power flow

```text
one BLE watcher -> address-specific decoders -> VictronPowerManager
-> atomic C:\PWM\PowerTelemetry.json -> /api/power -> Power System card
```

Every device has its own address and ACL-protected key file. Key values are never accepted as multi-device process arguments or emitted in telemetry, APIs, logs, or dashboard output. SmartShunt remains disabled until its real address and key file are supplied; BatteryProtect continues independently.

## Canonical build

Run from an ordinary PowerShell window on the development PC:

```powershell
cd E:\GP-fromgpt\GatewayPulse_v2_Multi_Device_Power
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

The build runs Release .NET tests, Power System JavaScript state tests, multi-device configuration/ACL tests, four self-contained Windows publishes (service, tray, Victron, LP-100A), and Inno Setup compilation. Output:

```text
Installer_Output\GatewayPulseSetup_v1.2.4.exe
```

## Documentation

- [`docs/POWER_MONITORING_ARCHITECTURE.md`](docs/POWER_MONITORING_ARCHITECTURE.md)
- [`docs/VICTRON_BLE_PROTOCOL.md`](docs/VICTRON_BLE_PROTOCOL.md)
- [`docs/GATEWAY_DEPLOYMENT.md`](docs/GATEWAY_DEPLOYMENT.md)
- [`GatewayPulse.VictronMonitor/README.md`](GatewayPulse.VictronMonitor/README.md)

Do not modify the live gateway until the v1.2 installer is fully built, hashed, and verified and the rollback backup has been captured.
