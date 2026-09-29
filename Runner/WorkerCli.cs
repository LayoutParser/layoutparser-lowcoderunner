#if SYSMIDDLE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SysMiddle.Base.Model.API;      // MapperBasicVO

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// CLI que executa um mapeador do SDK Sysmiddle (gera o XML gabarito) ou LISTa os mapeadores do
    /// package.
    ///
    /// <para><b>2026-08-10 — o runner deixou de depender do <c>appConnector</c>.</b> Antes ele
    /// replicava o <c>Service1.OnStart</c> do host FiatMQ
    /// (<c>new EDocsClientConnectorManager().Start()</c>) só para popular
    /// <c>ConnectorApplicationManager._configuration</c> e daí ler <c>GetServerPackage()</c> — que
    /// devolve <b>uma string</b>: o identificador do projeto Sysmiddle. Essa string agora chega por
    /// <c>--package</c> (<c>LowCode:Package</c>), e com ela saem o bootstrap, as
    /// <c>appConnector.Client.Core*</c> e o custo/instabilidade que vinham junto (init do host,
    /// threads de transporte, e um <c>TH_FAI</c> que podia derrubar o processo com
    /// <c>ArgumentNullException</c> antes mesmo de transformar).</para>
    ///
    /// <para>O que fica do SDK: <c>SysMiddle.Base</c> (APIManager/APIExecutor) e
    /// <c>SysMiddle.ConnectUs.Core</c> (LicenseController concreto). O gate de licença — as DUAS
    /// partes — mora em <see cref="SysmiddleRuntime.Create"/>.</para>
    ///
    /// <para>Uso (single-shot): LayoutParserLowCodeRunner &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid|LIST&gt; &lt;input&gt; &lt;output&gt;</para>
    /// <para>Uso (lote/A1):      LayoutParserLowCodeRunner SWEEP &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid&gt; &lt;pastaExamples&gt; &lt;pastaSaida&gt;</para>
    /// <para>Uso (nomeado):      é a forma que a API fala (LowCodeTransformationService) —
    ///   --globalFolder &lt;dir&gt; --package &lt;pkg&gt; --inputFile &lt;arq&gt; --outputFile &lt;arq&gt;
    ///   (--mapperId &lt;guid&gt; | --mapperName &lt;nome&gt;) [--fileName &lt;nome&gt;] [--correlationId &lt;id&gt;]
    ///   [--runnerLogFile &lt;arq&gt;] [--sysmiddleDir &lt;dir&gt;] [--nfePostProcessing true|false]</para>
    /// <para>(rode DE DENTRO da Bin da instância; globalFolder = pasta com o global.config de paths locais).</para>
    /// </summary>
    internal static class WorkerCli
    {
        internal static int Execute(string[] args)
        {
            var parse = RunnerArgsParser.Parse(args);
            if (!parse.Success)
            {
                // Antes do Configure: sem correlationId ainda, mas o formato de linha já é o mesmo.
                foreach (var mensagem in parse.Messages)
                    RunnerLog.Error("{0}", mensagem);

                Console.Error.Flush();
                return parse.ExitCode;
            }

            var cli = parse.Args;

            // Liga o log ao contexto ANTES de qualquer trabalho: falha de licença/config também
            // precisa sair correlacionada, senão o log da API não fecha com o do runner.
            RunnerLog.Configure(cli.CorrelationId, cli.RunnerLogFile);

            // O package virou obrigatório quando o fallback do host (GetServerPackage) saiu. Vazio é
            // erro EXPLÍCITO com exit code próprio — nunca silêncio que degrada em "mapeador não
            // encontrado" lá na frente. Ver RunnerArgs.ValidarPackage.
            var erroPackage = cli.ValidarPackage();
            if (erroPackage != null)
            {
                RunnerLog.Fatal("{0}", erroPackage);
                Console.Error.Flush();
                return RunnerExitCodes.PackageNotConfigured;
            }

            int exitCode;
            try
            {
                exitCode = cli.Mode == RunnerMode.Sweep ? Sweep(cli) : Run(cli);
            }
            catch (SysmiddlePackageNotFoundException ex)
            {
                RunnerLog.Fatal("{0}", ex.Message);
                exitCode = RunnerExitCodes.PackageNotFound;
            }
            catch (Exception ex)
            {
                RunnerLog.Fatal("{0}", ex);
                exitCode = RunnerExitCodes.Fatal;
            }

            Console.Out.Flush();
            Console.Error.Flush();

            // ── Environment.Exit: já NÃO é necessário, e fica só como cinto de segurança ──
            // Antes ele era obrigatório: o bootstrap do host subia ThreadManagers de transporte e
            // uma thread "TH_FAI" de PRIMEIRO PLANO (new Thread(...) sem IsBackground), então o
            // processo nunca terminava sozinho.
            //
            // MEDIDO em 2026-08-10, com uma build idêntica a esta menos esta linha: o processo
            // retorna do Main e ENCERRA sozinho (exit=0, 75,7s, saída de 4246 chars intacta). O que
            // o SDK deixa vivo — o FileSystemWatcher do APIManager sobre o exportContext.data
            // (LoadExportContextWatcher) e a eventual Task de licença temporária do APIExecutor —
            // não segura o processo.
            //
            // Consequência prática: hospedar SysmiddleRuntime + SysmiddleMapperExecutor IN-PROCESS
            // (projeto de teste x86) deixou de ter o risco de travar o host de teste. Mantemos o
            // Exit aqui mesmo assim porque é grátis e blinda o CLI contra uma thread de primeiro
            // plano futura — a API mata o runner por timeout, e um processo pendurado consome um
            // slot de LowCode:MaxConcurrentRunners até lá.
            Environment.Exit(exitCode);
            return exitCode; // inalcançável
        }

        /// <summary>
        /// Sobe o SDK (gate de licença + APIExecutor do package) e devolve o executor já configurado.
        /// Pré-requisito comum ao modo single-shot (EXEC/LIST) e ao SWEEP.
        /// </summary>
        private static SysmiddleMapperExecutor CriarExecutor(RunnerArgs cli)
        {
            var apiExecutor = SysmiddleRuntime.Create(cli.GlobalFolder, cli.Package);
            return new SysmiddleMapperExecutor(apiExecutor, cli.NfePostProcessing);
        }

        private static int Run(RunnerArgs cli)
        {
            var executor = CriarExecutor(cli);

            // ── LIST: imprime os mapeadores do package (descoberta) ──
            if (cli.Mode == RunnerMode.List)
            {
                RunnerLog.Info("[LIST] globalFolder={0} package='{1}'", cli.GlobalFolder, cli.Package);
                var mappers = executor.GetMappers();
                RunnerLog.Info("Mapeadores encontrados: {0}", mappers != null ? mappers.Count : 0);
                if (mappers != null)
                    foreach (var kv in mappers)
                        Console.Out.WriteLine("{0}\t{1}", kv.Value.IdentifierGuid, kv.Value.Name);
                return RunnerExitCodes.Ok;
            }

            // ── Resolução do mapeador ── a API aceita mapperId OU mapperName
            // (TransformationExecutionController), então o runner precisa fechar os dois lados do
            // contrato. Ambos passam pelo APIExecutor (item 2 da decisão) — nada de MappersHelper.
            var mapper = ResolverMapper(executor, cli);
            if (mapper == null)
                return RunnerExitCodes.MapperNameUnresolved;

            // ── EXEC: executa um mapeador sobre o input e grava o XML gabarito ──
            if (!File.Exists(cli.InputPath))
            {
                RunnerLog.Error("Input nao encontrado: {0}", cli.InputPath);
                return RunnerExitCodes.InputNotFound;
            }
            string document = File.ReadAllText(cli.InputPath);

            // ✅ --fileName é o nome LÓGICO do documento. A API grava a entrada num temporário
            // (in_<guid>.txt), então derivar de Path.GetFileName(inputPath) perdia o nome real do
            // arquivo do usuário — e esse nome pode influenciar a seleção de parser no Sysmiddle.
            // A regra (incluindo o fallback) vive em RunnerArgs porque lá ela é testável.
            string documentName = cli.ResolveDocumentName();

            RunnerLog.Info("[EXEC] globalFolder={0} package={1} mapper={2} input={3} fileName={4} nfePostProcessing={5}",
                cli.GlobalFolder, cli.Package, mapper.IdentifierGuid, Path.GetFileName(cli.InputPath),
                documentName, cli.NfePostProcessing);

            string result = executor.ExecuteMappingDocumentById(mapper, document, documentName);

            File.WriteAllText(cli.OutputPath, result ?? string.Empty, new UTF8Encoding(false));
            int len = (result ?? string.Empty).Length;
            RunnerLog.Info("[OK] {0} chars -> {1}", len, cli.OutputPath);
            if (len == 0)
            {
                RunnerLog.Warn("[WARN] Mapeador retornou vazio (mapperGuid errado, licenca invalida ou parser sem match).");
                return RunnerExitCodes.EmptyResult;
            }
            return RunnerExitCodes.Ok;
        }

        /// <summary>
        /// Traduz <c>--mapperId</c>/<c>--mapperName</c> no <c>MapperBasicVO</c>. Devolve null (e loga
        /// o motivo) quando não há match — o chamador converte isso em
        /// <see cref="RunnerExitCodes.MapperNameUnresolved"/>, para o log da API distinguir
        /// "identificador errado" de "mapeador rodou e voltou vazio".
        /// </summary>
        private static MapperBasicVO ResolverMapper(SysmiddleMapperExecutor executor, RunnerArgs cli)
        {
            if (!string.IsNullOrEmpty(cli.MapperGuid))
            {
                var porId = executor.GetMapperById(cli.MapperGuid);
                if (porId == null)
                    RunnerLog.Error("--mapperId '{0}' nao encontrado no package '{1}'.", cli.MapperGuid, cli.Package);

                return porId;
            }

            var porNome = executor.GetMapperByName(cli.MapperName);
            if (porNome == null)
                RunnerLog.Error("--mapperName '{0}' nao encontrado no package '{1}'.", cli.MapperName, cli.Package);
            else
                RunnerLog.Info("[MAPPER] --mapperName '{0}' resolvido para {1}.", cli.MapperName, porNome.IdentifierGuid);

            return porNome;
        }

        /// <summary>
        /// Modo SWEEP (A1): varre recursivamente a pasta de exemplos (ex.: Examples/LAY_CNHI_...),
        /// executa o mapeador informado para CADA arquivo-exemplo encontrado e grava o par input→XML
        /// resultante na pasta de saída (numerado 0001.xml, 0002.xml, ... — convenção já usada em
        /// .claude/tmp/gabaritos/fiat-sweep). Falha em UM arquivo não aborta a varredura: logamos e
        /// seguimos (degradação graciosa — princípio central do projeto, ver
        /// .claude/rules/dotnet-standards.md).
        /// </summary>
        private static int Sweep(RunnerArgs cli)
        {
            if (!Directory.Exists(cli.ExamplesFolder))
            {
                RunnerLog.Error("[SWEEP] Pasta de exemplos nao encontrada: {0}", cli.ExamplesFolder);
                return RunnerExitCodes.InputNotFound;
            }

            Directory.CreateDirectory(cli.OutputFolder);
            var executor = CriarExecutor(cli);

            var mapper = ResolverMapper(executor, cli);
            if (mapper == null)
                return RunnerExitCodes.MapperNameUnresolved;

            // Ignora artefatos que não são documentos de entrada (ex.: layout_learned.json, ocultos, o próprio manifesto).
            var files = Directory.EnumerateFiles(cli.ExamplesFolder, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RunnerLog.Info("[SWEEP] globalFolder={0} mapper={1} examples={2} ({3} arquivo(s)) -> {4}",
                cli.GlobalFolder, mapper.IdentifierGuid, cli.ExamplesFolder, files.Count, cli.OutputFolder);

            var manifestPath = Path.Combine(cli.OutputFolder, "_manifest.tsv");
            var manifestLines = new List<string> { "seq\tinput\tstatus\tout_chars\toutput" };

            int ok = 0, empty = 0, fail = 0, seq = 0;
            foreach (var inputPath in files)
            {
                seq++;
                string seqName = seq.ToString("D4", CultureInfo.InvariantCulture) + ".xml";
                string outputPath = Path.Combine(cli.OutputFolder, seqName);
                string relInput = MakeRelativePath(cli.ExamplesFolder, inputPath);

                try
                {
                    string document = File.ReadAllText(inputPath);
                    string result = executor.ExecuteMappingDocumentById(mapper, document, Path.GetFileName(inputPath));
                    string safeResult = result ?? string.Empty;

                    File.WriteAllText(outputPath, safeResult, new UTF8Encoding(false));
                    int len = safeResult.Length;

                    if (len == 0)
                    {
                        empty++;
                        RunnerLog.Warn("[SWEEP-WARN] {0}: mapeador retornou vazio.", relInput);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "EMPTY", "0", seqName));
                    }
                    else
                    {
                        ok++;
                        RunnerLog.Info("[SWEEP-OK] {0} -> {1} ({2} chars)", relInput, seqName, len);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "OK", len.ToString(CultureInfo.InvariantCulture), seqName));
                    }
                }
                catch (Exception ex)
                {
                    // Degrade gracioso: um arquivo ruim (encoding invalido, parser sem match, etc.) nao pode
                    // interromper a varredura dos demais 169 restantes.
                    fail++;
                    RunnerLog.Error("[SWEEP-FAIL] {0}: {1}", relInput, ex.Message);
                    manifestLines.Add(string.Join("\t", seqName, relInput, "FAIL", "0", string.Empty));
                }
            }

            File.WriteAllLines(manifestPath, manifestLines, new UTF8Encoding(false));
            RunnerLog.Info("[SWEEP] Concluido: {0} ok, {1} vazios, {2} falhas (de {3}). Manifesto: {4}",
                ok, empty, fail, files.Count, manifestPath);

            // Exit code != 0 somente se NADA saiu com sucesso (sinaliza problema sistemico, ex.: mapperGuid errado).
            return ok > 0 ? RunnerExitCodes.Ok : RunnerExitCodes.SweepAllFailed;
        }

        /// <summary>
        /// net481 nao tem Path.GetRelativePath (introduzido no .NET Core 2.0) — versao minima so para exibir
        /// o caminho do input no log/manifesto de forma legivel.
        /// </summary>
        private static string MakeRelativePath(string baseFolder, string fullPath)
        {
            var baseUri = new Uri(Path.GetFullPath(baseFolder) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
    }
}

#endif
