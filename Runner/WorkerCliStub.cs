#if !SYSMIDDLE
using System;

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Worker SEM o SDK Sysmiddle (build de CI, sem as DLLs proprietárias). Valida os argumentos como o
    /// worker real e sai com <see cref="RunnerExitCodes.BootstrapFailed"/>: o serviço HTTP continua
    /// testável, mas nenhuma transformação real acontece. O build real define SYSMIDDLE_LIBS_DIR
    /// (ver LayoutParserLowCodeRunner.csproj).
    /// </summary>
    internal static class WorkerCli
    {
        internal static int Execute(string[] args)
        {
            var parse = RunnerArgsParser.Parse(args);
            if (!parse.Success)
            {
                foreach (var mensagem in parse.Messages)
                    RunnerLog.Error("{0}", mensagem);
                return parse.ExitCode;
            }

            RunnerLog.Configure(parse.Args.CorrelationId, parse.Args.RunnerLogFile);
            RunnerLog.Fatal("Build sem o SDK Sysmiddle (SYSMIDDLE_LIBS_DIR nao definido): worker indisponivel.");
            return RunnerExitCodes.BootstrapFailed;
        }
    }
}
#endif
