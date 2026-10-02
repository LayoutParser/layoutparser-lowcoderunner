using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service.Application
{
    internal sealed class ExecOutcome
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        /// <summary>Cliente desistiu ou o orçamento do batch estourou.</summary>
        public bool Cancelled { get; set; }
        /// <summary>false = cancelado ainda na fila (nenhum worker chegou a subir).</summary>
        public bool Started { get; set; }
        public string Output { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>Mapeamento exit code do worker -> HTTP (contrato v1) e mensagens SEGURAS (sem caminho/stack).</summary>
    internal static class ExitMap
    {
        public static int Status(int exitCode)
        {
            switch (exitCode)
            {
                case RunnerExitCodes.Ok: return 200;
                case RunnerExitCodes.InvalidNamedArgument: return 400;
                case RunnerExitCodes.MapperNameUnresolved: return 404;
                case RunnerExitCodes.Fatal:
                case RunnerExitCodes.InputNotFound:
                case RunnerExitCodes.EmptyResult:
                case RunnerExitCodes.PackageNotConfigured:
                case RunnerExitCodes.PackageNotFound: return 422;
                case RunnerExitCodes.BootstrapFailed: return 503;
                default: return 500;
            }
        }

        public static string Message(int exitCode)
        {
            switch (exitCode)
            {
                case RunnerExitCodes.Fatal: return "Falha na transformacao.";
                case RunnerExitCodes.BootstrapFailed: return "Runner indisponivel (bootstrap/licenca do Sysmiddle).";
                case RunnerExitCodes.InputNotFound: return "Entrada nao encontrada pelo runner.";
                case RunnerExitCodes.EmptyResult: return "O mapeador retornou resultado vazio.";
                case RunnerExitCodes.InvalidNamedArgument: return "Requisicao invalida para o runner.";
                case RunnerExitCodes.MapperNameUnresolved: return "Mapeador nao encontrado no package.";
                case RunnerExitCodes.PackageNotConfigured: return "Package nao configurado no servico.";
                case RunnerExitCodes.PackageNotFound: return "Package nao encontrado ou licenca nao validada.";
                default: return "Erro interno do runner.";
            }
        }

        public static ServiceException ToException(ExecOutcome o)
        {
            if (o.TimedOut)
                return new ServiceException(504, -1, ErrorCodes.Timeout, "Tempo limite de execucao excedido.");
            if (o.Cancelled)
                return new ServiceException(499, -1, ErrorCodes.ClientClosedRequest, "Requisicao cancelada.");
            return new ServiceException(Status(o.ExitCode), o.ExitCode, ErrorCodes.ForExit(o.ExitCode), Message(o.ExitCode));
        }
    }

    /// <summary>
    /// Núcleo de execução: grava a entrada numa pasta temporária do serviço (removida no finally), sobe o
    /// worker com o mesmo protocolo nomeado que a API usava, respeita slots/fila/timeout/cancelamento e lê a
    /// saída. Nunca loga documento, XML de saída ou caminhos — só ids, tamanhos, tempos e exit codes.
    /// </summary>
    internal sealed class LowCodeExecutor
    {
        private readonly ServiceOptions _opt;
        private readonly WorkerLauncher _launcher;
        private readonly ExecutionGate _gate;
        private readonly RollingLogger _log;

        public LowCodeExecutor(ServiceOptions opt, WorkerLauncher launcher, ExecutionGate gate, RollingLogger log)
        {
            _opt = opt;
            _launcher = launcher;
            _gate = gate;
            _log = log;
        }

        public string RunnerLogFile { get { return Path.Combine(_opt.LogDir, "runner.log"); } }

        /// <summary>Config do SERVICO faltando (GlobalFolder) e erro 503, nao do cliente: sem isto o worker sairia com 7 e viraria um 400 enganoso.</summary>
        public void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(_opt.GlobalFolder))
                throw new ServiceException(503, RunnerExitCodes.BootstrapFailed, ErrorCodes.RunnerUnavailable, "Servico nao configurado (GlobalFolder).");
        }

        public async Task<ExecOutcome> ExecuteAsync(
            string document, string fileName, string mapperId, string mapperName, bool nfePostProcessing,
            string corr, bool enforceQueueLimit, CancellationToken ct)
        {
            IDisposable ticket;
            try
            {
                ticket = await _gate.AcquireAsync(ct, enforceQueueLimit).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ExecOutcome { Cancelled = true, Started = false };
            }

            using (ticket)
            {
                var dir = Path.Combine(_opt.WorkerTempDir, Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    var inFile = Path.Combine(dir, "in.txt");
                    var outFile = Path.Combine(dir, "out.xml");
                    File.WriteAllText(inFile, document, new UTF8Encoding(false));

                    var byId = !string.IsNullOrWhiteSpace(mapperId);
                    var args = WorkerLauncher.BuildArguments(
                        "--sysmiddleDir", _opt.SysmiddleDir,
                        "--globalFolder", _opt.GlobalFolder,
                        "--package", _opt.Package,
                        "--inputFile", inFile,
                        "--outputFile", outFile,
                        "--fileName", string.IsNullOrWhiteSpace(fileName) ? "documento.txt" : fileName,
                        "--correlationId", corr,
                        "--runnerLogFile", RunnerLogFile,
                        byId ? "--mapperId" : "--mapperName", byId ? mapperId : mapperName,
                        "--nfePostProcessing", nfePostProcessing ? "true" : "false");

                    _log.Info(corr, string.Format("worker start mapper={0} docChars={1}",
                        byId ? mapperId : "name:" + mapperName, document.Length));

                    var r = await _launcher.RunAsync(args, TimeSpan.FromSeconds(_opt.RunnerTimeoutSeconds), ct).ConfigureAwait(false);

                    var outcome = new ExecOutcome
                    {
                        Started = true,
                        ExitCode = r.ExitCode,
                        TimedOut = r.TimedOut,
                        Cancelled = r.Cancelled,
                        DurationMs = r.DurationMs
                    };

                    if (!r.TimedOut && !r.Cancelled && r.ExitCode == RunnerExitCodes.Ok)
                    {
                        if (File.Exists(outFile))
                            outcome.Output = File.ReadAllText(outFile, Encoding.UTF8);
                        else
                            outcome.ExitCode = RunnerExitCodes.Fatal; // exit 0 sem arquivo: contrato violado
                    }

                    _log.Info(corr, string.Format("worker end exit={0} timedOut={1} cancelled={2} ms={3} outChars={4}",
                        outcome.ExitCode, r.TimedOut, r.Cancelled, r.DurationMs, outcome.Output == null ? 0 : outcome.Output.Length));
                    return outcome;
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        /// <summary>Modo LIST (forma posicional): stdout = "guid\tnome" por linha.</summary>
        public async Task<WorkerResult> ListAsync(string corr, CancellationToken ct)
        {
            using (await _gate.AcquireAsync(ct, true).ConfigureAwait(false))
            {
                var dir = Path.Combine(_opt.WorkerTempDir, Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(dir);
                    // Posicional: <globalFolder> <package> LIST <input> <output> (input/output não são usados no LIST).
                    var args = WorkerLauncher.BuildArguments(
                        _opt.GlobalFolder, _opt.Package, "LIST", Path.Combine(dir, "in.txt"), Path.Combine(dir, "out.xml"));
                    _log.Info(corr, "worker LIST start");
                    var r = await _launcher.RunAsync(args, TimeSpan.FromSeconds(_opt.RunnerTimeoutSeconds), ct).ConfigureAwait(false);
                    _log.Info(corr, string.Format("worker LIST end exit={0} timedOut={1} ms={2}", r.ExitCode, r.TimedOut, r.DurationMs));
                    return r;
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }
    }
}
