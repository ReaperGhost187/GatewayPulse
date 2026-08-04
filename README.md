<img width="1166" height="1349" alt="Dashboard" src="https://github.com/user-attachments/assets/5b01d438-a199-4d2b-8b20-4adb9b1f43e5" />
<img width="1336" height="1183" alt="settings" src="https://github.com/user-attachments/assets/f039ee19-d5af-45ea-b783-aa3bb1fedf48" />

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

- `GatewayPulse.Core` ΓÇö gateway monitoring, memory/log parsing, and Pushover
- `GatewayPulse.Service` ΓÇö Windows Service, Kestrel API, dashboard, and collector supervision
- `GatewayPulse.Tray` ΓÇö notification-area client
- `GatewayPulse.PowerMonitoring` ΓÇö provider-neutral schema-v2 telemetry, composition, atomic JSON, and file reader
- `GatewayPulse.VictronMonitor` ΓÇö shared BLE scanner, BatteryProtect/SmartShunt decoders, multi-device manager, scan/device/mock modes
- `GatewayPulse.VictronMonitor.Tests` ΓÇö protocol, provider, manager, configuration, API/file, supervisor, and dashboard-state tests

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
Installer_Output\GatewayPulseSetup_v1.2.8.exe
```

## Documentation

- [`docs/POWER_MONITORING_ARCHITECTURE.md`](docs/POWER_MONITORING_ARCHITECTURE.md)
- [`docs/VICTRON_BLE_PROTOCOL.md`](docs/VICTRON_BLE_PROTOCOL.md)
- [`docs/GATEWAY_DEPLOYMENT.md`](docs/GATEWAY_DEPLOYMENT.md)
- [`GatewayPulse.VictronMonitor/README.md`](GatewayPulse.VictronMonitor/README.md)

Do not modify the live gateway until the v1.2 installer is fully built, hashed, and verified and the rollback backup has been captured.
