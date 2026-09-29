using System;
using System.Configuration;
using System.ServiceProcess;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service
{
    /// <summary>Serviço Windows (binPath: "...\LayoutParserLowCodeRunner.exe --service"). Config: env LowCodeRunner__* e appSettings.</summary>
    internal sealed class RunnerService : ServiceBase
    {
        public const string Name = "LayoutParserLowCodeRunner";
        private ServiceHost _host;

        public RunnerService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
        }

        public static ServiceOptions LoadOptions()
        {
            return ServiceOptions.Load(ServiceOptions.FromEnvironmentAndConfig(k => ConfigurationManager.AppSettings[k]));
        }

        protected override void OnStart(string[] args)
        {
            _host = new ServiceHost(LoadOptions());
            _host.Start();
        }

        protected override void OnStop()
        {
            if (_host != null)
                _host.StopAsync().GetAwaiter().GetResult();
        }

        protected override void OnShutdown()
        {
            OnStop();
        }
    }
}
