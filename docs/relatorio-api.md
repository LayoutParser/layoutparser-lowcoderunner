# Relatório para o lado da API (LayoutParserApi)

Versão do contrato: **v1** (`/v1/...`). Detalhes e tabelas completas: [README](../README.md).

## O que muda na API

`LowCodeTransformationService` deixa de fazer `Process.Start` e passa a ser um **cliente HTTP** do serviço.
Continuam na API (não movem): `LowCodeAutoTransformationService`, `LowCodeTransformationStore`,
`LowCodeTransformationEligibility`, `LowCodePositionalMetadata`, `LowCodeLayoutGuidResolver`, `DocumentIdCalculator`,
`SysmiddleSectionMappingResolver`, `AllowedPackageGuids`, `MultiCandidateTopN`, `InlineXmlMaxChars`,
`TransformationCacheTtlHours`, `ProjectId`, `LowCodeTransformationsPath`.

Movem para o serviço: execução do mapeador, `SysmiddleDocumentRules`, concorrência/timeout/fila, orçamento do batch
(`LowCodeCandidatesBudget`), saneação (`LowCodeErrorSanitizer`), catálogo (LIST) e health. Na API, `RunnerPath`,
`SysmiddleDir`, `GlobalFolder`, `Package`, `RunnerTimeoutSeconds` e `MaxConcurrentRunners` deixam de ser usados
(agora são config do serviço); `CandidatesRequestTimeoutSeconds` continua na API e vira `budgetSeconds` do batch.

## Contrato (resumo)

| Rota | Body → Resposta |
|---|---|
| `POST /v1/transform` | `{document,fileName,mapperId\|mapperName,nfePostProcessing?}` → `200 {output,warnings,durationMs,mapperId}` |
| `POST /v1/transform/batch` | `{document,fileName,candidates:[{mapperId\|mapperName}],nfePostProcessing?,budgetSeconds?}` → `200 {results[],waves,budgetSeconds,completed,partial}` |
| `GET /v1/mappers` | `[{id,name}]` |
| `GET /v1/health[?deep=true]` / `GET /v1/info` | ver README |

Erro: `{error,exitCode,correlationId}` — 400 (7) · 404 (8) · 422 (1/4/5/9/10) · 503 (3, ou fila cheia com `Retry-After`) ·
504 · 500. Enviar `X-Correlation-ID` (o serviço o espelha e o põe em todo log). Cancelar a request cancela a execução.

### Desvios da especificação (para a API se adaptar)

1. **Servidor sobre TcpListener, não HttpListener** (HttpListener não expõe desconexão do cliente). Sem urlacl.
2. **`mapperId` na resposta do `/transform`** só vem quando o request usou `mapperId`; com `mapperName` o campo é
   omitido (o worker não devolve o GUID resolvido).
3. **`/v1/health` sem `deep`** não prova licença (`"license":"unchecked"`); `?deep=true` sobe o SDK (12-38 s). A
   sondagem periódica deve usar a forma barata; `deep` só em deploy/diagnóstico.
4. **`warnings`** é sempre `[]` por ora (o worker não tem canal de avisos).
5. **`DefaultMapperName`** é aplicado quando o request não traz nenhum mapper (a spec dizia 400).
6. Fila cheia usa **503 + Retry-After** (não 429). Exit 2 e 6 mapeiam para 500; exit 7 para 400.
7. Body só com `Content-Length` (sem `Transfer-Encoding: chunked`; 411).
8. **Branch base:** este repositório não tem `develop` (só `master`); o PR foi preparado contra `master` até o dono
   criar/indicar a branch.

## Portas, rede e instalação

- Porta padrão **5230** (`ListenPrefix`, default `http://localhost:5230/`). Para a rede: `http://0.0.0.0:5230/` **e**
  regra de firewall só para o IP do host da API (o `install-service.ps1` cria; exige `-AllowedRemoteIp`).
- Instalação: `scripts/install-service.ps1` (idempotente); remoção: `scripts/uninstall-service.ps1`.
- Validar de outra máquina da sub-rede autorizada: `curl http://<host>:5230/v1/health?deep=true`.

## O que o dono precisa fazer no host

- [ ] Conta de serviço **sem admin** (virtual/gMSA/local) com leitura na Bin Sysmiddle e no `globalfolder`.
- [ ] Bin Sysmiddle **v4.4.1** (log4net 2.x) e `global.config` com o `LicenseCode` **do host** (licença atrelada ao host).
- [ ] Definir `Package` (o `<PackageMappers>` do `config.xml` da instância).
- [ ] Firewall: allowlist do IP da API; bloquear o resto.
- [ ] No runner self-hosted Windows: variável de repositório `SYSMIDDLE_LIBS_DIR` (Bin Sysmiddle). Label do job:
      `[self-hosted, windows, dev-local]`.
- [ ] Fornecer o corpus de paridade (fora do git) e rodar `scripts/Test-Parity.ps1` no host licenciado.

## Estado de validação (honesto)

| Item | Estado |
|---|---|
| Testes portados (args, budget, sanitizer) | ✅ verdes |
| Contrato HTTP com executor falso (200/400/404/422/503/504/413, correlation, fila, batch, cancelamento, carga leve, logs sem vazamento) | ✅ verdes |
| Build sem DLLs (stub) e `--console` | ✅ verificado localmente |
| Build **real** com SDK Sysmiddle (`SYSMIDDLE`) | ⛔ **não executado**: sem as DLLs neste ambiente. O código do worker é o do runner atual, com mudanças apenas de nome/compilação condicional |
| **Paridade** byte a byte com o exe de console | ⛔ **0 amostras**: exige host licenciado + corpus. Script pronto (`Test-Parity.ps1`) |
| Scripts de instalação | ⚠️ sintaxe validada; **não executados** em host real |
| Serviço RUNNING via script / health de outra máquina / bloqueio de firewall | ⛔ pendente no host |

## Limites conhecidos

- **Thread-safety das DLLs**: contornada por processo (um worker por execução); `MaxConcurrentRunners=2` mantém o
  default da API — o host FiatMQ é sensível a concorrência, valide antes de subir.
- **Custo por execução**: cada worker paga o init do SDK (12-38 s) — mesmo custo do `Process.Start` atual. Um pool de
  workers quentes seria a otimização natural (não feita).
- **Memória**: até `MaxConcurrentRunners` processos x86 (limite de ~2-4 GB de endereço cada) + o corpo do documento
  no serviço (até `MaxBodyBytes`). Não medido.
- **Cache do `/v1/mappers`** por `MapperCacheSeconds`; `?refresh=true` força.
- O `runner.log` do worker registra o `fileName` lógico do documento (não o conteúdo).
