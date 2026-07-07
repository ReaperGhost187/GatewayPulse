<img width="1166" height="1349" alt="Dashboard" src="https://github.com/user-attachments/assets/5b01d438-a199-4d2b-8b20-4adb9b1f43e5" />
<img width="1336" height="1183" alt="settings" src="https://github.com/user-attachments/assets/f039ee19-d5af-45ea-b783-aa3bb1fedf48" />
# Gateway Pulse v0.8 Clean Service Solution

Gateway Pulse is a read-only monitoring dashboard for RMS Relay and RMS Trimode gateways.

## Projects

- `GatewayPulse.Core` — monitoring logic, memory reader, log parser, Pushover
- `GatewayPulse.Service` — Windows Service host, Kestrel web dashboard

## Test in console mode

```powershell
cd .\GatewayPulse.Service
.\run-console.ps1
```

Open:

```text
http://127.0.0.1:8080
```

## Publish

```powershell
cd .\GatewayPulse.Service
.\publish.ps1
```

## Install service

Run PowerShell as Administrator:

```powershell
cd .\GatewayPulse.Service\publish
.\install-service.ps1
```

## Remove service

Run PowerShell as Administrator:

```powershell
cd .\GatewayPulse.Service\publish
.\uninstall-service.ps1
```

## Installer direction

For v1.0, normal users should not use PowerShell. The release goal is a normal Windows installer that asks for gateway name, callsign, log paths, Pushover keys, alert toggles, installs the service, adds firewall rule, and creates shortcuts.

See `INSTALLER_PLAN.md`.

## Important

Keep v0.7 stable untouched. Treat this as v0.8 development.
