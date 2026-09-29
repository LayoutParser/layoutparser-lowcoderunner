<#
.SYNOPSIS
    Instala (ou atualiza) o LayoutParserLowCodeRunner como servico Windows. Idempotente.

.DESCRIPTION
    - Copia o pacote (exe, .config, logger.xml, scripts) para -InstallDir.
    - Cria/atualiza o servico, com conta SEM admin e recovery (reinicia em falha).
    - Cria pastas de log e de trabalho com ACL restrita (servico, SYSTEM, Administrators).
    - Regra de firewall permitindo APENAS o(s) IP(s) do host da API (-AllowedRemoteIp).
    - Grava a config do servico (env LowCodeRunner__*) no registro do servico: nada de segredo no repositorio.

    NAO usa HttpListener: o servidor e um TcpListener, entao NAO ha urlacl para reservar.

    A licenca Sysmiddle e do HOST. Depois de instalar, valide: curl http://<host>:5230/v1/health?deep=true
    (sobe o SDK: confirma DLLs, licenca, package e globalFolder de verdade).

.PARAMETER PackageDir        Pasta com o build (bin\Release\net481). Default: pasta deste script\..
.PARAMETER InstallDir        Destino. Para o worker real, use a Bin da instancia Sysmiddle (o exe resolve as DLLs pelo app base).
.PARAMETER ServiceAccount    Conta do servico (ex.: .\svc-lowcode ou 'NT SERVICE\LayoutParserLowCodeRunner'). Sem admin.
.PARAMETER ServicePassword   SecureString da conta (omitir para contas virtuais/gMSA).
.PARAMETER ListenPrefix      Ex.: http://0.0.0.0:5230/ (para a rede) ou http://localhost:5230/.
.PARAMETER AllowedRemoteIp   IP(s) do host da API (firewall allowlist). Obrigatorio se ListenPrefix nao for localhost.
#>
[CmdletBinding()]
param(
    [string]$PackageDir = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$ServiceAccount,
    [securestring]$ServicePassword,
    [Parameter(Mandatory)][string]$SysmiddleDir,
    [Parameter(Mandatory)][string]$GlobalFolder,
    [Parameter(Mandatory)][string]$Package,
    [string]$ListenPrefix = 'http://localhost:5230/',
    [string[]]$AllowedRemoteIp = @(),
    [string]$DataDir = (Join-Path $env:ProgramData 'LayoutParserLowCodeRunner'),
    [int]$MaxConcurrentRunners = 2,
    [int]$RunnerTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$name = 'LayoutParserLowCodeRunner'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    Write-Error 'Execute como Administrador.'; exit 1
}
if ($ServiceAccount -match '(^|\\)(Administrator|LocalSystem|SYSTEM)$' ) {
    Write-Error "Use uma conta SEM privilegio de admin (recebido: $ServiceAccount)."; exit 1
}
$uri = [uri]$ListenPrefix
$isLocal = $uri.Host -in @('localhost', '127.0.0.1')
if (-not $isLocal -and $AllowedRemoteIp.Count -eq 0) {
    Write-Error 'ListenPrefix exposto na rede exige -AllowedRemoteIp (IP do host da API). Autenticacao e por rede isolada.'; exit 1
}
if (-not (Test-Path $GlobalFolder)) { Write-Error "GlobalFolder nao existe: $GlobalFolder"; exit 1 }
if (-not (Test-Path $SysmiddleDir)) { Write-Error "SysmiddleDir nao existe: $SysmiddleDir"; exit 1 }

# 1) Arquivos (nunca copia DLLs proprietarias: o InstallDir ja deve ser a Bin Sysmiddle, ou receber o worker por WorkerExePath)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Parando servico existente...'
    Stop-Service $name -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}
foreach ($f in 'LayoutParserLowCodeRunner.exe', 'LayoutParserLowCodeRunner.exe.config', 'logger.xml') {
    $src = Join-Path $PackageDir $f
    if (Test-Path $src) {
        # Preserva a .config ja ajustada no destino (mesma politica do deploy da API)
        $dst = Join-Path $InstallDir $f
        if ($f -like '*.config' -and (Test-Path $dst)) { continue }
        Copy-Item $src $dst -Force
    }
}

# 2) Pastas com ACL restrita
$logDir = Join-Path $DataDir 'logs'; $workDir = Join-Path $DataDir 'work'
foreach ($d in $DataDir, $logDir, $workDir) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
& icacls $DataDir /inheritance:r /grant:r "${ServiceAccount}:(OI)(CI)M" 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

# 3) Servico
$exe = Join-Path $InstallDir 'LayoutParserLowCodeRunner.exe'
$binPath = "`"$exe`" --service"
if (-not $existing) {
    $cred = if ($ServicePassword) { New-Object pscredential($ServiceAccount, $ServicePassword) } else { $null }
    $svcArgs = @{ Name = $name; BinaryPathName = $binPath; DisplayName = 'LayoutParser LowCode Runner'; StartupType = 'Automatic' }
    if ($cred) { $svcArgs.Credential = $cred }
    New-Service @svcArgs | Out-Null
} else {
    & sc.exe config $name binPath= $binPath start= auto | Out-Null
}
# Conta do servico (sempre reaplicada: idempotente). Conta virtual / gMSA (termina em $) dispensa senha.
if ($ServicePassword) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr); & sc.exe config $name obj= $ServiceAccount password= $plain | Out-Null }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
} else {
    & sc.exe config $name obj= $ServiceAccount | Out-Null
}
& sc.exe description $name 'Executa mapeadores Sysmiddle (low-code) por HTTP para a LayoutParserApi.' | Out-Null
# Recovery: reinicia apos 5s, 15s, 60s; zera o contador em 1 dia
& sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# 4) Config do servico via ambiente do servico (registro) — prioridade sobre o .config
$svcEnv = @(
    "LowCodeRunner__SysmiddleDir=$SysmiddleDir",
    "LowCodeRunner__GlobalFolder=$GlobalFolder",
    "LowCodeRunner__Package=$Package",
    "LowCodeRunner__ListenPrefix=$ListenPrefix",
    "LowCodeRunner__MaxConcurrentRunners=$MaxConcurrentRunners",
    "LowCodeRunner__RunnerTimeoutSeconds=$RunnerTimeoutSeconds",
    "LowCodeRunner__LogDir=$logDir",
    "LowCodeRunner__WorkerTempDir=$workDir"
)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Environment -Type MultiString -Value $svcEnv

# 5) Firewall: so o(s) IP(s) da API
$ruleName = "$name (HTTP $($uri.Port))"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $isLocal) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $uri.Port -RemoteAddress $AllowedRemoteIp -Profile Any | Out-Null
    Write-Host "Firewall: porta $($uri.Port) liberada apenas para: $($AllowedRemoteIp -join ', ')"
}

Start-Service $name
Write-Host "Servico '$name' RUNNING. Valide: curl $($ListenPrefix.TrimEnd('/'))/v1/health?deep=true" -ForegroundColor Green
