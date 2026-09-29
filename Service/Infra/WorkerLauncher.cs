using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LayoutParserLowCodeRunner.Service.Infra
{
    internal sealed class WorkerResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public string Stdout { get; set; } = "";
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Executa o worker (o próprio exe na forma nomeada/posicional — o MESMO protocolo que a API usava com
    /// Process.Start, o que garante paridade) em processo filho. Timeout e cancelamento matam a ÁRVORE do
    /// processo: um processo pendurado consumiria um slot até morrer sozinho. Isolamento: estado estático e
    /// thread-safety desconhecidos das DLLs Sysmiddle ficam confinados ao filho; crash não derruba o serviço.
    /// </summary>
    internal sealed class WorkerLauncher
    {
        private readonly string _exePath;
        private readonly ConcurrentDictionary<int, Process> _live = new ConcurrentDictionary<int, Process>();

        public WorkerLauncher(string exePath)
        {
            _exePath = exePath;
        }

        public string ExePath { get { return _exePath; } }
        public int LiveCount { get { return _live.Count; } }

        public async Task<WorkerResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // A Bin do Sysmiddle resolve as DLLs pelo app base do exe; o cwd acompanha por segurança.
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            proc.Exited += (_, __) => exited.TrySetResult(true);

            if (!proc.Start())
                throw new InvalidOperationException("Nao foi possivel iniciar o worker.");

            _live[proc.Id] = proc;
            var pid = proc.Id;
            try
            {
                // Drenar stdout/stderr SEMPRE: buffer cheio bloquearia o filho para sempre.
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();

                var result = new WorkerResult();
                using (var timeoutCts = new CancellationTokenSource(timeout))
                using (timeoutCts.Token.Register(() => exited.TrySetResult(false)))
                using (ct.Register(() => exited.TrySetResult(false)))
                {
                    if (!await exited.Task.ConfigureAwait(false))
                    {
                        result.Cancelled = ct.IsCancellationRequested;
                        result.TimedOut = !result.Cancelled;
                        KillTree(proc);
                        try { proc.WaitForExit(5000); } catch { }
                    }
                }

                try { await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(5000)).ConfigureAwait(false); } catch { }
                result.Stdout = outTask.IsCompleted && !outTask.IsFaulted ? outTask.Result : "";
                result.ExitCode = SafeExitCode(proc);
                result.DurationMs = sw.ElapsedMilliseconds;
                return result;
            }
            finally
            {
                Process removed;
                _live.TryRemove(pid, out removed);
                try { proc.Dispose(); } catch { }
            }
        }

        /// <summary>Shutdown: mata todo worker ainda vivo.</summary>
        public void KillAll()
        {
            foreach (var kv in _live)
                KillTree(kv.Value);
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.HasExited ? p.ExitCode : -1; } catch { return -1; }
        }

        private static void KillTree(Process p)
        {
            int pid;
            try { pid = p.Id; if (p.HasExited) return; } catch { return; }

            // net481 não tem Kill(entireProcessTree): taskkill /T cobre filhos do worker.
            try
            {
                using (var tk = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (tk != null) tk.WaitForExit(5000);
                }
            }
            catch { }

            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// <summary>Quoting de argumento no padrão CommandLineToArgvW (o mesmo que o Main do worker recebe).</summary>
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        public static string BuildArguments(params string[] args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }
    }
}
