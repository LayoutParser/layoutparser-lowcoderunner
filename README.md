# LayoutParserLowCodeRunner

Executa mappers do Sysmiddle (`SysMiddle.ConnectUs.API.dll`) em processo. Dois modos:

- **Serviço HTTP (sidecar)**: Windows Service ou `--console`.
- **CLI legado**: `--sysmiddleDir --globalFolder --package --mapperId|--mapperName --inputFile --outputFile [--fileName]`.

As DLLs Sysmiddle são proprietárias e **não são versionadas** (`.gitignore`); ficam em `SysmiddleDir` na máquina.

## Configuração (`LayoutParserLowCodeRunner.exe.config`, `appSettings`)

| Chave | Padrão | Descrição |
|---|---|---|
| `ListenPrefix` | `http://127.0.0.1:5080/` | Prefixo do HttpListener |
| `SysmiddleDir` | — | Pasta com as DLLs Sysmiddle (local do Windows) |
| `GlobalFolder` | — | Pasta com `global.config` |
| `Package` | — | Package do Sysmiddle; vazio => `PackageNotConfigured` |
| `MaxConcurrency` | `1` | Execuções simultâneas; excedente recebe 429 |
| `TimeoutSeconds` | `180` | Timeout por request; estourou => 504 |
| `LogFile` | `%ProgramData%\LayoutParserLowCodeRunner\logs\runner.log` | Log do runner |

> **Atenção:** não se sabe se `APIManager`/`APIExecutor` são thread-safe. Mantenha `MaxConcurrency=1` até validar.
> No timeout a thread do Sysmiddle não é abortada; ela continua até terminar e o slot só é liberado então.

## Instalar como serviço

```powershell
sc.exe create LayoutParserLowCodeRunner binPath= "\"C:\svc\LayoutParserLowCodeRunner.exe\" --service" start= auto
sc.exe start LayoutParserLowCodeRunner
```

O `logger.xml` deve ficar ao lado do exe. Para testar em primeiro plano: `LayoutParserLowCodeRunner.exe --console`.

## API

- `POST /transform` — body JSON `{ "mapperId" | "mapperName", "content", "fileName"? }` (mapperId tem prioridade). 200 devolve o XML transformado (`application/xml`). Todas as respostas trazem `X-Correlation-Id`.
- `GET /mappers` — lista `[{Id, Name}]`. **Depende de um método de listagem no `APIExecutor`** (`GetMappers`/`GetAllMappers`/`ListMappers`/`GetMapperList`, ver `TransformEngine.MapperListMethodNames`); não validado contra as DLLs reais.
- `GET /health` — status, uptime, slots livres.

Erros: `{ "error": { "code", "exitCode", "message", "correlationId" } }`.

| Exit code | Nome | HTTP |
|---|---|---|
| 0 | Success | 200 |
| 2 | Unexpected | 500 |
| 3 | InvalidArguments | 400 |
| 4 | ConfigurationInvalid | 503 |
| 5 | SysmiddleLoadFailed | 503 |
| 6 | PackageNotConfigured | 503 |
| 7 | PackageNotFound | 404 |
| 8 | MapperNotFound | 404 |
| 9 | TransformFailed | 422 |
| 10 | Timeout | 504 |
| — | Limite de concorrência | 429 |

Fora do escopo: interpretador TCL (Fase 6).
