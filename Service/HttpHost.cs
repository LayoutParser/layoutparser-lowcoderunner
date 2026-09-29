using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LayoutParserLowCodeRunner.Runner;

namespace LayoutParserLowCodeRunner.Service
{
    // API HTTP:
    //   POST /transform  { "mapperId"|"mapperName", "content", "fileName"? } -> 200 application/xml (X-Correlation-Id)
    //   GET  /mappers    -> 200 JSON
    //   GET  /health     -> 200 JSON
    // Erros: JSON { "error": { "code", "exitCode", "message", "correlationId" } }; ver RunnerExitCode.HttpStatus (+ 429 quando saturado).
    internal sealed class HttpHost : IDisposable
    {
        private readonly SidecarConfig _cfg;
        private readonly TransformEngine _engine;
        private readonly RunnerLog _log;
        private readonly SemaphoreSlim _slots;
        private readonly HttpListener _listener = new HttpListener();
        private readonly JavaScriptSerializer _json;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        public HttpHost(SidecarConfig cfg)
        {
            _cfg = cfg;
            _log = new RunnerLog(cfg.LogFile);
            _engine = new TransformEngine(new EngineOptions
            {
                SysmiddleDir = cfg.SysmiddleDir,
                GlobalFolder = cfg.GlobalFolder,
                Package = cfg.Package,
                RequirePackage = true
            }, _log);
            _slots = new SemaphoreSlim(cfg.MaxConcurrency, cfg.MaxConcurrency);
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            _listener.Prefixes.Add(cfg.ListenPrefix);
        }

        public void Start()
        {
            _listener.Start();
            _log.Info("host", $"Listening on {_cfg.ListenPrefix} maxConcurrency={_cfg.MaxConcurrency} timeout={_cfg.TimeoutSeconds}s package='{_cfg.Package}'");
            if (_cfg.MaxConcurrency > 1)
                _log.Warn("host", "MaxConcurrency > 1: valide se as DLLs Sysmiddle (APIManager/APIExecutor) são thread-safe antes de usar em produção.");
            Task.Run(AcceptLoop);
        }

        public void Stop()
        {
            _stop.Cancel();
            try { _listener.Close(); } catch { }
            _log.Info("host", "Stopped");
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var corr = Guid.NewGuid().ToString("N");
            try
            {
                ctx.Response.Headers["X-Correlation-Id"] = corr;
                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                var method = ctx.Request.HttpMethod.ToUpperInvariant();

                if (path == "/health" && method == "GET") { Health(ctx); return; }
                if (path == "/mappers" && method == "GET") { Mappers(ctx, corr); return; }
                if (path == "/transform" && method == "POST") { Transform(ctx, corr); return; }

                var known = path == "/health" || path == "/mappers" || path == "/transform";
                WriteError(ctx, known ? 405 : 404, known ? "MethodNotAllowed" : "NotFound", 0, known ? "Método não permitido" : "Rota não encontrada", corr);
            }
            catch (TooBusyException)
            {
                _log.Warn(corr, $"429 limite de concorrência ({_cfg.MaxConcurrency}) atingido");
                ctx.Response.Headers["Retry-After"] = "5";
                WriteError(ctx, 429, "TooManyRequests", 0, "Limite de concorrência atingido", corr);
            }
            catch (RunnerException rex)
            {
                _log.Error(corr, $"{rex.Code}: {rex.Message}");
                WriteError(ctx, rex.Code.HttpStatus(), rex.Code.ToString(), (int)rex.Code, rex.Message, corr);
            }
            catch (Exception ex)
            {
                _log.Error(corr, "FATAL " + ex);
                WriteError(ctx, 500, RunnerExitCode.Unexpected.ToString(), (int)RunnerExitCode.Unexpected, "Erro interno", corr);
            }
        }

        private void Health(HttpListenerContext ctx)
        {
            WriteJson(ctx, 200, new Dictionary<string, object>
            {
                { "status", "ok" },
                { "uptimeSeconds", (int)(DateTime.UtcNow - _startedUtc).TotalSeconds },
                { "package", _cfg.Package },
                { "maxConcurrency", _cfg.MaxConcurrency },
                { "availableSlots", _slots.CurrentCount },
                { "timeoutSeconds", _cfg.TimeoutSeconds }
            });
        }

        private void Mappers(HttpListenerContext ctx, string corr)
        {
            var list = RunLimited(() => _engine.ListMappers(corr), corr);
            WriteJson(ctx, 200, list);
        }

        private void Transform(HttpListenerContext ctx, string corr)
        {
            if (ctx.Request.ContentLength64 > _cfg.MaxRequestBytes)
                throw new RunnerException(RunnerExitCode.InvalidArguments, "Request excede MaxRequestBytes");

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            Dictionary<string, object> req;
            try { req = _json.Deserialize<Dictionary<string, object>>(body); }
            catch (Exception ex) { throw new RunnerException(RunnerExitCode.InvalidArguments, "JSON inválido: " + ex.Message, ex); }
            if (req == null)
                throw new RunnerException(RunnerExitCode.InvalidArguments, "Body vazio");

            string Get(string k) => req.TryGetValue(k, out var v) ? v as string : null;
            var mapperId = Get("mapperId");
            var mapperName = Get("mapperName");
            var content = Get("content");
            var fileName = Get("fileName") ?? "input.txt";

            _log.Info(corr, $"POST /transform mapperId='{mapperId}' mapperName='{mapperName}' fileName='{fileName}' chars={content?.Length ?? 0}");

            var output = RunLimited(() => _engine.Transform(mapperId, mapperName, content, fileName, corr), corr);

            var bytes = new UTF8Encoding(false).GetBytes(output);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        // Limite de concorrência (429 imediato se saturado) + timeout (504).
        // Em timeout a thread do Sysmiddle NÃO é abortada (não é seguro); o slot só é liberado quando ela terminar,
        // então trabalho travado continua contando contra o limite em vez de estourar a concorrência.
        private T RunLimited<T>(Func<T> work, string corr)
        {
            if (!_slots.Wait(0))
                throw new TooBusyException();

            Task<T> task;
            try
            {
                task = Task.Run(work);
            }
            catch
            {
                _slots.Release();
                throw;
            }
            task.ContinueWith(_ => _slots.Release(), TaskScheduler.Default);

            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(_cfg.TimeoutSeconds)))
                {
                    _log.Warn(corr, $"Timeout de {_cfg.TimeoutSeconds}s; execução segue em background até terminar");
                    throw new RunnerException(RunnerExitCode.Timeout, $"Timeout após {_cfg.TimeoutSeconds}s");
                }
                return task.Result;
            }
            catch (AggregateException ae)
            {
                var inner = ae.InnerException ?? ae;
                if (inner is RunnerException) throw inner;
                throw new RunnerException(RunnerExitCode.Unexpected, inner.Message, inner);
            }
        }

        private void WriteJson(HttpListenerContext ctx, int status, object payload)
        {
            var bytes = new UTF8Encoding(false).GetBytes(_json.Serialize(payload));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        private void WriteError(HttpListenerContext ctx, int status, string code, int exitCode, string message, string corr)
        {
            try
            {
                WriteJson(ctx, status, new Dictionary<string, object>
                {
                    { "error", new Dictionary<string, object>
                        {
                            { "code", code }, { "exitCode", exitCode }, { "message", message }, { "correlationId", corr }
                        } }
                });
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        private sealed class TooBusyException : Exception { }
    }
}
