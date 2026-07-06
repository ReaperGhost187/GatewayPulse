$ErrorActionPreference = "Stop"

dotnet publish .\GatewayPulse.Service.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o .\publish

Copy-Item .\appsettings.json .\publish\appsettings.json -Force
Copy-Item .\wwwroot .\publish\wwwroot -Recurse -Force
Copy-Item .\install-service.ps1 .\publish\install-service.ps1 -Force
Copy-Item .\uninstall-service.ps1 .\publish\uninstall-service.ps1 -Force

Write-Host ""
Write-Host "Publish complete:"
Write-Host (Resolve-Path .\publish)
Write-Host ""
Write-Host "Next:"
Write-Host "1. Edit publish\appsettings.json"
Write-Host "2. Run PowerShell as Administrator"
Write-Host "3. cd into the publish folder"
Write-Host "4. .\install-service.ps1"
