param(
    [string]$ServiceName = "GatewayPulse",
    [string]$DisplayName = "Gateway Pulse",
    [string]$Description = "Read-only monitoring dashboard for RMS Relay and RMS Trimode gateways.",
    [string]$Port = "8080"
)

$ErrorActionPreference = "Stop"
$ExePath = Join-Path $PSScriptRoot "GatewayPulse.exe"

if (!(Test-Path $ExePath)) {
    Write-Host "GatewayPulse.exe was not found in this folder."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Service -Name $ServiceName -BinaryPathName "`"$ExePath`"" -DisplayName $DisplayName -Description $Description -StartupType Automatic
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/30000/restart/60000 | Out-Null

$ruleName = "Gateway Pulse Dashboard $Port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private | Out-Null
}

Start-Service $ServiceName
Write-Host "Gateway Pulse service installed and started."
Write-Host "Dashboard: http://127.0.0.1:$Port"
