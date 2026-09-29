using System.ServiceProcess;

namespace LayoutParserLowCodeRunner.Service
{
    internal sealed class RunnerService : ServiceBase
    {
        public const string Name = "LayoutParserLowCodeRunner";
        private HttpHost _host;

        public RunnerService() { ServiceName = Name; }

        protected override void OnStart(string[] args)
        {
            _host = new HttpHost(SidecarConfig.Load());
            _host.Start();
        }

        protected override void OnStop()
        {
            _host?.Stop();
        }
    }
}
