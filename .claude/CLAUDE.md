# LayoutParser LowCode Runner — Claude

Serviço Windows (net481, x86): API HTTP v1 que executa mappers do Sysmiddle em workers (processos filhos) + CLI/worker legado. Build real só no runner self-hosted `[self-hosted, windows, production]` com as DLLs fora do git. Ver `README.md`.

## Agentes (`.claude/agents/`)

| Agente | Persona | Papel |
|--------|---------|-------|
| `@lp-architect` | Aria | Arquitetura e trade-offs (não codifica) |
| `@lp-dev` | Dex | Implementação C# net48 |
| `@lp-qa` | Quinn | Quality gate, contrato HTTP/exit codes |
| `@lp-doc` | Duda | README fiel ao código |
| `@lp-pm` | Pia | Issues/backlog no GitHub |
| `@lp-devops` | Gage | **Exclusivo:** push, PR/merge, CI, serviço |

Fluxo: `@lp-architect` → `@lp-dev` → `@lp-qa` → `@lp-doc` → `@lp-devops`.

## Regras

- `git push` e `gh pr create/merge` só via `@lp-devops`, com build verde e pedido explícito do usuário.
- Nunca versionar DLLs Sysmiddle (`SysMiddle*.dll`) nem segredos.
- Build: `dotnet build LayoutParserLowCodeRunner.csproj -c Release`.
- Fora de escopo: interpretador TCL (Fase 6).
