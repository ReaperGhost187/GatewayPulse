Victron Instant Readout key locations
=====================================

Place each device's own 32-hex-character Instant Readout key in its separate file:

    Smart BatteryProtect: C:\PWM\victron.key
    SmartShunt 300A:      C:\PWM\smartshunt.key

The SmartShunt address and key are optional until the physical device is installed. Do not reuse the BatteryProtect key; Victron keys are device-specific.

Each file must contain only its 32 hexadecimal characters. Never add key values to appsettings.json, installer command lines, shortcuts, dashboard settings, telemetry, API output, diagnostics, screenshots, or support logs.

The elevated Gateway Pulse installer validates enabled-device key files and restricts each to SYSTEM, Administrators, and any pre-existing custom GatewayPulse service account. It never writes, copies, replaces, or deletes a user's key file.

Verify from an elevated PowerShell window:

    icacls "C:\PWM\victron.key"
    icacls "C:\PWM\smartshunt.key"

Gateway Pulse stores only absolute key-file paths. The collector runs as a supervised child of the GatewayPulse Windows service and clears its managed key byte arrays when disposed.
