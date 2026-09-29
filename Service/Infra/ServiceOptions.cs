using System;
using System.Collections.Generic;
using System.IO;

namespace LayoutParserLowCodeRunner.Service.Infra
{
    /// <summary>
    /// Configuração DO SERVIÇO. Sysmiddle*/globalFolder/package são caminhos/valores locais do Windows:
    /// o cliente (a API) nunca os envia. Origem: variável de ambiente <c>LowCodeRunner__&lt;Chave&gt;</c>
    /// (prioridade) e, depois, <c>appSettings</c> do .exe.config. Valor ausente/inválido cai no default
    /// (degradar, nunca derrubar) e vira aviso em <see cref="Warnings"/>.
    /// </summary>
    internal sealed class ServiceOptions
    {
        public const string EnvPrefix = "LowCodeRunner__";

        public string SysmiddleDir { get; private set; } = "";
        public string GlobalFolder { get; private set; } = "";
        /// <summary>Identificador do projeto Sysmiddle (o &lt;PackageMappers&gt; do config.xml da instância).</summary>
        public string Package { get; private set; } = "";
        public string DefaultMapperName { get; private set; }

        /// <summary>Teto por execução do worker. 48-137 s medidos; 180 s = folga sobre o pior caso.</summary>
        public int RunnerTimeoutSeconds { get; private set; } = 180;
        public int MaxConcurrentRunners { get; private set; } = 2;
        /// <summary>Requisições únicas esperando slot além disso recebem 503 + Retry-After.</summary>
        public int MaxQueue { get; private set; } = 8;
        public int MaxBodyBytes { get; private set; } = 20 * 1024 * 1024;
        public bool NfePostProcessingDefault { get; private set; }
        public string ListenPrefix { get; private set; } = "http://localhost:5230/";
        public string LogDir { get; private set; }
        public string WorkerTempDir { get; private set; }

        /// <summary>Exe do worker; default = o próprio exe. Aponte para o exe dentro da Bin do Sysmiddle se necessário.</summary>
        public string WorkerExePath { get; private set; }
        public int MapperCacheSeconds { get; private set; } = 300;
        public int MaxCandidates { get; private set; } = 16;
        public int GracefulShutdownSeconds { get; private set; } = 30;
        public int QueueRetryAfterSeconds { get; private set; } = 30;

        public IList<string> Warnings { get; } = new List<string>();

        public static ServiceOptions Load(Func<string, string> get)
        {
            var o = new ServiceOptions();
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LayoutParserLowCodeRunner");

            o.SysmiddleDir = Text(get, "SysmiddleDir", "");
            o.GlobalFolder = Text(get, "GlobalFolder", "");
            o.Package = Text(get, "Package", "");
            var dm = Text(get, "DefaultMapperName", "");
            o.DefaultMapperName = dm.Length == 0 ? null : dm;

            o.RunnerTimeoutSeconds = Int(get, o, "RunnerTimeoutSeconds", 180);
            o.MaxConcurrentRunners = Int(get, o, "MaxConcurrentRunners", 2);
            o.MaxQueue = Int(get, o, "MaxQueue", 8, allowZero: true);
            o.MaxBodyBytes = Int(get, o, "MaxBodyBytes", 20 * 1024 * 1024);
            o.NfePostProcessingDefault = Bool(get, o, "NfePostProcessingDefault", false);
            o.ListenPrefix = Text(get, "ListenPrefix", "http://localhost:5230/");
            o.LogDir = Text(get, "LogDir", Path.Combine(baseDir, "logs"));
            o.WorkerTempDir = Text(get, "WorkerTempDir", Path.Combine(baseDir, "work"));
            o.WorkerExePath = Text(get, "WorkerExePath", "");
            o.MapperCacheSeconds = Int(get, o, "MapperCacheSeconds", 300, allowZero: true);
            o.MaxCandidates = Int(get, o, "MaxCandidates", 16);
            o.GracefulShutdownSeconds = Int(get, o, "GracefulShutdownSeconds", 30, allowZero: true);
            o.QueueRetryAfterSeconds = Int(get, o, "QueueRetryAfterSeconds", 30);

            if (o.Package.Length == 0)
                o.Warnings.Add("Package nao configurado: transformacoes vao falhar com exitCode 9 (PackageNotConfigured).");
            if (o.GlobalFolder.Length == 0)
                o.Warnings.Add("GlobalFolder nao configurado.");
            if (o.MaxConcurrentRunners > 1)
                o.Warnings.Add("MaxConcurrentRunners > 1: cada worker e um processo isolado, mas o host FiatMQ/licenca e sensivel a concorrencia; valide antes de subir.");
            return o;
        }

        /// <summary>Leitor padrão: variável de ambiente <c>LowCodeRunner__Chave</c>, senão appSettings.</summary>
        public static Func<string, string> FromEnvironmentAndConfig(Func<string, string> appSettings)
        {
            return key =>
            {
                var env = Environment.GetEnvironmentVariable(EnvPrefix + key);
                return string.IsNullOrWhiteSpace(env) ? appSettings(key) : env;
            };
        }

        private static string Text(Func<string, string> get, string key, string def)
        {
            var v = get(key);
            return string.IsNullOrWhiteSpace(v) ? def : v.Trim();
        }

        private static int Int(Func<string, string> get, ServiceOptions o, string key, int def, bool allowZero = false)
        {
            var raw = get(key);
            if (string.IsNullOrWhiteSpace(raw))
                return def;
            if (int.TryParse(raw.Trim(), out var n) && (n > 0 || (allowZero && n == 0)))
                return n;
            o.Warnings.Add(key + "='" + raw + "' invalido; usando o default " + def + ".");
            return def;
        }

        private static bool Bool(Func<string, string> get, ServiceOptions o, string key, bool def)
        {
            var raw = get(key);
            if (string.IsNullOrWhiteSpace(raw))
                return def;
            raw = raw.Trim();
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;
            o.Warnings.Add(key + "='" + raw + "' invalido; usando o default " + def + ".");
            return def;
        }
    }
}
