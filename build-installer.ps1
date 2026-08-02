param(
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

Write-Host "Running release tests..."
dotnet test .\GatewayPulse.VictronMonitor.Tests\GatewayPulse.VictronMonitor.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "Node.js is required to run the dashboard Power System tests."
}
node .\GatewayPulse.VictronMonitor.Tests\DashboardPowerSystemTests.js
if ($LASTEXITCODE -ne 0) {
    throw "Dashboard Power System tests failed with exit code $LASTEXITCODE."
}
node .\GatewayPulse.VictronMonitor.Tests\DashboardPreferencesTests.js
if ($LASTEXITCODE -ne 0) {
    throw "Dashboard Preferences tests failed with exit code $LASTEXITCODE."
}
node .\GatewayPulse.VictronMonitor.Tests\NetworkMapTests.js
if ($LASTEXITCODE -ne 0) {
    throw "Network Map tests failed with exit code $LASTEXITCODE."
}

Write-Host "Running multi-device configuration and ACL tests..."
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
    .\GatewayPulse.VictronMonitor.Tests\verify-configure-victron.ps1
if ($LASTEXITCODE -ne 0) {
    throw "Configuration tests failed with exit code $LASTEXITCODE."
}

Remove-Item .\Publish\Service -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\Publish\Tray -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\Publish\VictronMonitor -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Publishing Gateway Pulse service..."
dotnet publish .\GatewayPulse.Service\GatewayPulse.Service.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o .\Publish\Service
if ($LASTEXITCODE -ne 0) {
    throw "Gateway Pulse service publish failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing tray application..."
dotnet publish .\GatewayPulse.Tray\GatewayPulse.Tray.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o .\Publish\Tray
if ($LASTEXITCODE -ne 0) {
    throw "Gateway Pulse tray publish failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing Victron collector..."
dotnet publish .\GatewayPulse.VictronMonitor\GatewayPulse.VictronMonitor.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o .\Publish\VictronMonitor
if ($LASTEXITCODE -ne 0) {
    throw "Victron collector publish failed with exit code $LASTEXITCODE."
}

Remove-Item .\Publish\Lp100Monitor -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Publishing LP-100A collector..."
dotnet publish .\GatewayPulse.Lp100Monitor\GatewayPulse.Lp100Monitor.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o .\Publish\Lp100Monitor
if ($LASTEXITCODE -ne 0) {
    throw "LP-100A collector publish failed with exit code $LASTEXITCODE."
}

if (!$InnoCompiler) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if (!$InnoCompiler -or !(Test-Path $InnoCompiler)) {
    throw "Inno Setup 6 compiler was not found. Install it or pass -InnoCompiler C:\path\to\ISCC.exe."
}

# Keep local developer settings out of the packaged defaults.
$PublishedSettings = Join-Path $Root 'Publish\Service\appsettings.json'
if (Test-Path $PublishedSettings) {
    $settingsJson = Get-Content $PublishedSettings -Raw | ConvertFrom-Json
    if ($null -ne $settingsJson.NetworkMap) {
        $settingsJson.NetworkMap.ServiceCode = ""
        $settingsJson.NetworkMap.AutoRefreshMinutes = 15
        $settingsJson.NetworkMap.MapUrl = "https://cms.winlink.org:444/maps/WinlinkGateways.aspx"
    }
    $settingsJson | ConvertTo-Json -Depth 32 | Set-Content -Path $PublishedSettings -Encoding UTF8
    Write-Host "Sanitized packaged NetworkMap defaults in Publish\Service\appsettings.json"
}

Write-Host "Compiling installer..."
& $InnoCompiler .\GatewayPulse.iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$Installer = Get-Item .\Installer_Output\GatewayPulseSetup_v1.2.5.exe
$Hash = Get-FileHash $Installer.FullName -Algorithm SHA256
$ChecksumPath = Join-Path $Installer.DirectoryName 'GatewayPulseSetup_v1.2.5.sha256.txt'
$ChecksumLine = $Hash.Hash.ToLowerInvariant() + '  ' + $Installer.Name + "`n"
[System.IO.File]::WriteAllText($ChecksumPath, $ChecksumLine, [System.Text.Encoding]::ASCII)
$VerifiedHash = (Get-FileHash $Installer.FullName -Algorithm SHA256).Hash
$WrittenHash = ([System.IO.File]::ReadAllText($ChecksumPath).Trim() -split '\s+')[0]
if ($VerifiedHash.ToLowerInvariant() -ne $WrittenHash) {
    throw 'Installer checksum verification failed after writing the checksum file.'
}
Write-Host ""
Write-Host "Installer ready: $($Installer.FullName)"
Write-Host "Size: $($Installer.Length) bytes"
Write-Host "SHA-256: $($Hash.Hash.ToLowerInvariant())"
Write-Host "Checksum file: $ChecksumPath"
