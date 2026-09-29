<#
.SYNOPSIS
    Remove o servico LayoutParserLowCodeRunner. Idempotente (nao falha se ja removido).
.PARAMETER InstallDir  Se informado com -RemoveFiles, apaga os arquivos do servico (nunca a Bin do Sysmiddle inteira: so os 3 arquivos do runner).
.PARAMETER PurgeData   Apaga tambem logs e pasta de trabalho em %ProgramData%.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$RemoveFiles,
    [switch]$PurgeData,
    [string]$DataDir = (Join-Path $env:ProgramData 'LayoutParserLowCodeRunner')
)

$ErrorActionPreference = 'Stop'
$name = 'LayoutParserLowCodeRunner'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Error 'Execute como Administrador.'; exit 1
}

$svc = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $name -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
    & sc.exe delete $name | Out-Null
    Write-Host "Servico '$name' removido."
} else {
    Write-Host "Servico '$name' nao existe (ok)."
}

Get-NetFirewallRule -DisplayName "$name*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ($RemoveFiles -and $InstallDir) {
    foreach ($f in 'LayoutParserLowCodeRunner.exe', 'LayoutParserLowCodeRunner.exe.config', 'LayoutParserLowCodeRunner.pdb', 'logger.xml') {
        Remove-Item (Join-Path $InstallDir $f) -Force -ErrorAction SilentlyContinue
    }
}
if ($PurgeData -and (Test-Path $DataDir)) { Remove-Item $DataDir -Recurse -Force }
