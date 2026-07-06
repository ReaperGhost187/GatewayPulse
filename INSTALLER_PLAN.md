# Gateway Pulse Installer Plan

Goal for v1.0:

Download `GatewayPulseSetup.exe`, double-click, answer setup questions, and install.

No PowerShell for normal users.

## Installer should ask for

- Gateway name
- Callsign
- RMS Relay logs folder
- RMS Trimode logs folder
- RMS Trimode.ini path
- Dashboard port, default 8080
- Pushover enabled yes/no
- Pushover user key
- Pushover application token
- Pushover device, optional
- Alert toggles:
  - Relay offline
  - Trimode offline
  - Scanner stopped
  - Command port failed
  - Recovery
  - Station connected

## Installer should perform

- Install GatewayPulse.exe
- Write appsettings.json
- Install Windows Service
- Set service automatic startup
- Set service recovery restart actions
- Add firewall rule for dashboard port
- Add Start Menu shortcut to dashboard
- Add uninstall entry

## Preferred installer

Inno Setup.

PowerShell scripts remain only for development/testing.
