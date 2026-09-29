using System;
using System.IO;
using System.Text;

namespace LayoutParserLowCodeRunner.Runner
{
    internal sealed class RunnerLog
    {
        private static readonly object Gate = new object();
        private readonly string _file;

        public RunnerLog(string file) { _file = file; }

        public static RunnerLog Fallback(string correlationId)
        {
            var dir = Path.Combine(Path.GetTempPath(), "layoutparser-lowcode", "runner-logs");
            return new RunnerLog(Path.Combine(dir, $"runner_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{correlationId}.log"));
        }

        public void Info(string correlationId, string msg) => Write("INF", correlationId, msg);
        public void Warn(string correlationId, string msg) => Write("WRN", correlationId, msg);
        public void Error(string correlationId, string msg) => Write("ERR", correlationId, msg);

        private void Write(string level, string correlationId, string msg)
        {
            var line = $"{DateTime.UtcNow:O} [{level}] [Corr:{correlationId}] {msg}";
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    RollIfNeeded(_file, 2049L * 1024L, 10);
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static void RollIfNeeded(string basePath, long maxBytes, int maxFiles)
        {
            try
            {
                var fi = new FileInfo(basePath);
                if (!fi.Exists) return;
                if (fi.Length < maxBytes) return;

                var dir = fi.DirectoryName ?? ".";
                var baseName = Path.GetFileNameWithoutExtension(basePath);
                var ext = Path.GetExtension(basePath);
                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fff");
                var rolled = Path.Combine(dir, $"{baseName}-{stamp}{ext}");
                File.Move(basePath, rolled);

                var files = new DirectoryInfo(dir).GetFiles($"{baseName}-*{ext}");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = maxFiles - 1; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
