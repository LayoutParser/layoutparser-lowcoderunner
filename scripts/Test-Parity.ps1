<#
.SYNOPSIS
    Paridade: o XML do SERVICO HTTP deve ser IDENTICO ao do exe de console (o que a API chamava por Process.Start).
    Roda SO no host Windows licenciado (precisa do SDK Sysmiddle e do servico instalado/rodando).

.DESCRIPTION
    Para cada par do corpus (pasta -CorpusDir) executa:
      (a) o exe em modo worker/CLI nomeado  -> baseline
      (b) POST /v1/transform no servico      -> candidato
    e compara os bytes. Nenhum documento e copiado para o repositorio nem logado: o corpus vive fora do git.

    Corpus: um manifesto TSV em -CorpusDir\manifest.tsv, colunas:  arquivo<TAB>mapperId<TAB>nfePostProcessing(opcional)
    Cada 'arquivo' e relativo a -CorpusDir. Exemplo de linha:  amostra1.txt<TAB>MAP_xxxxxxxx<TAB>false

.PARAMETER Exe          Caminho do LayoutParserLowCodeRunner.exe (na Bin Sysmiddle).
.PARAMETER GlobalFolder Pasta do global.config do host.
.PARAMETER Package      Package Sysmiddle.
.PARAMETER ServiceUrl   Base do servico, ex.: http://localhost:5230
.PARAMETER CorpusDir    Pasta com manifest.tsv e os documentos (fora do git).
.PARAMETER NormalizeXmlDecl  Tolera APENAS o espaco duplo em '<?xml  version=' (mesma regra do gate historico).

Saida: exit 0 se todos identicos; 1 se houver diferenca. Imprime N amostras / N diferencas (sem conteudo).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$GlobalFolder,
    [Parameter(Mandatory)][string]$Package,
    [string]$ServiceUrl = 'http://localhost:5230',
    [Parameter(Mandatory)][string]$CorpusDir,
    [switch]$NormalizeXmlDecl
)

$ErrorActionPreference = 'Stop'
$manifest = Join-Path $CorpusDir 'manifest.tsv'
foreach ($p in @($Exe, $GlobalFolder, $manifest)) { if (-not (Test-Path $p)) { Write-Error "Nao encontrado: $p"; exit 1 } }

$tmp = Join-Path $env:TEMP ("parity-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$total = 0; $diffs = 0

try {
    foreach ($line in Get-Content $manifest) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $cols = $line -split "`t"
        $file = Join-Path $CorpusDir $cols[0]; $mapper = $cols[1]
        $nfe = if ($cols.Count -gt 2 -and $cols[2]) { $cols[2] } else { 'false' }
        $total++

        # (a) baseline: exe de console
        $baseOut = Join-Path $tmp "base_$total.xml"
        & $Exe --globalFolder $GlobalFolder --package $Package --mapperId $mapper --inputFile $file `
               --outputFile $baseOut --fileName (Split-Path -Leaf $file) --nfePostProcessing $nfe | Out-Null
        $baseExit = $LASTEXITCODE

        # (b) servico
        $body = @{ document = [IO.File]::ReadAllText($file); fileName = (Split-Path -Leaf $file)
                   mapperId = $mapper; nfePostProcessing = ($nfe -eq 'true') } | ConvertTo-Json -Compress
        $svcOut = $null; $svcStatus = 0
        try {
            $r = Invoke-WebRequest -Uri "$ServiceUrl/v1/transform" -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
                 -ContentType 'application/json' -UseBasicParsing -TimeoutSec 300
            $svcStatus = [int]$r.StatusCode
            $svcOut = ($r.Content | ConvertFrom-Json).output
        } catch { $svcStatus = [int]$_.Exception.Response.StatusCode }

        $ok = $false
        if ($baseExit -eq 0 -and $svcStatus -eq 200 -and (Test-Path $baseOut)) {
            # O worker grava UTF-8 sem BOM; o servico devolve o mesmo texto lido desse arquivo.
            $a = [IO.File]::ReadAllText($baseOut, [Text.Encoding]::UTF8)
            $b = $svcOut
            if ($NormalizeXmlDecl) { $a = $a -replace '<\?xml\s+version=', '<?xml version='; $b = $b -replace '<\?xml\s+version=', '<?xml version=' }
            $ok = $a -ceq $b
        } elseif ($baseExit -ne 0 -and $svcStatus -ne 200) {
            $ok = $true   # ambos falharam (paridade de falha); registrar abaixo
        }

        # Nunca imprime conteudo: so indice, mapper, exit/status.
        $tag = if ($ok) { 'OK  ' } else { 'DIFF' }
        Write-Host ("{0} #{1} mapper={2} baseExit={3} svcStatus={4}" -f $tag, $total, $mapper, $baseExit, $svcStatus)
        if (-not $ok) { $diffs++ }
    }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "PARIDADE: $total amostra(s), $diffs diferenca(s)"
exit $(if ($diffs -eq 0 -and $total -gt 0) { 0 } else { 1 })
