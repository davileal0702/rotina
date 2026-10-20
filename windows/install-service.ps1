#Requires -RunAsAdministrator
# Publishes Rotina.Service to Program Files and registers it as an auto-start Windows service with automatic restart.
param([switch]$Uninstall)
$ErrorActionPreference = "Stop"
$name = "Rotina"
$out = Join-Path $env:ProgramFiles "Rotina\Service"

$svc = Get-Service $name -ErrorAction SilentlyContinue
if ($svc) { Stop-Service $name -Force }

if ($Uninstall) {
    if ($svc) { sc.exe delete $name | Out-Null }
    Write-Host "Serviço removido. Os dados continuam em $env:ProgramData\Rotina."
    return
}

dotnet publish (Join-Path $PSScriptRoot "Rotina.Service") -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish." }

if (-not $svc) {
    New-Service -Name $name -DisplayName "Rotina" -StartupType Automatic `
        -BinaryPathName "`"$out\Rotina.Service.exe`"" `
        -Description "Mantém a rotina: agenda de atividades e bloqueio de apps e sites." | Out-Null
}
# Restart 5 s after any crash; failure counter resets daily.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service $name
Get-Service $name
