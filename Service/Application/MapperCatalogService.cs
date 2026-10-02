using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service.Application
{
    /// <summary>
    /// GET /v1/mappers (modo LIST) com cache — o init do SDK custa 12-38 s. O resultado do último LIST também
    /// alimenta o /v1/health: licença/package só são provados de fato ao subir o SDK.
    /// </summary>
    internal sealed class MapperCatalogService
    {
        private readonly ServiceOptions _opt;
        private readonly LowCodeExecutor _exec;
        private readonly SemaphoreSlim _refresh = new SemaphoreSlim(1, 1);
        private IList<MapperItem> _cache;
        private DateTime _cachedAtUtc;

        /// <summary>null = nunca verificado; true/false = resultado do último LIST.</summary>
        public bool? LastDeepOk { get; private set; }
        public int LastDeepExitCode { get; private set; }

        public MapperCatalogService(ServiceOptions opt, LowCodeExecutor exec)
        {
            _opt = opt;
            _exec = exec;
        }

        public async Task<IList<MapperItem>> GetAsync(string corr, bool force, CancellationToken ct)
        {
            var cached = _cache;
            if (!force && cached != null && Fresh())
                return cached;

            await _refresh.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && _cache != null && Fresh())
                    return _cache;

                _exec.EnsureConfigured();
                var r = await _exec.ListAsync(corr, ct).ConfigureAwait(false);
                if (r.TimedOut)
                {
                    LastDeepOk = false;
                    LastDeepExitCode = -1;
                    throw new ServiceException(504, -1, ErrorCodes.Timeout, "Tempo limite de execucao excedido.");
                }
                if (r.Cancelled)
                    throw new ServiceException(499, -1, ErrorCodes.ClientClosedRequest, "Requisicao cancelada.");

                LastDeepExitCode = r.ExitCode;
                LastDeepOk = r.ExitCode == RunnerExitCodes.Ok;
                if (r.ExitCode != RunnerExitCodes.Ok)
                    throw new ServiceException(ExitMap.Status(r.ExitCode), r.ExitCode, ErrorCodes.ForExit(r.ExitCode), ExitMap.Message(r.ExitCode));

                _cache = Parse(r.Stdout);
                _cachedAtUtc = DateTime.UtcNow;
                return _cache;
            }
            finally
            {
                _refresh.Release();
            }
        }

        private bool Fresh()
        {
            return (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < _opt.MapperCacheSeconds;
        }

        /// <summary>stdout do LIST: "guid&lt;TAB&gt;nome" por linha. Linhas sem TAB (ruído) são ignoradas.</summary>
        public static IList<MapperItem> Parse(string stdout)
        {
            var list = new List<MapperItem>();
            if (string.IsNullOrEmpty(stdout)) return list;
            foreach (var raw in stdout.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                list.Add(new MapperItem { Id = line.Substring(0, tab).Trim(), Name = line.Substring(tab + 1).Trim() });
            }
            return list;
        }
    }

    /// <summary>GET /v1/health e /v1/info.</summary>
    internal sealed class HealthService
    {
        private readonly ServiceOptions _opt;
        private readonly WorkerLauncher _launcher;
        private readonly MapperCatalogService _catalog;
        private readonly ExecutionGate _gate;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        public HealthService(ServiceOptions opt, WorkerLauncher launcher, MapperCatalogService catalog, ExecutionGate gate)
        {
            _opt = opt;
            _launcher = launcher;
            _catalog = catalog;
            _gate = gate;
        }

        /// <summary>
        /// Checagens baratas (worker, globalFolder/global.config, package configurado) + último LIST. Com
        /// <paramref name="deep"/> força um LIST agora (sobe o SDK: valida DLLs, licença e package de verdade).
        /// </summary>
        public async Task<KeyValuePair<int, HealthResponse>> CheckAsync(string corr, bool deep, CancellationToken ct)
        {
            var h = new HealthResponse { Status = "ok", License = "unchecked", Package = "ok", GlobalFolder = "ok" };
            var reasons = new List<string>();

            if (!File.Exists(_launcher.ExePath)) reasons.Add("worker ausente");
            if (string.IsNullOrWhiteSpace(_opt.GlobalFolder) || !File.Exists(Path.Combine(_opt.GlobalFolder, "global.config")))
            {
                h.GlobalFolder = "failed";
                reasons.Add("globalFolder/global.config invalido");
            }
            if (string.IsNullOrWhiteSpace(_opt.Package))
            {
                h.Package = "failed";
                reasons.Add("package nao configurado");
            }

            if (deep && reasons.Count == 0)
            {
                try { await _catalog.GetAsync(corr, true, ct).ConfigureAwait(false); }
                catch (ServiceException) { /* o estado fica em LastDeepOk/LastDeepExitCode */ }
            }

            if (_catalog.LastDeepOk == true)
            {
                h.License = "ok";
            }
            else if (_catalog.LastDeepOk == false)
            {
                var code = _catalog.LastDeepExitCode;
                if (code == RunnerExitCodes.PackageNotFound) { h.Package = "failed"; h.License = "unknown"; }
                else { h.License = "failed"; }
                reasons.Add("ultimo teste do SDK falhou (exit " + code + ")");
            }

            if (reasons.Count > 0)
            {
                h.Status = "degraded";
                h.Reason = string.Join("; ", reasons);
                return new KeyValuePair<int, HealthResponse>(503, h);
            }
            return new KeyValuePair<int, HealthResponse>(200, h);
        }

        public InfoResponse Info()
        {
            var asm = typeof(HealthService).Assembly;
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
            return new InfoResponse
            {
                Version = asm.GetName().Version.ToString(),
                Build = info == null ? "" : info.InformationalVersion,
                X86 = !Environment.Is64BitProcess,
                UptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                Package = _opt.Package,
                MaxConcurrent = _opt.MaxConcurrentRunners,
                Running = _gate.Running,
                Queued = _gate.Queued
            };
        }
    }
}
