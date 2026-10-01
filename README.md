# LayoutParserLowCodeRunner

Serviço Windows (net481, **x86**) que concentra a execução low-code (**Sysmiddle**) para a LayoutParserApi. A API
(ASP.NET Core, migrando para Linux) chama este serviço por HTTP em rede isolada, em vez de fazer `Process.Start`
localmente. Continua funcionando como **CLI/worker** (formas posicional e nomeada, `LIST`, `SWEEP`).

- Não compila/roda no Linux: build real e deploy são Windows. O host precisa da **licença Sysmiddle** (do host).
- Interpretador TCL / eliminar o runner proprietário: **fora de escopo** (Fase 6, issue #581).
- **As DLLs Sysmiddle nunca entram no git** (ver [Binários proprietários](#binários-proprietários-r0)).

## Arquitetura

```
 API (Linux) ──HTTP──▶ Serviço Windows (--service)              Workers (processos filhos, x86)
                      ┌───────────────────────────────┐        ┌──────────────────────────────┐
                      │ Http     MiniHttpServer/Router │        │ LayoutParserLowCodeRunner.exe │
                      │ Application Transform/Batch/   │ spawn  │  (mesmo exe, forma nomeada)   │
                      │   Catalog/Health/Budget/       ├───────▶│  SDK Sysmiddle + DocumentRules│
                      │   ErrorSanitizer               │  kill  └──────────────────────────────┘
                      │ Infra  ExecutionGate (slots+   │
                      │   fila) WorkerLauncher, Options│
                      │   RollingLogger                │
                      └───────────────────────────────┘
```

- **Um worker por execução**: `kill` duro no timeout/cancelamento, isolamento do estado estático e da
  thread-safety desconhecida das DLLs, e um crash não derruba o serviço. O protocolo com o worker é o **mesmo** da
  API atual (`--globalFolder --package --inputFile --outputFile --fileName --correlationId --runnerLogFile
  (--mapperId|--mapperName) --nfePostProcessing`), o que dá paridade por construção.
- Execução em processo (sem worker) **não** foi implementada; só como otimização futura atrás de flag, se a
  thread-safety for medida.
- Servidor HTTP próprio sobre `TcpListener` (sem ASP.NET Core). *Desvio da especificação, que citava
  HttpListener:* o `HttpListener` não expõe a desconexão do cliente, e o contrato exige que quem desiste não segure
  slot. Bônus: não há **urlacl** para reservar (nem privilégio de admin para escutar).

## Modos do exe

| Comando | Modo |
|---|---|
| `LayoutParserLowCodeRunner.exe --service` | Serviço Windows (binPath do serviço) |
| `LayoutParserLowCodeRunner.exe --console` | Mesmo host em primeiro plano (dev; Ctrl+C) |
| `LayoutParserLowCodeRunner.exe [--worker] --globalFolder … ` | Worker / CLI nomeado (o que a API chamava) |
| `LayoutParserLowCodeRunner.exe <globalFolder> <package> <mapperGuid\|LIST> <input> <output>` | CLI posicional legado |
| `LayoutParserLowCodeRunner.exe SWEEP <globalFolder> <package> <mapperGuid> <exemplos> <saida>` | Lote legado |

O modo serviço é **explícito** (`--service`): o CLI também roda sem sessão interativa (ex.: pelo IIS).

## Contrato HTTP v1

JSON UTF-8. `X-Correlation-ID` é aceito (`[A-Za-z0-9-_.]`, ≤ 64) ou gerado, e espelhado na resposta e em todo log.
Corpo acima de `MaxBodyBytes` (20 MB) → **413**. Só `Content-Length` (chunked → 411). Uma requisição por conexão.

| Rota | Descrição |
|---|---|
| `GET /v1/health[?deep=true]` | `200 {"status":"ok","license":"ok\|unchecked","package":"ok","globalFolder":"ok"}` ou `503 {"status":"degraded","reason":"…"}`. Sem `deep` valida worker, `global.config` e package configurado + resultado do último LIST. Com `deep=true` **sobe o SDK** (LIST): valida DLLs, licença e package de verdade (12-38 s). |
| `GET /v1/info` | `{"version","build","x86","uptimeSeconds","package","maxConcurrent","running","queued"}` |
| `GET /v1/mappers[?refresh=true]` | `[{"id","name"}]` (modo LIST, cache `MapperCacheSeconds`) |
| `POST /v1/transform` | `{"document","fileName","mapperId\|null","mapperName\|null","nfePostProcessing":bool\|null}` → `200 {"output","warnings":[],"durationMs","mapperId"}` |
| `POST /v1/transform/batch` | `{"document","fileName","candidates":[{"mapperId\|mapperName"}],"nfePostProcessing","budgetSeconds"?}` → `200 {"results":[{"index","mapperId","mapperName","status":"ok\|failed\|timeout\|skipped","output\|error","exitCode","durationMs"}],"waves","budgetSeconds","completed","partial"}` |

- `mapperId` e `mapperName`: **exatamente um**, senão 400. Se nenhum vier e existir `DefaultMapperName`, ele é usado.
- **Batch**: todos os candidatos disputam os slots (ondas = ⌈candidatos/slots⌉). Orçamento efetivo =
  `min(ondas × RunnerTimeoutSeconds, budgetSeconds do cliente; 90 s se inválido)`. Estourou o orçamento ou o cliente
  desistiu: workers em execução são mortos (`timeout`), os que não chegaram a rodar ficam `skipped`, e **o que já
  terminou é devolvido** (`partial:true`). Sempre 200 (falhas por candidato); 4xx só para request inválido
  (lista vazia, candidato ambíguo, mais de `MaxCandidates`).
- **Cancelamento**: se o cliente fechar a conexão, o token é cancelado, o worker é morto e o slot volta.

### Contrato v1 real (desvios da especificação inicial)

| # | Especificado | Implementado | Impacto no cliente (API) |
|---|---|---|---|
| 1 | `HttpListener` | `TcpListener` (detecta desconexão do cliente; sem urlacl; bind por IP) | nenhum no wire |
| 2 | fila cheia = 429 | **503 + `Retry-After`** | tratar 503 com `Retry-After` |
| 3 | `/health` prova a licença | barato: `"license":"unchecked"`; prova só com `?deep=true` (sobe o SDK) | readiness usa o barato |
| 4 | `mapperId` sempre na resposta | só quando o request usou `mapperId` | não depender do campo com `mapperName` |
| 5 | `warnings` | sempre `[]` (worker sem canal de avisos) | ignorar por ora |
| 6 | 400 sem mapper | usa `DefaultMapperName` se configurado (senão 400) | a API deve enviar mapper explícito |
| 7 | — | só `Content-Length`; `chunked` → **411** | enviar `Content-Length` |
| 8 | — | exit 3 (falha de bootstrap do SDK) agora é emitido pelo worker (antes caía em 1) → 503 | tratar 503 sem `Retry-After` como "runner indisponível" |
| 9 | — | `runner.log` do worker sai com caminhos sanitizados | — |

### Erros

Todo erro: `{"error":"<sanitizado>","exitCode":N,"correlationId":"…"}`. A mensagem é fixa por exit code e passa
pelo `LowCodeErrorSanitizer` (caminhos → `[caminho interno]`); nunca há stack, caminho ou conteúdo de documento.
`exitCode` `-1` = falha sem exit de worker (timeout/cancelamento); `0` = sem exit (rota/método).

| HTTP | Quando | exitCode |
|---|---|---|
| 400 | JSON inválido; mapper ausente/ambíguo; candidato inválido | 7 |
| 404 | `mapperName`/`mapperId` não resolvido; rota inexistente | 8 / 0 |
| 405 / 411 / 413 | método errado / sem Content-Length / corpo grande | — |
| 422 | documento vazio; falha de transformação; entrada não encontrada; resultado vazio; package não configurado / não encontrado | 4 / 1 / 4 / 5 / 9 / 10 |
| 503 | bootstrap/licença do SDK (exit 3); serviço sem `GlobalFolder`; **fila cheia** (com `Retry-After`) | 3 / 0 |
| 504 | timeout de execução (worker morto) | -1 |
| 500 | interno / exit 2 / 6 / desconhecido | 1 / 2 / 6 |

Exit codes do worker (`RunnerExitCodes`): 0 Ok, 1 Fatal, 2 UsageError (posicional), 3 BootstrapFailed, 4 InputNotFound,
5 EmptyResult, 6 SweepAllFailed, 7 InvalidNamedArgument, 8 MapperNameUnresolved, 9 PackageNotConfigured, 10 PackageNotFound.

## Configuração (do serviço; o cliente **não** envia caminhos)

Prioridade: variável de ambiente `LowCodeRunner__<Chave>` › `appSettings` do `LayoutParserLowCodeRunner.exe.config` › default.
Valor inválido cai no default com aviso no log de inicialização.

| Chave | Default | Descrição |
|---|---|---|
| `SysmiddleDir` | — | Pasta da instância Sysmiddle (diagnóstico; o SDK resolve as DLLs pelo app base do exe) |
| `GlobalFolder` | — | Pasta com o `global.config` (licença + paths locais). Ausente → 503 |
| `Package` | — | Identificador do projeto Sysmiddle (`<PackageMappers>` do `config.xml`). Vazio → exit 9 |
| `DefaultMapperName` | — | Mapper quando o request não informa nenhum |
| `RunnerTimeoutSeconds` | `180` | Timeout por execução (medido 48-137 s) |
| `MaxConcurrentRunners` | `2` | Slots simultâneos |
| `MaxQueue` | `8` | Requisições únicas esperando slot; acima → 503 + `Retry-After` |
| `MaxBodyBytes` | `20971520` | Limite do body (413) |
| `NfePostProcessingDefault` | `false` | Pós-processamento NF-e quando o request não informa |
| `ListenPrefix` | `http://localhost:5230/` | Bind (`localhost`, IP, ou `http://+:5230/`) |
| `LogDir` | `%ProgramData%\LayoutParserLowCodeRunner\logs` | `service.log` + `runner.log` (rotação 10 MB × 10) |
| `WorkerTempDir` | `%ProgramData%\LayoutParserLowCodeRunner\work` | Entrada/saída temporárias (removidas no `finally`) |
| `WorkerExePath` | o próprio exe | Exe do worker (ex.: dentro da Bin Sysmiddle) |
| `MapperCacheSeconds` / `MaxCandidates` / `GracefulShutdownSeconds` / `QueueRetryAfterSeconds` | `300` / `16` / `30` / `30` | |

Logs: correlationId em toda linha; **nunca** documento, XML de saída, caminhos internos ou credenciais — só ids,
tamanhos, tempos e exit codes. (O `runner.log` do worker registra o `fileName` lógico do documento.)

## Operação

### Instalar / remover

```powershell
# PowerShell ELEVADO no host Windows. API = VM Ubuntu 172.25.32.5.
.\scripts\install-service.ps1 -SysmiddleDir '<Bin Sysmiddle>' -GlobalFolder '<pasta do global.config>' `
    -Package '<package>' -BindAddress 0.0.0.0 -Port 5230 -AllowedRemoteAddress 172.25.32.5
.\scripts\uninstall-service.ps1 [-RemoveFiles -InstallDir …] [-PurgeData]
```

Ambos são idempotentes (mesmo padrão do `install-service.ps1` do layoutparser-decrypt). O instalador copia o exe
(por padrão **para dentro da Bin do Sysmiddle**: o worker resolve as DLLs pelo app base), cria o serviço
(`delayed-auto`, recovery 5 s/15 s/60 s), grava a config em variáveis de ambiente **do serviço** (`REG_MULTI_SZ`; nada
de segredo no repositório), aplica ACLs, cria a regra de firewall e **valida `/v1/health` e `/v1/health?deep=true` no
fim — se falhar, termina com exit 1**. Parâmetros de rede: `-BindAddress` (IP ou `0.0.0.0`; nome é recusado),
`-Port` (5230), `-AllowedRemoteAddress` (**obrigatório** quando o bind não é loopback). Firewall = porta + origem;
`Any`, `*`, `0.0.0.0`, `0.0.0.0/0`, `::/0`, `LocalSubnet` e qualquer `/0` são recusados. Portas 8080 (outra API do
host) e 5220 (decrypt) são recusadas. Sem urlacl. *O script tem a sintaxe validada, mas ainda **não foi executado**
num host real.*

### Nome, endereço e porta

O serviço liga por **endereço IP** (`TcpListener`), nunca por nome. O nome `lowcoderunner.local` (o decrypt é
`layoutparserdecrypt.local`:5220, ambos no **mesmo host Windows**) é resolvido **no cliente**: na VM Linux da API,

```
# /etc/hosts
<IP-do-host-Windows>  lowcoderunner.local  layoutparserdecrypt.local
```

`.local` é mDNS e pode deixar a resolução lenta no Ubuntu; por isso o `/etc/hosts`. Cliente da API:
`LowCode:BaseUrl=http://lowcoderunner.local:5230`. Porta do runner: **5230** (não 8080, não 5220).

### Conta de serviço (proposta; decisão do dono)

O worker precisa **ler** a Bin do Sysmiddle (DLLs), o `globalfolder` (`global.config` com a licença) e a instância
Sysmiddle que o SDK abre na inicialização, e **escrever** em `%ProgramData%\LayoutParserLowCodeRunner` (logs/work).
Default do script: `NT AUTHORITY\LocalService` (mínimo privilégio; ACLs concedidas pelo script). `LocalService` pode não
alcançar a instância/licença do host (ex.: pastas em `Program Files` com ACL restrita ou dependência do perfil do
usuário). Nesse caso, a menor conta que funciona é **uma conta local dedicada sem admin** (`.\svc-lowcoderunner`,
direito *Log on as a service*) com: leitura+execução em `SysmiddleDir`, `GlobalFolder` e na pasta da instância do
Sysmiddle, e modificação em `DataDir`. **Não usar** `LocalSystem`/administrador. Só o teste no host diz qual funciona.

### Segurança

Sem segredo compartilhado: a autenticação é a **rede isolada** (firewall com allowlist do IP do host da API). Sem
TLS/token/mTLS (decisão registrada; pode virar issue). `ListenPrefix` exposto na rede exige `-AllowedRemoteAddress`.

### Validar

```bash
curl http://<host>:5230/v1/health            # barato
curl "http://<host>:5230/v1/health?deep=true" # sobe o SDK: DLLs, licença, package, globalFolder
curl http://<host>:5230/v1/info
```

O `/v1/health` **não** prova a licença sem `deep` (`"license":"unchecked"`): só subir o SDK a prova.

### Troubleshooting

| Sintoma | Causa provável |
|---|---|
| 422 `exitCode` 9 | `Package` vazio |
| 422 `exitCode` 10 | `Package` não bate com o `config.xml` da instância, ou licença não validada |
| 503 `exitCode` 3 | Falha no bootstrap do SDK (log4net 1.x na Bin, licença, DLL ausente). Veja `runner.log` |
| 503 sem exit / `Retry-After` | Fila cheia (`MaxQueue`) ou `GlobalFolder` não configurado |
| 504 | Worker passou de `RunnerTimeoutSeconds` (foi morto) |
| Saída só com o envelope da NF-e | Worker rodando 64-bit: o exe deve ser **x86** |

## Desenvolvimento e testes

```bash
dotnet build LayoutParserLowCodeRunner.csproj -c Release                 # sem DLLs: worker = stub (exit 3)
dotnet test  tests/LayoutParserLowCodeRunner.Tests                        # contrato HTTP com o executor falso
```

`tools/FakeLowCodeRunner` é o dublê do worker (cenários por conteúdo do documento: `SLEEP:<ms>`, `EXIT:<n>`, `EMPTY`,
e por mapeador: `SLOW:<ms>:x`, `FAIL:<n>:x`). Cobrem-se: 200/400/404/422/503/504/413, `X-Correlation-ID`, fila cheia,
batch (ordem, ondas, parcial), cancelamento (worker morto e slot devolvido), carga leve sem vazar processos/pastas,
ausência de vazamento nos logs — além dos testes portados da API (parser de argumentos, budget, sanitizer).

### Binários proprietários (R0)

O repositório contém **apenas código-fonte**. O build real define o símbolo `SYSMIDDLE` ao encontrar as DLLs:

- variável de ambiente/propriedade `SYSMIDDLE_LIBS_DIR` = Bin da instância Sysmiddle **v4.4.1** (a que tem
  `log4net` 2.x), ou uma pasta `libs\` na raiz (ignorada pelo git). Referências usadas: `SysMiddle.Base.dll` e
  `SysMiddle.ConnectUs.Core.dll` (`Private=false`; o exe roda de dentro da Bin).
- **De onde vêm as DLLs.** Da Bin da instância `Instance_FiatMQ` (`…\AppConnector.DIR\Bin`, v4.4.1, `DbProviderType=File`,
  trazida do servidor). Uma cópia dessa Bin está hoje em `layoutparser-api\tools\LowCodeRunner\Functions` (239 DLLs;
  `SysMiddle.Base`/`ConnectUs.Core` **4.4.1.0**, `log4net` **2.0.17**). Confira: `log4net.dll` da Bin deve ser **2.x**
  (1.2.13.0 estoura em `InstanceFactory.Initialize()`). Build local contra ela:
  `dotnet build -c Release -p:SYSMIDDLE_LIBS_DIR=<Bin>`. No runner self-hosted: variável **do repositório**
  `SYSMIDDLE_LIBS_DIR` (ver `build.yml`, job `build-real`).
- **Não** reintroduzir `appConnector.Client.Core/Interface` (bootstrap do host e threads que derrubavam o processo).
- O CI hospedado não tem as DLLs: compila o stub e roda os testes. O **build real** roda só no runner
  `[self-hosted, windows, production]` (`[self-hosted, windows, dev-local]` é a máquina de teste do dev; `windows` é obrigatório nos dois),
  com `SYSMIDDLE_LIBS_DIR` como variável do repositório, e nunca em `pull_request`. O artefato é um zip com exe,
  `.config`, `logger.xml` e scripts — **sem DLLs**. O exe sai em `bin\Release\net481\`.

### Paridade com o exe de console

```powershell
.\scripts\Test-Parity.ps1 -Exe 'C:\Sysmiddle\Bin\LayoutParserLowCodeRunner.exe' -GlobalFolder … -Package … `
    -CorpusDir 'D:\corpus' -ServiceUrl http://localhost:5230
```

Corpus (`manifest.tsv`: `arquivo<TAB>mapperId<TAB>nfe`) fica **fora do git**. Compara o XML do serviço com o do exe
byte a byte e imprime só índice/mapper/status. Contrato completo com exemplos JSON: [docs/contrato-v1.md](docs/contrato-v1.md). Relatório para a API: [docs/relatorio-api.md](docs/relatorio-api.md).
