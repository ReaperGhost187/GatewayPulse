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
