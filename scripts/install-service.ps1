<#
.SYNOPSIS
  Instala o LayoutParserLowCodeRunner como serviço Windows (firewall + variáveis de ambiente do serviço).

.DESCRIPTION
  Idempotente: pode ser reexecutado para reconfigurar/atualizar. Requer PowerShell elevado.

  NOME x ENDEREÇO: o serviço usa TcpListener, então o bind é por ENDEREÇO IP (ou 0.0.0.0), NUNCA por nome.
  O nome (ex.: lowcoderunner.local) é resolvido no CLIENTE: na VM Linux da API use /etc/hosts
      <IP-deste-host-Windows>  lowcoderunner.local  layoutparserdecrypt.local
  (ambos apontam para o mesmo host; decrypt = 5220, runner = 5230). ".local" é mDNS e pode deixar a resolução
  lenta no Ubuntu; por isso o /etc/hosts. NÃO há urlacl (não é HttpListener). NÃO use 8080 (outra API do host)
  nem 5220 (decrypt).

  Autenticação: o serviço NÃO autentica requisições. A proteção é o isolamento de rede: quando o bind não é
  loopback, -AllowedRemoteAddress (IP/CIDR da API) é OBRIGATÓRIO e vira o escopo da regra de firewall (porta+origem).
  Escopos amplos (Any, *, 0.0.0.0, 0.0.0.0/0, ::/0, LocalSubnet) são recusados.

  O worker é o próprio exe e resolve as DLLs Sysmiddle pelo diretório onde está: por padrão o exe é instalado DENTRO
  da Bin do Sysmiddle (-InstallDir = -SysmiddleDir). As DLLs proprietárias nunca são copiadas por este script.

  Ao final valida GET /v1/health (e, salvo -SkipDeepCheck, /v1/health?deep=true, que sobe o SDK e prova licença/
  package). Se falhar, o script TERMINA COM ERRO (exit 1).

.EXAMPLE
  # API Linux 172.25.32.5 alcançando este host (todas as interfaces), porta 5230:
  .\install-service.ps1 -SysmiddleDir 'C:\Program Files\NDDigital\...\Bin' -GlobalFolder 'D:\globalfolder' `
      -Package '<package>' -BindAddress 0.0.0.0 -AllowedRemoteAddress 172.25.32.5

.EXAMPLE
  # Somente local (sem regra de firewall):
  .\install-service.ps1 -SysmiddleDir ... -GlobalFolder ... -Package ...
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'LayoutParserLowCodeRunner',
    # Zip do CI: exe na raiz, scripts em .\scripts. Checkout: saída SDK-style em bin\Release\net481.
    [string]$ExeSource = $(foreach ($c in '..\LayoutParserLowCodeRunner.exe', '..\bin\Release\net481\LayoutParserLowCodeRunner.exe') { $f = Join-Path $PSScriptRoot $c; if (Test-Path $f) { $f; break } }),

    [Parameter(Mandatory)][string]$SysmiddleDir,
    [Parameter(Mandatory)][string]$GlobalFolder,
    [Parameter(Mandatory)][string]$Package,
    # Onde o exe fica. Default = a Bin do Sysmiddle (o worker resolve as DLLs pelo app base).
    [string]$InstallDir,
    [string]$DataDir = 'C:\ProgramData\LayoutParserLowCodeRunner',

    # "localhost", "127.0.0.1", um IP de interface ou 0.0.0.0. NOME de host é recusado (TcpListener liga em endereço).
    [string]$BindAddress = 'localhost',
    [ValidateRange(1, 65535)][int]$Port = 5230,
    # IP ou CIDR permitido no firewall (ex.: 172.25.32.5, a VM da API). Obrigatório se o bind não for loopback.
    [string[]]$AllowedRemoteAddress = @(),

    # Conta do serviço. Default LocalService (mínimo privilégio); ver "Conta de serviço" no README para as ACLs.
    # Para uma conta dedicada informe -ServiceAccount '.\svc-lowcoderunner' -ServicePassword (SecureString).
    [string]$ServiceAccount = 'NT AUTHORITY\LocalService',
    [securestring]$ServicePassword,
    # Concede leitura da conta em SysmiddleDir e GlobalFolder (ACL). Desligue se já configurou as ACLs à mão.
    [switch]$SkipGrantSysmiddleAccess,

    [int]$MaxConcurrentRunners = 2,
    [int]$RunnerTimeoutSeconds = 180,
    [int]$MaxQueue = 8,
    [int]$MaxBodyBytes = 20971520,
    [switch]$SkipDeepCheck,
    # Teto do health deep (o init do SDK leva 12-38 s; o timeout de execução do worker é RunnerTimeoutSeconds).
    [int]$DeepCheckTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute em um PowerShell elevado (Administrador).'
}
if (-not $ExeSource -or -not (Test-Path $ExeSource)) { throw "Executável não encontrado: $ExeSource (rode o build Release antes)." }
foreach ($p in @($SysmiddleDir, $GlobalFolder)) { if (-not (Test-Path $p)) { throw "Pasta não existe: $p" } }
if (-not (Test-Path (Join-Path $GlobalFolder 'global.config'))) { throw "global.config não encontrado em $GlobalFolder" }
if (-not $InstallDir) { $InstallDir = $SysmiddleDir }
if ($Port -in 8080, 5220) { throw "Porta $Port reservada (8080 = outra API do host; 5220 = decrypt). O runner usa 5230." }

# --- Bind: por ENDEREÇO ------------------------------------------------------------------------------------------
$bind = $BindAddress.Trim()
if ($bind -in @('+', '*')) { throw "Bind curinga '$bind' não existe aqui: use 0.0.0.0 (todas as interfaces) ou um IP." }
$loopbackNames = @('localhost', '127.0.0.1')
$isLoopbackOnly = $loopbackNames -contains $bind.ToLowerInvariant()
if (-not $isLoopbackOnly) {
    $ip = $null
    if (-not [Net.IPAddress]::TryParse($bind, [ref]$ip)) {
        throw "BindAddress '$bind' não é um endereço IP. O serviço liga por ENDEREÇO (IP ou 0.0.0.0), não por nome: o nome (ex.: lowcoderunner.local) é resolvido no cliente via /etc/hosts."
    }
    if ($ip.AddressFamily -ne 'InterNetwork') { throw 'Somente IPv4 é suportado no bind.' }
    if ([Net.IPAddress]::IsLoopback($ip)) { $isLoopbackOnly = $true }
}

if (-not $isLoopbackOnly) {
    if ($AllowedRemoteAddress.Count -eq 0) {
        throw 'Bind não-loopback exige -AllowedRemoteAddress (IP/CIDR da API, ex.: 172.25.32.5). O serviço não tem autenticação; o firewall é a única barreira.'
    }
    $bad = $AllowedRemoteAddress | Where-Object { $_ -in @('Any', '*', '0.0.0.0', '0.0.0.0/0', '::/0', 'LocalSubnet') -or $_ -match '/0$' }
    if ($bad) { throw "Escopo de firewall amplo demais: $($bad -join ', '). Informe o IP/CIDR específico da API." }
}
$listenHost = if ($isLoopbackOnly) { 'localhost' } else { $bind }
$listenPrefix = "http://${listenHost}:$Port/"

# --- 1) Arquivos --------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
$logDir = Join-Path $DataDir 'logs'; $workDir = Join-Path $DataDir 'work'
New-Item -ItemType Directory -Force -Path $logDir, $workDir | Out-Null

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:01:00') }
Copy-Item $ExeSource $InstallDir -Force
$exeConfig = "$ExeSource.config"
if (Test-Path $exeConfig) { Copy-Item $exeConfig $InstallDir -Force }   # a config real vem das variáveis do serviço
$loggerXml = Join-Path (Split-Path $ExeSource -Parent) 'logger.xml'
if (Test-Path $loggerXml) { Copy-Item $loggerXml $InstallDir -Force }
$exePath = Join-Path $InstallDir (Split-Path $ExeSource -Leaf)

# --- 2) Conta e ACLs ----------------------------------------------------------------------------------------------
$accountArg = $ServiceAccount
if ($ServiceAccount -match 'LocalService$') {
    $accountSid = New-Object Security.Principal.SecurityIdentifier 'S-1-5-19'   # SID: funciona em Windows localizado
    $accountArg = $accountSid.Translate([Security.Principal.NTAccount]).Value
    $aclPrincipal = '*S-1-5-19'
} else {
    $aclPrincipal = $ServiceAccount
}
& icacls $DataDir /inheritance:r /grant:r "${aclPrincipal}:(OI)(CI)M" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Falha ao aplicar ACL em $DataDir" }
if (-not $SkipGrantSysmiddleAccess) {
    foreach ($p in @($GlobalFolder, $SysmiddleDir, $InstallDir) | Select-Object -Unique) {
        & icacls $p /grant "${aclPrincipal}:(OI)(CI)RX" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Falha ao conceder leitura/execução a '$ServiceAccount' em $p" }
    }
}

# --- 3) Serviço ---------------------------------------------------------------------------------------------------
if ($svc) { & sc.exe delete $ServiceName | Out-Null; Start-Sleep -Seconds 2 }
$create = @('create', $ServiceName, 'binPath=', "`"$exePath`" --service", 'start=', 'delayed-auto', 'obj=', $accountArg, 'DisplayName=', 'LayoutParser LowCode Runner')
if ($ServicePassword) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
    try { $create += @('password=', [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)); & sc.exe @create | Out-Null }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
} else {
    & sc.exe @create | Out-Null
}
if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o serviço (sc create).' }
& sc.exe description $ServiceName 'Executa mapeadores Sysmiddle (low-code) por HTTP para a LayoutParserApi.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# --- 4) Configuração do serviço = variáveis de ambiente por serviço (REG_MULTI_SZ) --------------------------------
$envValues = @(
    "LowCodeRunner__SysmiddleDir=$SysmiddleDir",
    "LowCodeRunner__GlobalFolder=$GlobalFolder",
    "LowCodeRunner__Package=$Package",
    "LowCodeRunner__ListenPrefix=$listenPrefix",
    "LowCodeRunner__MaxConcurrentRunners=$MaxConcurrentRunners",
    "LowCodeRunner__RunnerTimeoutSeconds=$RunnerTimeoutSeconds",
    "LowCodeRunner__MaxQueue=$MaxQueue",
    "LowCodeRunner__MaxBodyBytes=$MaxBodyBytes",
    "LowCodeRunner__LogDir=$logDir",
    "LowCodeRunner__WorkerTempDir=$workDir"
)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $envValues

# --- 5) Firewall: por porta + origem ------------------------------------------------------------------------------
$ruleName = "$ServiceName (TCP $Port)"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (-not $isLoopbackOnly) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Port -RemoteAddress $AllowedRemoteAddress -Profile Any `
        -Description 'LayoutParserLowCodeRunner: somente a API autorizada. Sem autenticação na aplicação.' | Out-Null
    Write-Host "Firewall: TCP $Port liberado apenas para $($AllowedRemoteAddress -join ', ')"
} else {
    Write-Host 'Bind loopback: nenhuma regra de firewall criada (não exposto à rede).'
}

# --- 6) Start + verificação (FALHA DE VERDADE) --------------------------------------------------------------------
Start-Service $ServiceName
$probe = if ($isLoopbackOnly -or $bind -eq '0.0.0.0') { '127.0.0.1' } else { $bind }
$base = "http://${probe}:$Port"

function Fail([string]$msg) {
    Write-Host "ERRO: $msg" -ForegroundColor Red
    Write-Host "Veja $logDir\service.log e o Event Viewer (Application)." -ForegroundColor Red
    exit 1
}

$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    $code = & curl.exe -s -o NUL -w '%{http_code}' --max-time 3 "$base/v1/health"
    if ($code -eq '200') { $ok = $true } else { Start-Sleep -Seconds 1 }
}
if (-not $ok) { Fail "GET $base/v1/health não respondeu 200 (último HTTP: $code)." }
Write-Host "OK: /v1/health 200 em $base"

if (-not $SkipDeepCheck) {
    Write-Host "Validando o SDK (health?deep=true; o init leva 12-38 s)..."
    $deep = & curl.exe -s -w "`n%{http_code}" --max-time $DeepCheckTimeoutSeconds "$base/v1/health?deep=true"
    $deepCode = ($deep | Select-Object -Last 1)
    if ($deepCode -ne '200') { Fail "health?deep=true falhou (HTTP $deepCode): $(($deep | Select-Object -SkipLast 1) -join ' ')" }
    Write-Host 'OK: SDK Sysmiddle carregado, licença e package validados (deep).'
}
Write-Host "Serviço '$ServiceName' saudável em $listenPrefix" -ForegroundColor Green
