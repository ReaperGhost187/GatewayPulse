param(
    [string]$ServiceName = "GatewayPulse",
    [string]$Port = "8080"
)

$ErrorActionPreference = "SilentlyContinue"
Stop-Service $ServiceName
sc.exe delete $ServiceName | Out-Null
Get-NetFirewallRule -DisplayName "Gateway Pulse Dashboard $Port" | Remove-NetFirewallRule
Write-Host "Gateway Pulse service removed."
