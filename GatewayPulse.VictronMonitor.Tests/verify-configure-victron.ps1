$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ConfigureScript = Join-Path $Root 'GatewayPulse.Service\configure-victron.ps1'
$TestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('gateway-pulse-config-test-' + [guid]::NewGuid().ToString('N'))
$ConfigPath = Join-Path $TestRoot 'appsettings.json'
$BatteryKey = Join-Path $TestRoot 'victron.key'
$ShuntKey = Join-Path $TestRoot 'smartshunt.key'

New-Item -ItemType Directory -Path $TestRoot | Out-Null
try {
    Set-Content -LiteralPath $BatteryKey -Value ('01' * 16) -Encoding ASCII -NoNewline
    Set-Content -LiteralPath $ShuntKey -Value ('02' * 16) -Encoding ASCII -NoNewline
    @'
{
  "Unrelated": { "PreserveMe": "yes" },
  "PowerMonitoring": { "CustomSetting": 42 },
  "VictronMonitor": {
    "Enabled": false,
    "Thresholds": { "StateOfChargeWarningPercent": 27 },
    "Devices": []
  }
}
'@ | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

    $relativePathRejected = $false
    try {
        & $ConfigureScript -ConfigPath $ConfigPath `
            -BatteryProtectAddress 'D5:11:30:C1:55:16' -BatteryProtectKeyFile 'victron.key'
    }
    catch {
        $relativePathRejected = $_.Exception.Message -match 'must be absolute'
    }
    if (-not $relativePathRejected) { throw 'A relative key-file path was not rejected.' }

    & $ConfigureScript -ConfigPath $ConfigPath `
        -BatteryProtectAddress 'D5:11:30:C1:55:16' -BatteryProtectKeyFile $BatteryKey `
        -ConfigureSmartShunt -SmartShuntAddress 'AA:BB:CC:DD:EE:FF' -SmartShuntKeyFile $ShuntKey
    if ($LASTEXITCODE -notin @(0, $null)) { throw "Initial configure command failed: $LASTEXITCODE" }

    $configured = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($configured.Unrelated.PreserveMe -ne 'yes') { throw 'Unrelated settings were not preserved.' }
    if ($configured.PowerMonitoring.CustomSetting -ne 42) { throw 'Unrelated PowerMonitoring setting was not preserved.' }
    if ($configured.VictronMonitor.Thresholds.StateOfChargeWarningPercent -ne 27) { throw 'Custom threshold was not preserved.' }
    if (@($configured.VictronMonitor.Devices).Count -ne 2) { throw 'Both Victron devices were not configured.' }
    if (@($configured.VictronMonitor.Devices | Where-Object Type -eq 'SmartShunt').Count -ne 1) { throw 'SmartShunt entry is missing.' }
    if ((Get-Content -LiteralPath $ConfigPath -Raw) -match ('01' * 16) -or
        (Get-Content -LiteralPath $ConfigPath -Raw) -match ('02' * 16)) {
        throw 'An encryption key was written to appsettings.json.'
    }

    # The production installer runs elevated. Restore this non-elevated test user's
    # access before simulating a second installer pass, then verify the script
    # reapplies the protected ACL successfully.
    foreach ($path in @($BatteryKey, $ShuntKey)) {
        & icacls.exe $path /reset /Q | Out-Null
        & icacls.exe $path /grant:r "${env:USERNAME}:(F)" /Q | Out-Null
    }

    & $ConfigureScript -ConfigPath $ConfigPath `
        -BatteryProtectAddress '11:22:33:44:55:66' -BatteryProtectKeyFile $ShuntKey `
        -PreserveExistingBatteryProtect
    if ($LASTEXITCODE -notin @(0, $null)) { throw "Upgrade-preservation command failed: $LASTEXITCODE" }
    $upgraded = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    $preservedShunt = @($upgraded.VictronMonitor.Devices | Where-Object Type -eq 'SmartShunt')[0]
    if (-not $preservedShunt.Enabled -or $preservedShunt.Address -ne 'AA:BB:CC:DD:EE:FF') {
        throw 'Existing SmartShunt configuration was not preserved during upgrade.'
    }

    $preservedBattery = @($upgraded.VictronMonitor.Devices | Where-Object Type -eq 'BatteryProtect')[0]
    if ($preservedBattery.Address -ne 'D5:11:30:C1:55:16' -or $preservedBattery.KeyFile -ne $BatteryKey) {
        throw 'Existing BatteryProtect configuration was not preserved during upgrade.'
    }

    & icacls.exe $BatteryKey /reset /Q | Out-Null
    & icacls.exe $BatteryKey /grant:r "${env:USERNAME}:(F)" /Q | Out-Null
    $legacyConfig = Join-Path $TestRoot 'legacy-appsettings.json'
    @"
{
  "Unrelated": { "PreserveMe": "legacy" },
  "VictronMonitor": {
    "Enabled": true,
    "Address": "10:20:30:40:50:60",
    "KeyFile": "$($BatteryKey.Replace('\', '\\'))"
  }
}
"@ | Set-Content -LiteralPath $legacyConfig -Encoding UTF8
    & $ConfigureScript -ConfigPath $legacyConfig `
        -BatteryProtectAddress 'D5:11:30:C1:55:16' -BatteryProtectKeyFile $ShuntKey `
        -PreserveExistingBatteryProtect
    $migrated = Get-Content -LiteralPath $legacyConfig -Raw | ConvertFrom-Json
    $migratedBattery = @($migrated.VictronMonitor.Devices | Where-Object Type -eq 'BatteryProtect')[0]
    if ($migratedBattery.Address -ne '10:20:30:40:50:60' -or $migratedBattery.KeyFile -ne $BatteryKey) {
        throw 'Legacy BatteryProtect Address/KeyFile configuration was not preserved and migrated.'
    }
    if ($migrated.Unrelated.PreserveMe -ne 'legacy') { throw 'Legacy unrelated setting was not preserved.' }

    & icacls.exe $BatteryKey /reset /Q | Out-Null
    & icacls.exe $BatteryKey /grant:r "${env:USERNAME}:(F)" /Q | Out-Null
    $freshConfig = Join-Path $TestRoot 'fresh-appsettings.json'
    @"
{
  "VictronMonitor": {
    "Enabled": false,
    "Devices": [
      {
        "Type": "BatteryProtect",
        "Address": "D5:11:30:C1:55:16",
        "KeyFile": "$($BatteryKey.Replace('\', '\\'))",
        "Enabled": true
      }
    ]
  }
}
"@ | Set-Content -LiteralPath $freshConfig -Encoding UTF8
    & $ConfigureScript -ConfigPath $freshConfig `
        -BatteryProtectAddress '21:22:23:24:25:26' -BatteryProtectKeyFile $BatteryKey
    $fresh = Get-Content -LiteralPath $freshConfig -Raw | ConvertFrom-Json
    $freshBattery = @($fresh.VictronMonitor.Devices | Where-Object Type -eq 'BatteryProtect')[0]
    if ($freshBattery.Address -ne '21:22:23:24:25:26') {
        throw 'Fresh-install BatteryProtect wizard configuration was not applied.'
    }

    Write-Output 'configure-victron tests passed: fresh install, merge, two devices, key non-disclosure, and current/legacy upgrade preservation.'
}
finally {
    foreach ($path in @($BatteryKey, $ShuntKey)) {
        if (Test-Path -LiteralPath $path) {
            & icacls.exe $path /reset /Q 2>$null | Out-Null
            & icacls.exe $path /grant:r "${env:USERNAME}:(F)" /Q 2>$null | Out-Null
        }
    }
    Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
}
