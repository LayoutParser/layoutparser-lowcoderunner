using System;
using System.Reflection;
using System.Threading.Tasks;
using LayoutParserLowCodeRunner.Service.Application;
using LayoutParserLowCodeRunner.Service.Http;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service
{
    /// <summary>Composição do serviço: opções -> launcher/gate -> aplicação -> HTTP. Usado pelo serviço Windows, pelo --console e pelos testes.</summary>
    internal sealed class ServiceHost
    {
        private readonly ServiceOptions _opt;
        private readonly RollingLogger _log;
        private readonly WorkerLauncher _launcher;
        private readonly MiniHttpServer _server;

        public int Port { get { return _server.Port; } }
        public ServiceOptions Options { get { return _opt; } }

        public ServiceHost(ServiceOptions opt, bool echoToConsole = false)
        {
            _opt = opt;
            _log = new RollingLogger(opt.LogDir) { EchoToConsole = echoToConsole };

            var workerExe = string.IsNullOrWhiteSpace(opt.WorkerExePath)
                ? Assembly.GetExecutingAssembly().Location
                : opt.WorkerExePath;
            _launcher = new WorkerLauncher(workerExe);

            var gate = new ExecutionGate(opt.MaxConcurrentRunners, opt.MaxQueue, opt.QueueRetryAfterSeconds);
            var exec = new LowCodeExecutor(opt, _launcher, gate, _log);
            var catalog = new MapperCatalogService(opt, exec);
            var health = new HealthService(opt, _launcher, catalog, gate);
            var router = new Router(new TransformService(opt, exec, _log), catalog, health, _log);
            _server = new MiniHttpServer(router.HandleAsync, opt.MaxBodyBytes);
        }

        public void Start()
        {
            foreach (var w in _opt.Warnings)
                _log.Warn("host", w);

            System.IO.Directory.CreateDirectory(_opt.WorkerTempDir);
            _server.Start(_opt.ListenPrefix);
            _log.Info("host", string.Format("Ouvindo em {0} (porta {1}) slots={2} fila={3} timeout={4}s package={5}",
                _opt.ListenPrefix, _server.Port, _opt.MaxConcurrentRunners, _opt.MaxQueue, _opt.RunnerTimeoutSeconds,
                string.IsNullOrEmpty(_opt.Package) ? "(vazio)" : "configurado"));
        }

        /// <summary>Graceful: espera execuções em andamento até GracefulShutdownSeconds e depois mata os workers.</summary>
        public async Task StopAsync()
        {
            _log.Info("host", "Encerrando (graceful " + _opt.GracefulShutdownSeconds + "s)");
            await _server.StopAsync(TimeSpan.FromSeconds(_opt.GracefulShutdownSeconds), _launcher.KillAll).ConfigureAwait(false);
            _launcher.KillAll();
            _log.Info("host", "Encerrado");
        }
    }
}
