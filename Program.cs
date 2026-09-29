using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using LayoutParserLowCodeRunner.Service;

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Ponto de entrada único. Modos:
    /// <list type="bullet">
    ///   <item><description><c>--service</c>: serviço Windows (HTTP + supervisor de workers). É o binPath do serviço.</description></item>
    ///   <item><description><c>--console</c>: o mesmo host em primeiro plano (dev; Ctrl+C encerra).</description></item>
    ///   <item><description>qualquer outro: WORKER / CLI legado (formas posicional e nomeada, incl. LIST e SWEEP),
    ///   idêntico ao runner que a API chamava por Process.Start. <c>--worker</c> como primeiro token é aceito e
    ///   removido (marca explícita de "modo worker").</description></item>
    /// </list>
    /// A flag do modo serviço é explícita: o CLI também roda sem sessão interativa (ex.: pelo IIS).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && Is(args[0], "--service"))
            {
                ServiceBase.Run(new RunnerService());
                return 0;
            }

            if (args.Length > 0 && Is(args[0], "--console"))
                return RunConsole();

            if (args.Length > 0 && Is(args[0], "--worker"))
                args = args.Skip(1).ToArray();

            return WorkerCli.Execute(args);
        }

        private static int RunConsole()
        {
            var host = new ServiceHost(RunnerService.LoadOptions(), echoToConsole: true);
            using (var quit = new ManualResetEventSlim())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
                host.Start();
                Console.WriteLine("LayoutParserLowCodeRunner ativo em " + host.Options.ListenPrefix + " (Ctrl+C encerra).");
                quit.Wait();
            }
            host.StopAsync().GetAwaiter().GetResult();
            return 0;
        }

        private static bool Is(string a, string flag)
        {
            return string.Equals(a, flag, StringComparison.OrdinalIgnoreCase);
        }
    }
}
