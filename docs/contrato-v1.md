# Contrato HTTP v1 — LayoutParserLowCodeRunner (real, como implementado)

Base: `http://lowcoderunner.local:5230` (nome resolvido no cliente via `/etc/hosts`; o serviço liga por IP).
Todos os exemplos abaixo foram capturados do serviço real rodando com o worker falso (`tools/FakeLowCodeRunner`).

## Regras gerais

- JSON UTF-8. Requests com corpo: **`Content-Length` obrigatório** (sem `Transfer-Encoding: chunked` → 411). Uma
  requisição por conexão (`Connection: close`). Limite de corpo `MaxBodyBytes` (20 MB) → 413.
- `X-Correlation-ID` (opcional; `[A-Za-z0-9-_.]`, ≤ 64): aceito ou gerado; **sempre** devolvido no header e, em erros,
  no corpo. Aparece em toda linha de log do serviço e do worker.
- Cancelar a request (fechar a conexão) cancela a execução: o worker é morto e o slot volta.
- Sem autenticação: isolamento de rede (firewall 172.25.32.5).
- **Formato de erro único** (inclusive erros de protocolo 404/405/411/413/431/500): `application/json` com
  **exatamente** `{"error":string,"exitCode":int,"code":string,"correlationId":string}`. `error` é fixo por causa e sem caminhos.

## Rotas

| Método/rota | Corpo de entrada | Sucesso |
|---|---|---|
| `GET /v1/health` | — | 200 / 503 |
| `GET /v1/health?deep=true` | — | 200 / 503 (sobe o SDK; lento) |
| `GET /v1/info` | — | 200 |
| `GET /v1/mappers[?refresh=true]` | — | 200 |
| `POST /v1/transform` | `{document,fileName,mapperId\|mapperName,nfePostProcessing?}` | 200 |
| `POST /v1/transform/batch` | `{document,fileName,candidates[],nfePostProcessing?,budgetSeconds?}` | 200 |

`mapperId` e `mapperName`: **exatamente um** (senão 400). `nfePostProcessing`: `true`/`false`/omitido (omitido = default
do serviço, `false`). `fileName`: nome lógico do documento (omitido = `documento.txt`).

## Exemplos

### `GET /v1/health` → 200

```json
{"globalFolder":"ok","license":"unchecked","package":"ok","status":"ok"}
```

`503` (degradado): `{"globalFolder":"failed","license":"unchecked","package":"failed","reason":"globalFolder/global.config invalido; package nao configurado","status":"degraded"}`.
`license` ∈ `ok | unchecked | failed | unknown`; só `?deep=true` prova a licença. **Readiness da API: usar o barato.**

### `GET /v1/info` → 200

```json
{"build":"1.0.0+57c5e05d8ce93b6360e4e37d59b6e96b73760324","maxConcurrent":2,"package":"PKG","queued":0,"running":0,"uptimeSeconds":3,"version":"1.0.0.0","x86":true}
```

### `GET /v1/mappers` → 200

```json
[{"id":"MAP_1","name":"Mapper Um"},{"id":"MAP_2","name":"Mapper Dois"}]
```

### `POST /v1/transform` → 200

```json
// request
{"document":"<a>oi</a>","fileName":"NFE.txt","mapperId":"MAP_1"}
// response (X-Correlation-ID no header)
{"durationMs":258,"mapperId":"MAP_1","output":"<fake mapper=\"MAP_1\" file=\"NFE.txt\" chars=\"9\" nfe=\"false\"/>","warnings":[]}
```

`mapperId` só vem quando o request usou `mapperId`. `warnings` é sempre `[]`. `output` é o XML como string.

### `POST /v1/transform/batch` → 200 (sempre, mesmo com falhas por candidato)

```json
// request
{"document":"d","fileName":"NFE.txt","candidates":[{"mapperId":"MAP_1"},{"mapperName":"B"},{"mapperName":"C"}],"budgetSeconds":3}
// response (exemplo com parcial)
{"budgetSeconds":3,"completed":2,"partial":true,"waves":2,"results":[
 {"index":0,"mapperId":"MAP_1","status":"ok","exitCode":0,"durationMs":276,"output":"<...>"},
 {"index":1,"mapperName":"B","status":"failed","exitCode":8,"durationMs":234,"error":"Mapeador nao encontrado no package.","code":"mapper_not_found"},
 {"index":2,"mapperName":"C","status":"timeout","durationMs":3001,"error":"Tempo limite excedido.","code":"timeout"}]}
```

`status` ∈ `ok | failed | timeout | skipped` (`skipped` = nem chegou a rodar; sem `durationMs` útil). `results` mantém
a ordem e o `index` do request. `waves = ⌈candidatos/slots⌉`. `budgetSeconds` efetivo =
`min(waves × RunnerTimeoutSeconds, budgetSeconds do request; 90 s se inválido/ausente)`. Estourou o orçamento: workers
em execução são mortos (`timeout`) e o que já terminou é devolvido (`partial:true`). Máx. `MaxCandidates` (16) → 400.

### Erros

```json
// 400 — {"document":"d"}   (sem mapper)
{"code":"invalid_request","correlationId":"312bb1a6472540d9b1568427d8223c48","error":"Informe exatamente um entre mapperId e mapperName.","exitCode":7}
// 404 — mapper não resolvido
{"code":"mapper_not_found","correlationId":"5f9811fa8ee24bddbfa43e5a490c0020","error":"Mapeador nao encontrado no package.","exitCode":8}
// 422 — resultado vazio
{"code":"empty_result","correlationId":"8384fcfc49044773bb9a0ae05b5d8b01","error":"O mapeador retornou resultado vazio.","exitCode":5}
// 422 — documento vazio (rejeitado pelo serviço, sem subir worker)
{"code":"empty_document","correlationId":"0c1f5e0a7b3d4a2e9f6d8b1c2a3e4f50","error":"Documento de entrada vazio.","exitCode":4}
// 504 — timeout de execução (worker morto)
{"code":"timeout","correlationId":"d0dfb247e90c48a7af23e8ab9f367565","error":"Tempo limite de execucao excedido.","exitCode":-1}
```

(Exemplos adaptados fielmente do formato real: campo `code` adicionado; correlationIds ilustrativos.)

`code` é **estável** (usar este para decidir; `error` é só texto humano). Campo aditivo: `error`, `exitCode` e
`correlationId` seguem como antes. Mapeamento único em `Service/Application/ErrorCodes.cs`.

| HTTP | Causa | `exitCode` | `code` | Retry-After |
|---|---|---|---|---|
| 400 | JSON inválido; mapper ausente/ambíguo; candidato inválido/lista vazia/acima do máximo | 7 | `invalid_request` | — |
| 404 | mapper não resolvido | 8 | `mapper_not_found` | — |
| 404 | rota inexistente | 0 | `route_not_found` | — |
| 405 / 411 / 413 / 431 | método errado / sem Content-Length (chunked) / corpo grande / cabeçalhos grandes | 0 | `invalid_request` | — |
| 422 | falha de transformação | 1 | `transform_failed` | — |
| 422 | documento vazio (rejeitado pelo serviço, sem worker) | 4 | `empty_document` | — |
| 422 | entrada não encontrada (exit 4 vindo do worker) | 4 | `input_not_found` | — |
| 422 | resultado vazio | 5 | `empty_result` | — |
| 422 | package não configurado | 9 | `package_not_configured` | — |
| 422 | package não encontrado/licença | 10 | `package_not_found` | — |
| 503 **sem** Retry-After | bootstrap/licença do SDK (exit 3); serviço sem `GlobalFolder` | 3 | `runner_unavailable` | — |
| 503 **com** `Retry-After` | fila cheia (`MaxQueue`) | 0 | `queue_full` | segundos (30) |
| 504 | timeout de execução (`RunnerTimeoutSeconds`) | -1 | `timeout` | — |
| 499 | cliente cancelou (raramente observável pelo cliente) | -1 | `client_closed_request` | — |
| 500 | interno / exit 2, 6 ou desconhecido | 1 / 2 / 6 | `runtime_error` | — |

**exit 4 (decisão):** "documento vazio" é distinguível. O serviço valida o documento antes de subir o worker e responde
422 com exitCode 4 e `empty_document`; um exit 4 que venha do worker (arquivo de entrada não encontrado) sai como
`input_not_found`. Mesmo `exitCode`, `code` diferentes.

No batch, cada resultado `failed`/`timeout`/`skipped` traz também `code` (mesmos valores; `skipped` →
`not_executed`, `timeout` → `timeout`); `ok` não tem `code`.

`exitCode`: os do worker (`RunnerExitCodes`: 0 Ok, 1 Fatal, 2 UsageError, 3 BootstrapFailed, 4 InputNotFound, 5 EmptyResult,
6 SweepAllFailed, 7 InvalidNamedArgument, 8 MapperNameUnresolved, 9 PackageNotConfigured, 10 PackageNotFound); `-1` =
falha sem exit de worker (timeout/cancelamento); `0` = sem exit (rota/método/fila).

**Dois tipos de 503:** com `Retry-After` = fila cheia (tentar de novo); sem = runner indisponível (não adianta insistir
agora; verificar `/v1/health`).

## Desvios da especificação inicial

| # | Especificado | Real | Impacto na API |
|---|---|---|---|
| 1 | `HttpListener` | `TcpListener` (detecta desconexão; sem urlacl; bind por IP) | nenhum no wire |
| 2 | fila cheia = 429 | **503 + Retry-After** | tratar 503 com Retry-After |
| 3 | `/health` prova licença | barato = `"license":"unchecked"`; prova só com `?deep=true` | readiness usa o barato |
| 4 | `mapperId` sempre na resposta | só quando o request usou `mapperId` | não depender com `mapperName` |
| 5 | `warnings` | sempre `[]` | ignorar |
| 6 | 400 sem mapper | usa `DefaultMapperName` se configurado (senão 400) | enviar mapper explícito |
| 7 | — | só `Content-Length`; chunked → 411 | enviar Content-Length |
| 8 | — | exit 3 (falha de bootstrap) emitido pelo worker → 503 | 503 sem Retry-After = indisponível |
| 9 | — | erros de protocolo em JSON (não `text/plain`) | um só parser de erro |

## Tempos

Ver README/relatório: o custo dominante é o **init do SDK por worker** (12–38 s, medição histórica da API). Medição
desta sessão (máquina de desenvolvimento, SDK real 4.4.1, **sem** a instância Sysmiddle do servidor): o SDK inicializa
por ~17 s e falha ao abrir a configuração da instância → exit 3. Tempos com licença/instância reais: **NÃO
VERIFICADO** (pendente do host).
