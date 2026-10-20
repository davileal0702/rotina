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
    Write-Host "Serviço removido. Os dados continuam em $env:ProgramData\Rotina."
    return
}

dotnet publish (Join-Path $PSScriptRoot "Rotina.Service") -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish do serviço." }
dotnet publish (Join-Path $PSScriptRoot "Rotina.App") -c Release -o $appOut
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish do app." }
Set-ItemProperty $runKey -Name $name -Value "`"$appOut\Rotina.App.exe`" --background"

if (-not $svc) {
    New-Service -Name $name -DisplayName "Rotina" -StartupType Automatic `
        -BinaryPathName "`"$out\Rotina.Service.exe`"" `
        -Description "Mantém a rotina: agenda de atividades e bloqueio de apps e sites." | Out-Null
}
# Restart 5 s after any crash; failure counter resets daily.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service $name
Get-Service $name
