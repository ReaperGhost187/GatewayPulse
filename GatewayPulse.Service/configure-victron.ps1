param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [string]$Address = '',
    [string]$KeyFile = 'C:\PWM\victron.key',
    [string]$BatteryProtectAddress = '',
    [string]$BatteryProtectKeyFile = '',
    [switch]$PreserveExistingBatteryProtect,
    [switch]$ConfigureSmartShunt,
    [string]$SmartShuntAddress = '',
    [string]$SmartShuntKeyFile = 'C:\PWM\smartshunt.key',
    [string]$OutputPath = 'C:\PWM\PowerTelemetry.json',
    [string]$LogsPath = 'C:\PWM\logs',
    [string]$ServiceName = 'GatewayPulse',
    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$Enabled = !$Disable

if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Gateway Pulse configuration was not found: $ConfigPath"
}

$configuration = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$configurationAcl = Get-Acl -LiteralPath $ConfigPath
if ($null -eq $configuration.VictronMonitor) {
    $configuration | Add-Member -NotePropertyName VictronMonitor -NotePropertyValue ([pscustomobject]@{}) -Force
}
$victron = $configuration.VictronMonitor
$existingDevices = @($victron.Devices)
$existingBatteryProtect = @($existingDevices | Where-Object { $_.Type -eq 'BatteryProtect' } | Select-Object -First 1)
$existingSmartShunt = @($existingDevices | Where-Object { $_.Type -eq 'SmartShunt' } | Select-Object -First 1)
if ($existingBatteryProtect.Count -gt 0) { $existingBatteryProtect = $existingBatteryProtect[0] } else { $existingBatteryProtect = $null }
if ($existingSmartShunt.Count -gt 0) { $existingSmartShunt = $existingSmartShunt[0] } else { $existingSmartShunt = $null }

if ($null -eq $existingBatteryProtect -and
    ![string]::IsNullOrWhiteSpace([string]$victron.Address) -and
    ![string]::IsNullOrWhiteSpace([string]$victron.KeyFile)) {
    $existingBatteryProtect = [pscustomobject]@{
        Type = 'BatteryProtect'
        Address = [string]$victron.Address
        KeyFile = [string]$victron.KeyFile
        Enabled = $true
    }
}

if ($PreserveExistingBatteryProtect -and $null -ne $existingBatteryProtect) {
    $BatteryProtectAddress = [string]$existingBatteryProtect.Address
    $BatteryProtectKeyFile = [string]$existingBatteryProtect.KeyFile
}

if ([string]::IsNullOrWhiteSpace($BatteryProtectAddress)) {
    if (![string]::IsNullOrWhiteSpace($Address)) {
        $BatteryProtectAddress = $Address
    }
    elseif ($null -ne $existingBatteryProtect) {
        $BatteryProtectAddress = [string]$existingBatteryProtect.Address
    }
}
if ([string]::IsNullOrWhiteSpace($BatteryProtectKeyFile)) {
    if (![string]::IsNullOrWhiteSpace($KeyFile)) {
        $BatteryProtectKeyFile = $KeyFile
    }
    elseif ($null -ne $existingBatteryProtect) {
        $BatteryProtectKeyFile = [string]$existingBatteryProtect.KeyFile
    }
}

function Test-BluetoothAddress([string]$Value) {
    return $Value -match '^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$'
}

function New-DeviceConfiguration(
    [string]$Type,
    [string]$DeviceAddress,
    [string]$DeviceKeyFile,
    [bool]$DeviceEnabled) {
    return [pscustomobject]@{
        Type = $Type
        Address = $DeviceAddress
        KeyFile = $DeviceKeyFile
        Enabled = $DeviceEnabled
    }
}

if ($Enabled -and !(Test-BluetoothAddress $BatteryProtectAddress)) {
    throw 'BatteryProtect Bluetooth address must use AA:BB:CC:DD:EE:FF format.'
}

if ($Enabled) {
    $batteryProtect = New-DeviceConfiguration `
        'BatteryProtect' `
        $BatteryProtectAddress.ToUpperInvariant() `
        $BatteryProtectKeyFile `
        $true
}
elseif ($null -ne $existingBatteryProtect) {
    $batteryProtect = $existingBatteryProtect
}
else {
    $batteryProtect = New-DeviceConfiguration 'BatteryProtect' '' 'C:\PWM\victron.key' $true
}

if ($ConfigureSmartShunt) {
    if (!(Test-BluetoothAddress $SmartShuntAddress)) {
        throw 'SmartShunt Bluetooth address must use AA:BB:CC:DD:EE:FF format.'
    }
    if ([string]::IsNullOrWhiteSpace($SmartShuntKeyFile)) {
        throw 'SmartShunt key-file path is required when SmartShunt monitoring is enabled.'
    }
    $smartShunt = New-DeviceConfiguration `
        'SmartShunt' `
        $SmartShuntAddress.ToUpperInvariant() `
        $SmartShuntKeyFile `
        $true
}
elseif ($null -ne $existingSmartShunt) {
    $smartShunt = $existingSmartShunt
}
else {
    $smartShunt = New-DeviceConfiguration 'SmartShunt' '' 'C:\PWM\smartshunt.key' $false
}

$system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
$collectorAccountSid = $system
$administrators = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if ($null -ne $service -and $service.StartName -and $service.StartName -ne 'LocalSystem') {
    $serviceAccount = New-Object System.Security.Principal.NTAccount($service.StartName)
    $collectorAccountSid = $serviceAccount.Translate([System.Security.Principal.SecurityIdentifier])
}

function Protect-VictronKeyFile([string]$Path, [string]$DeviceType) {
    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$DeviceType key file path must be absolute."
    }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$DeviceType key file was not found. Create it before enabling that device."
    }
    $keyText = (Get-Content -LiteralPath $Path -Raw).Trim()
    if ($keyText -notmatch '^[0-9A-Fa-f]{32}$') {
        $keyText = $null
        throw "$DeviceType key file must contain exactly 32 hexadecimal characters."
    }
    $keyText = $null

    $keyAcl = Get-Acl -LiteralPath $Path
    $keyAcl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($keyAcl.Access)) {
        [void]$keyAcl.RemoveAccessRuleSpecific($rule)
    }
    $readRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $system,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $administrators,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow)
    [void]$keyAcl.AddAccessRule($readRule)
    [void]$keyAcl.AddAccessRule($adminRule)
    if ($collectorAccountSid -ne $system) {
        $serviceRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $collectorAccountSid,
            [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$keyAcl.AddAccessRule($serviceRule)
    }
    Set-Acl -LiteralPath $Path -AclObject $keyAcl
}

$devices = @($batteryProtect, $smartShunt)
if ($Enabled) {
    foreach ($device in $devices | Where-Object { $_.Enabled }) {
        Protect-VictronKeyFile ([string]$device.KeyFile) ([string]$device.Type)
    }
}

if ($null -eq $configuration.PowerMonitoring) {
    $configuration | Add-Member -NotePropertyName PowerMonitoring -NotePropertyValue ([pscustomobject]@{}) -Force
}
$configuration.PowerMonitoring | Add-Member -NotePropertyName TelemetryPath -NotePropertyValue $OutputPath -Force
$configuration.PowerMonitoring | Add-Member -NotePropertyName StaleAfterSeconds -NotePropertyValue 30 -Force

$thresholdDefaults = [ordered]@{
    StaleAfterSeconds = 30
    WeakSignalRssi = -85
    StateOfChargeWarningPercent = 30
    StateOfChargeCriticalPercent = 15
    IdleCurrentAmps = 0.2
    LowVoltageWarning = 11.8
    LowVoltageCritical = 11.0
    HighVoltageWarning = 15.0
}
if ($null -eq $victron.Thresholds) {
    $victron | Add-Member -NotePropertyName Thresholds -NotePropertyValue ([pscustomobject]$thresholdDefaults) -Force
}
else {
    foreach ($entry in $thresholdDefaults.GetEnumerator()) {
        if ($null -eq $victron.Thresholds.($entry.Key)) {
            $victron.Thresholds | Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force
        }
    }
}

$victron | Add-Member -NotePropertyName Enabled -NotePropertyValue $Enabled -Force
$victron | Add-Member -NotePropertyName ExecutablePath -NotePropertyValue 'VictronMonitor\GatewayPulse.VictronMonitor.exe' -Force
$victron | Add-Member -NotePropertyName ConfigurationPath -NotePropertyValue 'appsettings.json' -Force
$victron | Add-Member -NotePropertyName Address -NotePropertyValue ([string]$batteryProtect.Address) -Force
$victron | Add-Member -NotePropertyName KeyFile -NotePropertyValue ([string]$batteryProtect.KeyFile) -Force
$victron | Add-Member -NotePropertyName OutputPath -NotePropertyValue $OutputPath -Force
$victron | Add-Member -NotePropertyName LogsPath -NotePropertyValue $LogsPath -Force
$victron | Add-Member -NotePropertyName IntervalSeconds -NotePropertyValue 5 -Force
$victron | Add-Member -NotePropertyName RestartDelaySeconds -NotePropertyValue 10 -Force
$victron | Add-Member -NotePropertyName Devices -NotePropertyValue $devices -Force

if ($Enabled) {
    $dataDirectory = Split-Path -Parent $OutputPath
    if ($dataDirectory) {
        New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $LogsPath -Force | Out-Null

    foreach ($directory in @($dataDirectory, $LogsPath) | Where-Object { $_ } | Select-Object -Unique) {
        $directoryAcl = Get-Acl -LiteralPath $directory
        $modifyRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $collectorAccountSid,
            [System.Security.AccessControl.FileSystemRights]::Modify,
            ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $directoryAcl.SetAccessRule($modifyRule)
        Set-Acl -LiteralPath $directory -AclObject $directoryAcl
    }
}

$temporaryPath = "$ConfigPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Set-Acl -LiteralPath $temporaryPath -AclObject $configurationAcl
    Move-Item -LiteralPath $temporaryPath -Destination $ConfigPath -Force
    Set-Acl -LiteralPath $ConfigPath -AclObject $configurationAcl
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}

$enabledDeviceCount = @($devices | Where-Object { $_.Enabled }).Count
Write-Host "Gateway Pulse Victron integration configured for $enabledDeviceCount power device(s)."
Write-Host "Telemetry: $OutputPath"
Write-Host "Logs: $LogsPath"
