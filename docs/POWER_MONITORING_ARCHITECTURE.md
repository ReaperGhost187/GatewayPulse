# Gateway Pulse power monitoring architecture

## Runtime data flow

Gateway Pulse keeps BLE and vendor protocol handling outside the web service. Version 1.2 uses one Windows BLE advertisement watcher for every configured Victron device:

```text
BatteryProtect advertisements ---\
                                  > VictronBleScanner (one watcher)
SmartShunt advertisements -------/
             |
             +-- address -> BatteryProtectDecoder
             +-- address -> SmartShuntDecoder
             |
             v
       VictronPowerManager
       - isolates provider failures
       - rejects near-duplicate advertisements
       - applies per-device stale state
       - calculates normalized system state
       - records transition-only power events
             |
             | atomic same-directory file replacement
             v
       C:\PWM\PowerTelemetry.json (schemaVersion 2)
             |
       JsonFilePowerMonitor
             |
       /api/power -> adaptive Power System card
```

The original single-device `--device` mode and schema-v1 top-level fields remain available for backward compatibility. Production service supervision uses `--multi-device --config <appsettings.json>`.

## Components

### `GatewayPulse.PowerMonitoring`

- `IPowerMonitor` remains the service-facing provider-neutral boundary.
- `PowerDeviceTelemetry` contains one device snapshot. Unsupported values are nullable and omitted from JSON.
- `PowerSystemTelemetry` is the normalized system view consumed by the dashboard.
- `PowerSystemComposer` prefers a fresh SmartShunt for voltage/current/SOC/runtime and combines BatteryProtect output/alarm state.
- `PowerTelemetry` is the schema envelope. Existing top-level fields remain populated while schema v2 adds `updatedAt`, `system`, `devices`, and transition-only `events`.
- `PowerTelemetryJson.WriteFileAtomicallyAsync` writes a unique temporary file in the destination directory and atomically replaces the published snapshot.
- `JsonFilePowerMonitor` independently validates timestamps and applies stale state per device, so one stale device does not hide a healthy peer.

### `GatewayPulse.VictronMonitor`

- `WindowsBleAdvertisementSource` is the shared scanner.
- `IPowerProvider` is the internal decoder/provider contract.
- `BatteryProtectDecoder` and `SmartShuntDecoder` own separate copied key buffers and clear them on disposal.
- `VictronPowerManager` routes by normalized Bluetooth address, catches malformed/decryption failures per provider, and leaves other providers running.
- `VictronMultiDeviceConfiguration` validates each enabled device independently. A missing SmartShunt address/key produces a generic unavailable state and log entry, not a Gateway Pulse service failure.
- BLE watcher errors are restarted after a delay. The Windows service supervisor separately restarts a collector process that exits.

### `GatewayPulse.Service`

The service starts one child collector using absolute executable, configuration, telemetry, and log paths. A Windows job object kills the collector tree if the service exits. Windows service recovery restarts Gateway Pulse after failure and automatic startup starts it after reboot.

The service itself only reads the JSON boundary. `/api/power` is LAN-readable; settings and test-alert endpoints retain their loopback-only policy.

## Normalization rules

- SmartShunt is authoritative for current, SOC, consumed Ah, time remaining, and power state.
- Signed watts are calculated as voltage × current only when both values are valid.
- Positive current is `Charging`, negative current is `Discharging`, and absolute current at or below the configurable idle threshold is `Idle`.
- BatteryProtect is authoritative for output-enabled state.
- Alarm reasons from connected devices are combined without inventing missing fields.
- SOC thresholds take precedence over voltage thresholds whenever fresh SmartShunt SOC exists.
- An enabled stale SmartShunt is critical by default. Other disconnected/misconfigured devices produce warning status unless an alarm, disabled BatteryProtect output, critical SOC, or critical voltage applies.
- Device `Connected`, `Stale`, and `ConnectionState` are separate so stale telemetry is distinguishable from never-connected/misconfigured telemetry.

## Transition events

The manager records bounded, transition-only events: device connect/disconnect, stale/recovered, charging/discharging/idle, SOC warning/critical, BatteryProtect output disabled/restored, and alarm raised/cleared. It does not emit an event for every advertisement or small measurement change. `/api/power.events` is merged into the dashboard Recent Activity list.

## Security boundary

Each enabled device has its own ACL-protected key file. Configuration stores only absolute key-file paths. Key values are never passed as process arguments, serialized, returned by APIs, rendered by the dashboard, or included in logs/errors. BLE Instant Readout is monitoring-only and must not be used as a safety interlock because AES-CTR advertisements do not include a cryptographic authentication tag.
