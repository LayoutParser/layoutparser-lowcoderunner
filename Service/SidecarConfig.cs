using System;
using System.Configuration;
using System.IO;

namespace LayoutParserLowCodeRunner.Service
{
    // Configuração local do sidecar (App.config -> LayoutParserLowCodeRunner.exe.config, appSettings).
    // sysmiddleDir/globalFolder/package são caminhos/valores locais do Windows, por isso não vêm no request.
    internal sealed class SidecarConfig
    {
        public string ListenPrefix { get; private set; }
        public string SysmiddleDir { get; private set; }
        public string GlobalFolder { get; private set; }
        public string Package { get; private set; }
        public int MaxConcurrency { get; private set; }
        public int TimeoutSeconds { get; private set; }
        public int MaxRequestBytes { get; private set; }
        public string LogFile { get; private set; }

        public static SidecarConfig Load()
        {
            string S(string key, string def = "")
            {
                var v = ConfigurationManager.AppSettings[key];
                return string.IsNullOrWhiteSpace(v) ? def : v.Trim();
            }
            int I(string key, int def)
                => int.TryParse(S(key), out var n) && n > 0 ? n : def;

            var prefix = S("ListenPrefix", "http://127.0.0.1:5080/");
            if (!prefix.EndsWith("/")) prefix += "/";

            return new SidecarConfig
            {
                ListenPrefix = prefix,
                SysmiddleDir = S("SysmiddleDir"),
                GlobalFolder = S("GlobalFolder"),
                Package = S("Package"),
                // Padrão 1: não há garantia de que as DLLs Sysmiddle sejam thread-safe. Só aumente após validar.
                MaxConcurrency = I("MaxConcurrency", 1),
                TimeoutSeconds = I("TimeoutSeconds", 180),
                MaxRequestBytes = I("MaxRequestBytes", 50 * 1024 * 1024),
                LogFile = S("LogFile", Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "LayoutParserLowCodeRunner", "logs", "runner.log"))
            };
        }
    }
}
