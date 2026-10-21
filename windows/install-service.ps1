#Requires -RunAsAdministrator
# Publishes Rotina.Service and Rotina.App to Program Files, registers the service (auto-start, automatic restart)
# and starts the app in the tray at every login. The service finds the app at ..\App next to itself.
param([switch]$Uninstall)
$ErrorActionPreference = "Stop"
$name = "Rotina"
$out = Join-Path $env:ProgramFiles "Rotina\Service"
$appOut = Join-Path $env:ProgramFiles "Rotina\App"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

$svc = Get-Service $name -ErrorAction SilentlyContinue
if ($svc) { Stop-Service $name -Force }
Get-Process Rotina.App -ErrorAction SilentlyContinue | Stop-Process -Force # the exe can't be replaced while open

if ($Uninstall) {
    if ($svc) { sc.exe delete $name | Out-Null }
    Remove-ItemProperty $runKey -Name $name -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName "Rotina (celular)" -ErrorAction SilentlyContinue
    Write-Host "Serviço removido. Os dados continuam em $env:ProgramData\Rotina."
    return
}

dotnet publish (Join-Path $PSScriptRoot "Rotina.Service") -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish do serviço." }
dotnet publish (Join-Path $PSScriptRoot "Rotina.App") -c Release -o $appOut
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish do app." }
Set-ItemProperty $runKey -Name $name -Value "`"$appOut\Rotina.App.exe`" --background"
# The phone syncs over the home network: open the port only on private networks, never on public Wi-Fi.
Remove-NetFirewallRule -DisplayName "Rotina (celular)" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "Rotina (celular)" -Direction Inbound -Protocol TCP -LocalPort 47800 `
    -Profile Private -Action Allow -Program "$out\Rotina.Service.exe" | Out-Null

if (-not $svc) {
    New-Service -Name $name -DisplayName "Rotina" -StartupType Automatic `
        -BinaryPathName "`"$out\Rotina.Service.exe`"" `
        -Description "Mantém a rotina: agenda de atividades e bloqueio de apps e sites." | Out-Null
}
# Restart 5 s after any crash; failure counter resets daily.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
# Data only for the system: a standard account must not edit the database to end an activity early.
# SIDs instead of names, because group names change with the Windows language.
$data = Join-Path $env:ProgramData "Rotina"
New-Item -ItemType Directory -Force $data | Out-Null
icacls $data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
Start-Service $name
Get-Service $name
