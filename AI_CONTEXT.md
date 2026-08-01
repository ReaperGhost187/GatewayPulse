# Gateway Pulse AI Context

## Project
Gateway Pulse

## Version
Current stable release: v1.0.0

## Purpose
Gateway Pulse is a read-only monitoring dashboard for Winlink RMS Relay and RMS Trimode gateways.

Gateway Pulse must:
- Never transmit
- Never control Winlink
- Never modify RMS Relay
- Never modify RMS Trimode
- Only monitor status, logs, and health

## Current v1.0 Features
- Windows Service
- Local web dashboard on port 8080
- Mobile-friendly browser access
- RMS Relay running/stopped detection
- RMS Trimode running/stopped detection
- Scanner running/stopped detection
- Pushover notifications
- Installer built with Inno Setup
- Default config ships with blank Pushover keys
- Dashboard available at http://127.0.0.1:8080
- LAN access available at http://GATEWAY_IP:8080 if firewall allows TCP 8080

## Architecture
Projects:
- GatewayPulse.Core
- GatewayPulse.Service
- GatewayPulse.Web

Main service:
- ASP.NET Core / .NET 8
- Windows Service
- Kestrel web server
- Uses appsettings.json

Main config file:
- GatewayPulse.Service/appsettings.json

Default install path:
- C:\Program Files\Gateway Pulse\Service

## Important Config Rules
Alerts are stored at top level:

```json
"Alerts": {
  "RelayOffline": true,
  "TrimodeOffline": true,
  "ScannerStopped": true,
  "Recovery": true,
  "StationConnected": false
}