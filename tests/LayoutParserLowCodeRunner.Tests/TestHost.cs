using System.Diagnostics;
using System.Net.Http;
using System.Text;
using LayoutParserLowCodeRunner.Service;
using LayoutParserLowCodeRunner.Service.Application;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Tests
{
    /// <summary>Sobe o serviço real (porta efêmera) com o worker falso; apaga tudo no Dispose.</summary>
    internal sealed class TestHost : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lpr-tests-" + Guid.NewGuid().ToString("N"));
        public string TrackFile => Path.Combine(Root, "track.txt");
        public string LogDir => Path.Combine(Root, "logs");
        public ServiceHost Host { get; }
        public HttpClient Http { get; }

        public TestHost(Dictionary<string, string> overrides = null)
        {
            Directory.CreateDirectory(Root);
            var global = Path.Combine(Root, "global");
            Directory.CreateDirectory(global);
            File.WriteAllText(Path.Combine(global, "global.config"), "<configuration/>");

            var cfg = new Dictionary<string, string>
            {
                ["ListenPrefix"] = "http://127.0.0.1:0/",
                ["WorkerExePath"] = Path.Combine(AppContext.BaseDirectory, "fake", "FakeLowCodeRunner.exe"),
                ["GlobalFolder"] = global,
                ["SysmiddleDir"] = Path.Combine(Root, "sysmiddle"),
                ["Package"] = "PKG",
                ["LogDir"] = LogDir,
                ["WorkerTempDir"] = Path.Combine(Root, "work"),
                ["RunnerTimeoutSeconds"] = "20",
                ["GracefulShutdownSeconds"] = "1"
            };
            if (overrides != null)
                foreach (var kv in overrides) cfg[kv.Key] = kv.Value;

            Environment.SetEnvironmentVariable("FAKE_RUNNER_TRACK", TrackFile);
            Host = new ServiceHost(ServiceOptions.Load(k => cfg.TryGetValue(k, out var v) ? v : null));
            Host.Start();
            Http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + Host.Port + "/"), Timeout = TimeSpan.FromSeconds(60) };
        }

        public Task<HttpResponseMessage> PostJson(string path, byte[] body, CancellationToken ct = default)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Http.PostAsync(path, content, ct);
        }

        public Task<HttpResponseMessage> Transform(string document, string mapperName = "M1", string mapperId = null,
            string fileName = "doc.txt", bool? nfe = null, CancellationToken ct = default)
            => PostJson("v1/transform", Json.Serialize(new TransformRequest
            { Document = document, FileName = fileName, MapperName = mapperName, MapperId = mapperId, NfePostProcessing = nfe }), ct);

        public Task<HttpResponseMessage> Batch(string document, string[] mapperNames, int? budgetSeconds = null)
            => PostJson("v1/transform/batch", Json.Serialize(new BatchRequest
            {
                Document = document,
                FileName = "doc.txt",
                Candidates = mapperNames.Select(n => new CandidateRef { MapperName = n }).ToList(),
                BudgetSeconds = budgetSeconds
            }));

        public static async Task<T> Read<T>(HttpResponseMessage r) where T : class
            => Json.Deserialize<T>(await r.Content.ReadAsByteArrayAsync());

        /// <summary>Pico de workers simultâneos e pids observados no arquivo de rastreio do worker falso.</summary>
        public (int MaxOverlap, List<int> Pids) Track()
        {
            var events = new List<(long Ticks, int Delta)>();
            var pids = new List<int>();
            if (File.Exists(TrackFile))
            {
                foreach (var line in File.ReadAllLines(TrackFile))
                {
                    var p = line.Split(' ');
                    if (p.Length != 3) continue;
                    if (p[0] == "S") pids.Add(int.Parse(p[1]));
                    events.Add((long.Parse(p[2]), p[0] == "S" ? 1 : -1));
                }
            }
            int cur = 0, max = 0;
            foreach (var e in events.OrderBy(e => e.Ticks).ThenBy(e => e.Delta)) { cur += e.Delta; max = Math.Max(max, cur); }
            return (max, pids);
        }

        public static bool IsAlive(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
        }

        public string AllLogs()
        {
            if (!Directory.Exists(LogDir)) return "";
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(LogDir, "*.log"))
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                    sb.Append(r.ReadToEnd());
            return sb.ToString();
        }

        public void Dispose()
        {
            Http.Dispose();
            try { Host.StopAsync().GetAwaiter().GetResult(); } catch { }
            Environment.SetEnvironmentVariable("FAKE_RUNNER_TRACK", null);
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
