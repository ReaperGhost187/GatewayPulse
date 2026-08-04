#define MyAppName "Gateway Pulse"
#define MyAppVersion "1.2.8"
#define MyAppPublisher "Gateway Pulse"
#define MyServiceName "GatewayPulse"

[Setup]
AppId={{7E4A7B16-8B11-4A40-9F27-5E12065D9A01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Gateway Pulse
DefaultGroupName=Gateway Pulse
DisableProgramGroupPage=yes
OutputDir=Installer_Output
OutputBaseFilename=GatewayPulseSetup_v{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "victron"; Description: "Install and supervise Victron BatteryProtect and SmartShunt power monitoring"; GroupDescription: "Power monitoring:"
Name: "lp100"; Description: "Install LP-100A RF monitoring collector (disabled until configured)"; GroupDescription: "RF monitoring:"; Flags: unchecked

[Dirs]
Name: "C:\PWM"; Check: NeedsPwmDir
Name: "C:\PWM\logs"; Check: NeedsPwmDir

[Files]
Source: "Publish\Service\*"; DestDir: "{app}\Service"; Excludes: "appsettings.json,mockup-*.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Publish\Service\appsettings.json"; DestDir: "{app}\Service"; Flags: onlyifdoesntexist
Source: "Publish\Tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Publish\VictronMonitor\GatewayPulse.VictronMonitor.exe"; DestDir: "{app}\Service\VictronMonitor"; Flags: ignoreversion; Check: IsVictronSelected
Source: "Publish\Lp100Monitor\GatewayPulse.Lp100Monitor.exe"; DestDir: "{app}\Service\Lp100Monitor"; Flags: ignoreversion; Check: IsLp100Selected
Source: "GatewayPulse.Service\configure-victron.ps1"; DestDir: "{app}\Service"; Flags: ignoreversion
Source: "GatewayPulse.Service\configure-lp100.ps1"; DestDir: "{app}\Service"; Flags: ignoreversion
Source: "docs\VICTRON_KEY_README.txt"; DestDir: "C:\PWM"; Flags: onlyifdoesntexist; Check: IsVictronSelected
Source: "docs\LP100_PROTOCOL.md"; DestDir: "{app}"; Flags: ignoreversion; Check: IsLp100Selected
Source: "README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "docs\GATEWAY_DEPLOYMENT.md"; DestDir: "{app}"; DestName: "GATEWAY_DEPLOYMENT.md"; Flags: ignoreversion

[Icons]
Name: "{group}\Open Gateway Pulse Dashboard"; Filename: "http://127.0.0.1:8080"
Name: "{group}\Gateway Pulse Tray"; Filename: "{app}\Tray\GatewayPulse.Tray.exe"
Name: "{group}\README"; Filename: "{app}\README.txt"
Name: "{group}\Gateway deployment guide"; Filename: "{app}\GATEWAY_DEPLOYMENT.md"
Name: "{autodesktop}\Gateway Pulse"; Filename: "{app}\Tray\GatewayPulse.Tray.exe"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/C exit 0"; Flags: runhidden waituntilterminated; BeforeInstall: ConfigureVictronSettings
Filename: "{sys}\sc.exe"; Parameters: "config {#MyServiceName} binPath= ""{app}\Service\GatewayPulse.exe"" start= auto DisplayName= ""Gateway Pulse"""; Flags: runhidden waituntilterminated; StatusMsg: "Updating Gateway Pulse service..."; Check: ServiceInstalled
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\Service\GatewayPulse.exe"" start= auto DisplayName= ""Gateway Pulse"""; Flags: runhidden waituntilterminated; StatusMsg: "Creating Gateway Pulse service..."; Check: ServiceNotInstalledAndExeExists
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/30000/restart/30000/restart/60000"; Flags: runhidden waituntilterminated; StatusMsg: "Configuring service recovery..."; Check: ServiceExeExists
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Gateway Pulse Dashboard 8080"" >nul 2>&1 & netsh advfirewall firewall add rule name=""Gateway Pulse Dashboard 8080"" dir=in action=allow protocol=TCP localport=8080 profile=private"; Flags: runhidden waituntilterminated; StatusMsg: "Configuring the Windows firewall..."; Check: ServiceExeExists
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting Gateway Pulse service..."; Check: ServiceExeExists
Filename: "{app}\Tray\GatewayPulse.Tray.exe"; Description: "Launch Gateway Pulse Tray"; Flags: nowait postinstall skipifsilent; Check: TrayExeExists
Filename: "{app}\README.txt"; Description: "Open README.txt"; Flags: shellexec postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopGatewayPulseService"
Filename: "{cmd}"; Parameters: "/C taskkill /IM GatewayPulse.Tray.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopTray"
Filename: "{cmd}"; Parameters: "/C taskkill /IM GatewayPulse.VictronMonitor.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopVictronMonitor"
Filename: "{cmd}"; Parameters: "/C taskkill /IM GatewayPulse.Lp100Monitor.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopLp100Monitor"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteGatewayPulseService"
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Gateway Pulse Dashboard 8080"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteGatewayPulseFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  BatteryProtectPage: TInputQueryWizardPage;
  SmartShuntOptionPage: TInputOptionWizardPage;
  SmartShuntPage: TInputQueryWizardPage;
  PreserveBatteryProtectConfiguration: Boolean;

procedure InitializeWizard;
begin
  BatteryProtectPage := CreateInputQueryPage(wpSelectTasks,
    'Victron BatteryProtect',
    'Configure the BatteryProtect monitor',
    'Enter the Bluetooth address and the path to the existing 32-hex-character key file. The key value is never stored in the installer.');
  BatteryProtectPage.Add('Bluetooth address:', False);
  BatteryProtectPage.Add('Key file:', False);
  BatteryProtectPage.Values[0] := '';
  BatteryProtectPage.Values[1] := 'C:\PWM\victron.key';

  SmartShuntOptionPage := CreateInputOptionPage(BatteryProtectPage.ID,
    'Victron SmartShunt',
    'Optional SmartShunt configuration',
    'Enable this only when the SmartShunt address and key file are available. Leaving it unchecked preserves an existing SmartShunt configuration during upgrades.',
    False, False);
  SmartShuntOptionPage.Add('Configure or update SmartShunt monitoring now');
  SmartShuntOptionPage.Values[0] := False;

  SmartShuntPage := CreateInputQueryPage(SmartShuntOptionPage.ID,
    'Victron SmartShunt',
    'Configure the SmartShunt 300A monitor',
    'Enter the SmartShunt Bluetooth address and path to its existing Instant Readout key file. No key value is stored by Setup.');
  SmartShuntPage.Add('Bluetooth address:', False);
  SmartShuntPage.Add('Key file:', False);
  SmartShuntPage.Values[0] := '';
  SmartShuntPage.Values[1] := 'C:\PWM\smartshunt.key';
end;

function IsVictronSelected: Boolean;
begin
  Result := WizardIsTaskSelected('victron');
end;

function IsLp100Selected: Boolean;
begin
  Result := WizardIsTaskSelected('lp100');
end;

function NeedsPwmDir: Boolean;
begin
  Result := IsVictronSelected or IsLp100Selected;
end;

function IsVictronNotSelected: Boolean;
begin
  Result := not IsVictronSelected;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = BatteryProtectPage.ID) or (PageID = SmartShuntOptionPage.ID)) and not IsVictronSelected;
  if PageID = SmartShuntPage.ID then
    Result := (not IsVictronSelected) or (not SmartShuntOptionPage.Values[0]);
end;

function IsHexCharacter(C: Char): Boolean;
begin
  Result := ((C >= '0') and (C <= '9')) or
            ((C >= 'A') and (C <= 'F')) or
            ((C >= 'a') and (C <= 'f'));
end;

function IsValidBluetoothAddress(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Value) = 17;
  if not Result then Exit;
  for I := 1 to 17 do
  begin
    if (I = 3) or (I = 6) or (I = 9) or (I = 12) or (I = 15) then
      Result := Result and (Value[I] = ':')
    else
      Result := Result and IsHexCharacter(Value[I]);
  end;
end;

function IsValidVictronKey(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := Length(Value) = 32;
  if not Result then Exit;
  for I := 1 to 32 do
    Result := Result and IsHexCharacter(Value[I]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  KeyText: AnsiString;
begin
  Result := True;
  if (CurPageID = BatteryProtectPage.ID) and IsVictronSelected then
  begin
    if not IsValidBluetoothAddress(Trim(BatteryProtectPage.Values[0])) then
    begin
      MsgBox('Enter the BatteryProtect Bluetooth address as AA:BB:CC:DD:EE:FF.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(BatteryProtectPage.Values[1]) = '' then
    begin
      MsgBox('Enter the path to the BatteryProtect key file.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not FileExists(Trim(BatteryProtectPage.Values[1])) then
    begin
      MsgBox('The BatteryProtect key file does not exist. Create it before continuing.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not LoadStringFromFile(Trim(BatteryProtectPage.Values[1]), KeyText) or
       (not IsValidVictronKey(Trim(KeyText))) then
    begin
      MsgBox('The BatteryProtect key file must contain exactly 32 hexadecimal characters.', mbError, MB_OK);
      Result := False;
    end;
  end;

  if (CurPageID = SmartShuntPage.ID) and IsVictronSelected and SmartShuntOptionPage.Values[0] then
  begin
    if not IsValidBluetoothAddress(Trim(SmartShuntPage.Values[0])) then
    begin
      MsgBox('Enter the SmartShunt Bluetooth address as AA:BB:CC:DD:EE:FF.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not FileExists(Trim(SmartShuntPage.Values[1])) then
    begin
      MsgBox('The SmartShunt key file does not exist. Create C:\PWM\smartshunt.key before continuing.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if not LoadStringFromFile(Trim(SmartShuntPage.Values[1]), KeyText) or
       (not IsValidVictronKey(Trim(KeyText))) then
    begin
      MsgBox('The SmartShunt key file must contain exactly 32 hexadecimal characters.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function GetVictronAddress(Param: String): String;
begin
  Result := Uppercase(Trim(BatteryProtectPage.Values[0]));
end;

function GetVictronKeyFile(Param: String): String;
begin
  Result := Trim(BatteryProtectPage.Values[1]);
end;

function GetSmartShuntAddress(Param: String): String;
begin
  Result := Uppercase(Trim(SmartShuntPage.Values[0]));
end;

function GetSmartShuntKeyFile(Param: String): String;
begin
  Result := Trim(SmartShuntPage.Values[1]);
end;

procedure ConfigureVictronSettings;
var
  Parameters: String;
  ResultCode: Integer;
begin
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\Service\configure-victron.ps1') + '" -ConfigPath "' +
    ExpandConstant('{app}\Service\appsettings.json') + '" -BatteryProtectAddress "' +
    GetVictronAddress('') + '" -BatteryProtectKeyFile "' + GetVictronKeyFile('') + '"';
  if PreserveBatteryProtectConfiguration then
    Parameters := Parameters + ' -PreserveExistingBatteryProtect';
  if IsVictronSelected and SmartShuntOptionPage.Values[0] then
    Parameters := Parameters + ' -ConfigureSmartShunt -SmartShuntAddress "' +
      GetSmartShuntAddress('') + '" -SmartShuntKeyFile "' + GetSmartShuntKeyFile('') + '"';
  if not IsVictronSelected then
    Parameters := Parameters + ' -Disable';

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    RaiseException('Gateway Pulse Victron configuration failed. Setup did not start the service.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  I: Integer;
  ResultCode: Integer;
begin
  PreserveBatteryProtectConfiguration := FileExists(
    ExpandConstant('{app}\Service\appsettings.json'));
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  ResultCode := 0;
  for I := 1 to 30 do
  begin
    Exec(ExpandConstant('{cmd}'),
      '/C tasklist /FI "IMAGENAME eq GatewayPulse.exe" /NH | find /I "GatewayPulse.exe" >nul',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      Break;
    Sleep(1000);
  end;
  if ResultCode = 0 then
  begin
    Result := 'Gateway Pulse did not stop within 30 seconds. Close the service and retry the upgrade.';
    Exit;
  end;
  // Tray holds GatewayPulse.Tray.exe open; must exit before file replace on upgrade.
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM GatewayPulse.Tray.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  if IsVictronSelected then
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM GatewayPulse.VictronMonitor.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM GatewayPulse.Lp100Monitor.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function ServiceExeExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Service\GatewayPulse.exe'));
end;

function ServiceInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceName}', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceNotInstalledAndExeExists: Boolean;
begin
  Result := ServiceExeExists and not ServiceInstalled;
end;

function TrayExeExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Tray\GatewayPulse.Tray.exe'));
end;

procedure ConfigureLp100Settings;
var
  Parameters: String;
  ResultCode: Integer;
begin
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\Service\configure-lp100.ps1') + '" -AppSettingsPath "' +
    ExpandConstant('{app}\Service\appsettings.json') + '" -BaudRate 115200';
  if IsLp100Selected then
    Parameters := Parameters + ' -AutoDetect'
  else
    Parameters := Parameters + ' -Disable';

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    RaiseException('Gateway Pulse LP-100A configuration failed.');
end;
