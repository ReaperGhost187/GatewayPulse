# Gateway Pulse Build

## Complete production installer

Run from the repository root in PowerShell:

```powershell
.\build-installer.ps1
```

The script:

1. runs the Release .NET suite;
2. runs Power System JavaScript state tests;
3. runs multi-device configuration, ACL, secret-nondisclosure, and upgrade-migration tests;
4. publishes the self-contained service, tray, Victron collector, and LP-100A collector;
5. compiles `GatewayPulse.iss` with Inno Setup 6;
6. writes and independently verifies the SHA-256 checksum file; and
7. prints the installer path, size, and checksum.

If Inno Setup is installed in a nonstandard directory:

```powershell
.\build-installer.ps1 -InnoCompiler "C:\Tools\Inno Setup 6\ISCC.exe"
```

Output:

```text
Installer_Output\GatewayPulseSetup_v1.2.8.exe
Installer_Output\GatewayPulseSetup_v1.2.8.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.9.exe
Installer_Output\GatewayPulseSetup_v1.2.9.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.10.exe
Installer_Output\GatewayPulseSetup_v1.2.10.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.11.exe
Installer_Output\GatewayPulseSetup_v1.2.11.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.12.exe
Installer_Output\GatewayPulseSetup_v1.2.12.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.13.exe
Installer_Output\GatewayPulseSetup_v1.2.13.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.14.exe
Installer_Output\GatewayPulseSetup_v1.2.14.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.15.exe
Installer_Output\GatewayPulseSetup_v1.2.15.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.16.exe
Installer_Output\GatewayPulseSetup_v1.2.16.sha256.txt
Installer_Output\GatewayPulseSetup_v1.2.19.exe
Installer_Output\GatewayPulseSetup_v1.2.19.sha256.txt
```

The installer preserves existing appsettings content/ACLs, dashboard preferences, and current or legacy BatteryProtect configuration. SmartShunt configuration is optional and preserved on upgrades. It never copies or records a key value. It creates `C:\PWM` and `C:\PWM\logs`, protects enabled-device key files, installs one supervised collector, configures service recovery, and starts the Windows service.

Generated folders and release artifacts are ignored by git:

```text
Publish/
Installer_Output/
bin/
obj/
*.exe
*.zip
```
