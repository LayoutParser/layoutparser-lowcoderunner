// Nullable desligado explicitamente: este arquivo é compilado nos DOIS lados — no runner
// (net481, <Nullable>disable</Nullable>) e, via <Compile Include ... Link>, no projeto de testes
// (net10.0, <Nullable>enable</Nullable>). Fixar aqui garante semântica idêntica nas duas compilações.
#nullable disable

using System;
using System.Collections.Generic;

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Modo de execução resolvido a partir da linha de comando.
    /// </summary>
    internal enum RunnerMode
    {
        /// <summary>Executa um mapeador sobre um arquivo de entrada e grava o XML resultante.</summary>
        Exec,

        /// <summary>Lista os mapeadores do package (descoberta). Só existe na forma POSICIONAL.</summary>
        List,

        /// <summary>Varre uma pasta de exemplos e executa o mapeador para cada arquivo (lote/A1).</summary>
        Sweep
    }

    /// <summary>
    /// Códigos de saída do runner. Centralizados porque o LowCodeTransformationService (lado API)
    /// loga o exit code e ele é a única pista quando o stderr é engolido.
    /// </summary>
    internal static class RunnerExitCodes
    {
        public const int Ok = 0;
        public const int Fatal = 1;

        /// <summary>Forma POSICIONAL com argumentos de menos (uso impresso no stderr).</summary>
        public const int UsageError = 2;

        public const int BootstrapFailed = 3;
        public const int InputNotFound = 4;
        public const int EmptyResult = 5;
        public const int SweepAllFailed = 6;

        /// <summary>
        /// Forma NOMEADA malformada: flag desconhecida, flag sem valor ou obrigatório ausente.
        /// Código próprio (não reaproveita o 2) para separar "chamador humano errou o uso posicional"
        /// de "a API mandou um protocolo que o runner não entende" — foi exatamente esse segundo caso
        /// que ficou invisível por meses, degradando para exit=4 ("Input nao encontrado").
        /// </summary>
        public const int InvalidNamedArgument = 7;

        /// <summary>--mapperName informado, mas nenhum mapeador do package casa com esse nome.</summary>
        public const int MapperNameUnresolved = 8;

        /// <summary>
        /// <c>--package</c> ausente/vazio. Código PRÓPRIO e não reaproveitado do 7
        /// (protocolo malformado) nem do 3 (bootstrap): aqui o protocolo está correto e o runner
        /// está saudável — o que falta é CONFIGURAÇÃO (<c>LowCode:Package</c>).
        ///
        /// <para>Passou a existir em 2026-08-10, quando o runner deixou de depender do
        /// <c>appConnector.Client.Core</c>: antes o package vinha do
        /// <c>ConnectorApplicationManager.GetServerPackage()</c> (populado pelo bootstrap) e o
        /// <c>--package</c> vazio era irrelevante. Agora ele é a ÚNICA fonte do identificador do
        /// projeto — vazio não pode degradar em silêncio, porque o sintoma seria
        /// "mapeador não encontrado" ou saída vazia, longe da causa real.</para>
        /// </summary>
        public const int PackageNotConfigured = 9;

        /// <summary>
        /// O package informado não corresponde a nenhum projeto carregado do
        /// <c>exportContext.data</c> (ou a licença não validou). Separado do 9 porque a ação do
        /// operador é outra: lá falta configurar, aqui está configurado ERRADO.
        /// </summary>
        public const int PackageNotFound = 10;
    }

    /// <summary>
    /// Argumentos já normalizados, independentemente de terem chegado na forma posicional
    /// (uso offline, geração de gabaritos) ou nomeada (a API, via LowCodeTransformationService).
    /// </summary>
    internal sealed class RunnerArgs
    {
        public RunnerMode Mode { get; set; }

        /// <summary>Pasta que contém o global.config (licença + paths locais do Sysmiddle).</summary>
        public string GlobalFolder { get; set; }

        /// <summary>Package do Sysmiddle. Aceita vazio (a API manda "" quando LowCode:Package não está configurado).</summary>
        public string Package { get; set; }

        /// <summary>GUID do mapeador (--mapperId), ou o literal LIST na forma posicional.</summary>
        public string MapperGuid { get; set; }

        /// <summary>Nome do mapeador (--mapperName). Resolvido para GUID em runtime, contra o package.</summary>
        public string MapperName { get; set; }

        /// <summary>Arquivo de entrada (modo Exec).</summary>
        public string InputPath { get; set; }

        /// <summary>Arquivo de saída (modo Exec).</summary>
        public string OutputPath { get; set; }

        /// <summary>Pasta de exemplos (modo Sweep).</summary>
        public string ExamplesFolder { get; set; }

        /// <summary>Pasta de saída (modo Sweep).</summary>
        public string OutputFolder { get; set; }

        /// <summary>
        /// Nome LÓGICO do documento (--fileName). Diferente do nome do arquivo em disco: a API grava
        /// a entrada num temporário (in_&lt;guid&gt;.txt) e o nome real do documento do usuário — que
        /// pode influenciar a seleção de parser no Sysmiddle — só chega por aqui.
        /// </summary>
        public string FileName { get; set; }

        /// <summary>CorrelationId da request na API; vira prefixo de toda linha de log do runner.</summary>
        public string CorrelationId { get; set; }

        /// <summary>Arquivo de log do runner (a API calcula o caminho e o lê de volta quando o exit != 0).</summary>
        public string RunnerLogFile { get; set; }

        /// <summary>
        /// Diretório da instância Sysmiddle (--sysmiddleDir). ACEITO E IGNORADO de propósito:
        /// o .NET Framework resolve as ~300 DLLs do Sysmiddle pelo APP BASE (a pasta onde o .exe
        /// está instalado, hoje .../api/Functions/), não pelo diretório de trabalho nem por este
        /// argumento. Trocar o probing em runtime exigiria AppDomain próprio ou AssemblyResolve —
        /// custo sem benefício enquanto o .exe convive com suas dependências. É guardado só para
        /// aparecer no log de diagnóstico; se um dia o layout de instalação mudar, é daqui que sai.
        /// </summary>
        public string SysmiddleDir { get; set; }

        /// <summary>
        /// Liga o pós-processamento NF-e (<c>--nfePostProcessing true</c>). <b>Desligado por padrão.</b>
        /// Ver <see cref="SysmiddleDocumentRules.AplicarPosProcessamentoNFe"/>: o caminho que produziu
        /// o gabarito (<c>ExecuteMappingDocumentById</c>) NÃO aplica os três escapes, e ligá-los
        /// reserializa o XML inteiro — quebra a equivalência byte a byte.
        /// </summary>
        public bool NfePostProcessing { get; set; }

        /// <summary>
        /// Nome do documento a passar ao Sysmiddle: <see cref="FileName"/> quando veio, senão o nome
        /// do arquivo em disco (comportamento histórico, preservado para a forma posicional).
        ///
        /// <para>Vazio/whitespace conta como AUSENTE, não como nome válido: a API monta
        /// <c>--fileName (fileName ?? Path.GetFileName(inputPath))</c>, e um <c>fileName</c> string
        /// vazia (não nula) chegaria aqui como <c>""</c> — passar isso ao Sysmiddle como nome de
        /// documento é pior do que cair no nome do temporário.</para>
        ///
        /// <para>Mora aqui, e não inline no Program, porque o Program depende das DLLs x86 do
        /// Sysmiddle e não é testável; este arquivo é puro e roda na suíte.</para>
        /// </summary>
        public string ResolveDocumentName()
        {
            if (!string.IsNullOrWhiteSpace(FileName))
                return FileName;

            return string.IsNullOrEmpty(InputPath) ? null : System.IO.Path.GetFileName(InputPath);
        }

        /// <summary>
        /// Mensagem de erro quando o <c>--package</c> não veio (null/vazio/whitespace); <c>null</c>
        /// quando está tudo certo.
        ///
        /// <para>Validado no MOMENTO DA EXECUÇÃO e não no parse de propósito: para o parser, um
        /// <c>--package ""</c> é um valor bem-formado (a API já mandava exatamente isso quando
        /// <c>LowCode:Package</c> não estava configurado, e há teste travando esse aceite). O que
        /// mudou em 2026-08-10 foi a SEMÂNTICA: sem o <c>appConnector</c>, o package deixou de ter
        /// fallback via <c>ConnectorApplicationManager.GetServerPackage()</c> e virou obrigatório.
        /// Manter a separação preserva a distinção entre "a API falou um protocolo que não entendo"
        /// (exit 7) e "o protocolo está certo, falta configurar" (exit 9).</para>
        ///
        /// <para>Mora aqui, e não inline no Program, pelo mesmo motivo do
        /// <see cref="ResolveDocumentName"/>: o Program depende das DLLs x86 do Sysmiddle e não é
        /// testável; este arquivo é puro e roda na suíte.</para>
        /// </summary>
        public string ValidarPackage()
        {
            if (!string.IsNullOrWhiteSpace(Package))
                return null;

            return "--package (LowCode:Package) é obrigatório e veio vazio. "
                 + "O runner não tem mais fallback para o package do host (ConnectorApplicationManager): "
                 + "configure LowCode:Package no appsettings.json ou a variável de ambiente LowCode__Package "
                 + "com o identificador do projeto Sysmiddle (o mesmo <PackageMappers> do config.xml da instância).";
        }
    }

    /// <summary>Resultado do parse: ou os argumentos normalizados, ou o motivo + exit code.</summary>
    internal sealed class RunnerArgsParseResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public RunnerArgs Args { get; set; }

        /// <summary>Mensagens de erro (uma por problema encontrado), já em PT-BR e prontas pro stderr.</summary>
        public IList<string> Messages { get; set; }
    }

    /// <summary>
    /// Parser da linha de comando do runner. Entende DUAS formas:
    ///
    /// <para><b>Posicional</b> (uso offline/histórico, geração de gabaritos):
    /// <c>LayoutParserLowCodeRunner &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid|LIST&gt; &lt;input&gt; &lt;output&gt;</c>
    /// e <c>SWEEP &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid&gt; &lt;pastaExamples&gt; &lt;pastaSaida&gt;</c>.</para>
    ///
    /// <para><b>Nomeada</b> (a que a API fala, em LowCodeTransformationService):
    /// <c>--globalFolder X --package Y --inputFile A --outputFile B [--mapperId G | --mapperName N]
    /// [--fileName F] [--correlationId C] [--runnerLogFile L] [--sysmiddleDir D]</c>.</para>
    ///
    /// A escolha é feita pelo PRIMEIRO token: se começa com "--", é nomeada; senão, posicional.
    /// Não há forma mista — token solto no meio da forma nomeada é ERRO, nunca um posicional
    /// implícito. Esse é o ponto central: até 2026-08-10 o runner só tinha a forma posicional, então
    /// a chamada da API caía inteira nela — args[0] ("--sysmiddleDir") virava globalFolder e args[3]
    /// (o VALOR do --globalFolder) virava o input, produzindo o falso
    /// "exit=4 Input nao encontrado: ...\globalfolder".
    /// </summary>
    internal static class RunnerArgsParser
    {
        public const string UsoSingleShot =
            "Uso: LayoutParserLowCodeRunner <globalFolder> <package> <mapperGuid|LIST> <input> <output>";

        public const string UsoSweep =
            "Uso (lote): LayoutParserLowCodeRunner SWEEP <globalFolder> <package> <mapperGuid> <pastaExamples> <pastaSaida>";

        public const string UsoNomeado =
            "Uso (nomeado): LayoutParserLowCodeRunner --globalFolder <dir> --package <pkg> --inputFile <arq> --outputFile <arq> "
            + "(--mapperId <guid> | --mapperName <nome>) [--fileName <nome>] [--correlationId <id>] [--runnerLogFile <arq>] [--sysmiddleDir <dir>] "
            + "[--nfePostProcessing true|false]";

        /// <summary>
        /// Fonte ÚNICA das flags conhecidas: quem não está aqui é argumento desconhecido e derruba o
        /// parse com <see cref="RunnerExitCodes.InvalidNamedArgument"/>. Um dicionário (em vez de um
        /// switch + HashSet paralelo) evita a divergência silenciosa entre "conheço a flag" e
        /// "sei atribuir a flag" — divergência que reintroduziria justamente o deslocamento.
        ///
        /// <para>O delegate devolve <c>null</c> quando atribuiu, ou a MENSAGEM DE ERRO quando o valor
        /// é inválido para aquela flag. Assumir que todo valor serve funciona para as flags de string,
        /// mas não para <c>--nfePostProcessing</c>: um <c>--nfePostProcessing yes</c> seria ignorado
        /// e a flag ficaria desligada — quem pediu para ligar não saberia. É o mesmo padrão de falha
        /// silenciosa que este parser existe para eliminar.</para>
        /// </summary>
        private static readonly Dictionary<string, Func<RunnerArgs, string, string>> Setters =
            new Dictionary<string, Func<RunnerArgs, string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "globalFolder",       (a, v) => { a.GlobalFolder = v; return null; } },
                { "package",            (a, v) => { a.Package = v; return null; } },
                { "mapperId",           (a, v) => { a.MapperGuid = v; return null; } },
                { "mapperName",         (a, v) => { a.MapperName = v; return null; } },
                { "inputFile",          (a, v) => { a.InputPath = v; return null; } },
                { "outputFile",         (a, v) => { a.OutputPath = v; return null; } },
                { "fileName",           (a, v) => { a.FileName = v; return null; } },
                { "correlationId",      (a, v) => { a.CorrelationId = v; return null; } },
                { "runnerLogFile",      (a, v) => { a.RunnerLogFile = v; return null; } },
                { "sysmiddleDir",       (a, v) => { a.SysmiddleDir = v; return null; } }, // ver RunnerArgs.SysmiddleDir: aceito e ignorado
                { "nfePostProcessing",  AtribuirNfePostProcessing },
            };

        /// <summary>
        /// Aceita apenas <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c> (case-insensitive). Qualquer outra
        /// coisa é erro explícito — ver o comentário de <see cref="Setters"/>.
        /// </summary>
        private static string AtribuirNfePostProcessing(RunnerArgs args, string valor)
        {
            if (string.Equals(valor, "true", StringComparison.OrdinalIgnoreCase) || valor == "1")
            {
                args.NfePostProcessing = true;
                return null;
            }

            if (string.Equals(valor, "false", StringComparison.OrdinalIgnoreCase) || valor == "0")
            {
                args.NfePostProcessing = false;
                return null;
            }

            return "Valor invalido para --nfePostProcessing: '" + valor + "' (use true|false|1|0).";
        }

        public static RunnerArgsParseResult Parse(string[] args)
        {
            if (args == null)
                args = new string[0];

            return EhFlag(args.Length > 0 ? args[0] : null)
                ? ParseNomeado(args)
                : ParsePosicional(args);
        }

        /// <summary>Um token é flag quando começa com "--". Só o PRIMEIRO decide a forma.</summary>
        private static bool EhFlag(string token)
        {
            return !string.IsNullOrEmpty(token) && token.StartsWith("--", StringComparison.Ordinal);
        }

        /// <summary>
        /// Forma histórica, preservada byte a byte: é ela que o fluxo offline de geração de gabaritos
        /// usa (ver o &lt;summary&gt; de Program). Qualquer mudança de ordem/obrigatoriedade aqui
        /// quebraria esse fluxo.
        /// </summary>
        private static RunnerArgsParseResult ParsePosicional(string[] args)
        {
            bool isSweep = args.Length > 0 && string.Equals(args[0], "SWEEP", StringComparison.OrdinalIgnoreCase);

            if (isSweep && args.Length < 6)
                return Falha(RunnerExitCodes.UsageError, UsoSweep);

            if (!isSweep && args.Length < 5)
                return Falha(RunnerExitCodes.UsageError, UsoSingleShot, UsoSweep, UsoNomeado);

            var resultado = new RunnerArgs();

            if (isSweep)
            {
                resultado.Mode = RunnerMode.Sweep;
                resultado.GlobalFolder = args[1];
                resultado.Package = args[2];
                resultado.MapperGuid = args[3];
                resultado.ExamplesFolder = args[4];
                resultado.OutputFolder = args[5];
            }
            else
            {
                resultado.GlobalFolder = args[0];
                resultado.Package = args[1];
                resultado.MapperGuid = args[2];
                resultado.InputPath = args[3];
                resultado.OutputPath = args[4];

                // LIST só existe na forma posicional — é slot documentado do <mapperGuid|LIST>.
                resultado.Mode = string.Equals(resultado.MapperGuid, "LIST", StringComparison.OrdinalIgnoreCase)
                    ? RunnerMode.List
                    : RunnerMode.Exec;
            }

            return Sucesso(resultado);
        }

        private static RunnerArgsParseResult ParseNomeado(string[] args)
        {
            var resultado = new RunnerArgs { Mode = RunnerMode.Exec };
            var erros = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];

                if (!EhFlag(token))
                {
                    // Token solto: reportar, NUNCA absorver como posicional. Absorver é o defeito.
                    erros.Add("Argumento inesperado (esperava uma flag --nome): " + token);
                    continue;
                }

                string nome = token.Substring(2);
                Func<RunnerArgs, string, string> setter;

                if (!Setters.TryGetValue(nome, out setter))
                {
                    erros.Add("Argumento desconhecido: " + token);

                    // Consome o provável valor da flag desconhecida só para não gerar uma cascata de
                    // "argumento inesperado" que esconderia o erro real. O parse já está condenado.
                    if (i + 1 < args.Length && !EhFlag(args[i + 1]))
                        i++;

                    continue;
                }

                // Valor obrigatório. Se o próximo token é outra flag (ou não existe), é erro explícito —
                // consumir a flag seguinte como valor seria o mesmo deslocamento por outro caminho.
                // Valor VAZIO ("") é legítimo e aceito: a API manda --package "" quando LowCode:Package
                // não está configurado.
                if (i + 1 >= args.Length || EhFlag(args[i + 1]))
                {
                    erros.Add("Valor ausente para " + token);
                    continue;
                }

                string erroDeValor = setter(resultado, args[++i]);
                if (erroDeValor != null)
                    erros.Add(erroDeValor);
            }

            if (string.IsNullOrEmpty(resultado.GlobalFolder))
                erros.Add("--globalFolder é obrigatório na forma nomeada.");
            if (string.IsNullOrEmpty(resultado.InputPath))
                erros.Add("--inputFile é obrigatório na forma nomeada.");
            if (string.IsNullOrEmpty(resultado.OutputPath))
                erros.Add("--outputFile é obrigatório na forma nomeada.");
            if (string.IsNullOrEmpty(resultado.MapperGuid) && string.IsNullOrEmpty(resultado.MapperName))
                erros.Add("Informe --mapperId ou --mapperName.");

            if (erros.Count > 0)
            {
                erros.Add(UsoNomeado);
                return Falha(RunnerExitCodes.InvalidNamedArgument, erros.ToArray());
            }

            return Sucesso(resultado);
        }

        private static RunnerArgsParseResult Sucesso(RunnerArgs args)
        {
            return new RunnerArgsParseResult
            {
                Success = true,
                ExitCode = RunnerExitCodes.Ok,
                Args = args,
                Messages = new string[0]
            };
        }

        private static RunnerArgsParseResult Falha(int exitCode, params string[] mensagens)
        {
            return new RunnerArgsParseResult
            {
                Success = false,
                ExitCode = exitCode,
                Args = null,
                Messages = mensagens
            };
        }
    }
}
