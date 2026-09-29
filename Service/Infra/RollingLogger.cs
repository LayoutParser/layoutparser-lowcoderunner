using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LayoutParserLowCodeRunner.Service.Infra
{
    /// <summary>
    /// Log do serviço com rotação por tamanho. Mesmo formato do RunnerLog
    /// (<c>{O} [LVL] [Corr:xxx] msg</c>). Regra de conteúdo: só tamanhos, ids, tempos e exit codes —
    /// NUNCA documento, XML de saída, caminhos internos ou credenciais. Escrita best effort.
    /// </summary>
    internal sealed class RollingLogger
    {
        private const long MaxBytes = 10L * 1024 * 1024;
        private const int MaxFiles = 10;
        private readonly object _gate = new object();
        private readonly string _file;

        public bool EchoToConsole { get; set; }

        public RollingLogger(string logDir, string fileName = "service.log")
        {
            _file = string.IsNullOrWhiteSpace(logDir) ? null : Path.Combine(logDir, fileName);
        }

        public void Info(string corr, string msg) { Write("INF", corr, msg); }
        public void Warn(string corr, string msg) { Write("WRN", corr, msg); }
        public void Error(string corr, string msg) { Write("ERR", corr, msg); }

        private void Write(string level, string corr, string msg)
        {
            var line = string.Format(CultureInfo.InvariantCulture, "{0:O} [{1}] [Corr:{2}] {3}",
                DateTime.Now, level, string.IsNullOrEmpty(corr) ? "-" : corr, msg);

            if (EchoToConsole)
            {
                try { Console.WriteLine(line); } catch { }
            }

            if (_file == null) return;
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file));
                    Roll();
                    File.AppendAllText(_file, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private void Roll()
        {
            try
            {
                var fi = new FileInfo(_file);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                var dir = fi.DirectoryName ?? ".";
                var name = Path.GetFileNameWithoutExtension(_file);
                var ext = Path.GetExtension(_file);
                File.Move(_file, Path.Combine(dir, name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ext));

                var old = new DirectoryInfo(dir).GetFiles(name + "-*" + ext);
                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = MaxFiles - 1; i < old.Length; i++)
                {
                    try { old[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
