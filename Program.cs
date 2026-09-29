using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using LayoutParserLowCodeRunner.Runner;
using LayoutParserLowCodeRunner.Service;

namespace LayoutParserLowCodeRunner
{
    internal static class Program
    {
        // Modos:
        // - --service (binPath do serviço Windows): API HTTP, config em LayoutParserLowCodeRunner.exe.config
        // - --console: mesma API HTTP em primeiro plano (Ctrl+C encerra)
        // - CLI legado (qualquer outro caso):
        //   LayoutParserLowCodeRunner.exe --sysmiddleDir "C:\...\Sysmiddle" --globalFolder "C:\...\global" --package "XYZ" --mapperName "..." --mapperId "..." --inputFile "..." --outputFile "..." --fileName "..."
        //   - mapperId tem prioridade sobre mapperName; exit code conforme RunnerExitCode
        public static int Main(string[] args)
        {
            if (Array.Exists(args, x => string.Equals(x, "--console", StringComparison.OrdinalIgnoreCase)))
                return RunConsole();

            // Flag explícita: o CLI legado também roda sem sessão interativa (ex.: Process.Start a partir do IIS)
            if (Array.Exists(args, x => string.Equals(x, "--service", StringComparison.OrdinalIgnoreCase)))
            {
                ServiceBase.Run(new RunnerService());
                return 0;
            }

            return RunCli(args);
        }

        private static int RunConsole()
        {
            using (var host = new HttpHost(SidecarConfig.Load()))
            using (var quit = new ManualResetEventSlim())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
                host.Start();
                Console.WriteLine("LayoutParserLowCodeRunner HTTP ativo. Ctrl+C para encerrar.");
                quit.Wait();
            }
            return 0;
        }

        private static int RunCli(string[] args)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            try
            {
                var a = ParseArgs(args);
                correlationId = Get(a, "--correlationId") ?? correlationId;

                var inputFile = Require(a, "--inputFile");
                var outputFile = Require(a, "--outputFile");
                var runnerLogFile = Get(a, "--runnerLogFile");
                var log = string.IsNullOrWhiteSpace(runnerLogFile) ? RunnerLog.Fallback(correlationId) : new RunnerLog(runnerLogFile);

                var engine = new TransformEngine(new EngineOptions
                {
                    SysmiddleDir = Require(a, "--sysmiddleDir"),
                    GlobalFolder = Require(a, "--globalFolder"),
                    Package = Get(a, "--package") ?? ""
                }, log);

                var fileName = Get(a, "--fileName") ?? Path.GetFileName(inputFile);
                log.Info(correlationId, $"START inputFile='{inputFile}' outputFile='{outputFile}' fileName='{fileName}'");

                var content = File.ReadAllText(inputFile, Encoding.UTF8);
                var transformed = engine.Transform(Get(a, "--mapperId"), Get(a, "--mapperName"), content, fileName, correlationId);

                File.WriteAllText(outputFile, transformed, Encoding.UTF8);
                log.Info(correlationId, $"Output escrito: {transformed.Length} chars");
                log.Info(correlationId, "END success");
                return (int)RunnerExitCode.Success;
            }
            catch (Exception ex)
            {
                // Nunca escrever em console: o chamador só olha o exit code. Persistir o erro em arquivo.
                var code = ex is RunnerException rex ? rex.Code
                         : ex is ArgumentException ? RunnerExitCode.InvalidArguments
                         : RunnerExitCode.Unexpected;
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "layoutparser-lowcode", "runner-logs");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(
                        Path.Combine(dir, $"runner_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{correlationId}.log"),
                        $"{DateTime.UtcNow:O} [ERR] [Corr:{correlationId}] FATAL ({code}) {ex}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch { }
                return (int)code;
            }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--")) continue;
                var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                d[key] = value;
            }
            return d;
        }

        private static string Require(Dictionary<string, string> args, string key)
        {
            var v = Get(args, key);
            if (string.IsNullOrWhiteSpace(v))
                throw new ArgumentException($"Argumento obrigatório ausente: {key}");
            return v;
        }

        private static string Get(Dictionary<string, string> args, string key)
            => args.TryGetValue(key, out var v) ? v : null;
    }
}
