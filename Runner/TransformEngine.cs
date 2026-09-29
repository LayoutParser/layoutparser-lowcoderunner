using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace LayoutParserLowCodeRunner.Runner
{
    public sealed class EngineOptions
    {
        public string SysmiddleDir { get; set; }
        public string GlobalFolder { get; set; }
        public string Package { get; set; }
        // Serviço: package vazio é erro de configuração. CLI legado: aceita vazio (comportamento anterior).
        public bool RequirePackage { get; set; }
    }

    public sealed class MapperInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    // Executa o Sysmiddle em processo (reflection), sem Process.Start.
    // A inicialização (APIManager, executor) acontece uma única vez por processo, pois APIManager é singleton/estático.
    public sealed class TransformEngine
    {
        private readonly EngineOptions _opt;
        private readonly RunnerLog _log;
        private readonly object _initGate = new object();
        private bool _initialized;

        private Type _executorType;
        private object _executor;
        private string _tempGlobalConfigPath;

        internal TransformEngine(EngineOptions opt, RunnerLog log)
        {
            _opt = opt;
            _log = log;
        }

        public string Transform(string mapperId, string mapperName, string content, string fileName, string correlationId)
        {
            if (string.IsNullOrWhiteSpace(mapperId) && string.IsNullOrWhiteSpace(mapperName))
                throw new RunnerException(RunnerExitCode.InvalidArguments, "Informe mapperId ou mapperName");
            if (content == null)
                throw new RunnerException(RunnerExitCode.InvalidArguments, "Conteúdo de entrada ausente");

            EnsureInitialized(correlationId);

            var mapper = ResolveMapper(mapperId, mapperName);
            var mapperGuid = GetProp(mapper, "IdentifierGuid");
            _log.Info(correlationId, $"Mapper resolvido: type={mapper.GetType().FullName} IdentifierGuid={mapperGuid}");

            var execMapper = FindExecuteMapper();
            var t0 = DateTime.UtcNow;
            object result;
            try
            {
                result = execMapper.Invoke(_executor, new[] { mapperGuid, content, true, fileName });
            }
            catch (TargetInvocationException tie)
            {
                throw new RunnerException(RunnerExitCode.TransformFailed, "Falha ao executar o mapper: " + Unwrap(tie).Message, Unwrap(tie));
            }
            if (result == null)
                throw new RunnerException(RunnerExitCode.TransformFailed, "ExecuteMapper retornou null");
            _log.Info(correlationId, $"ExecuteMapper OK em {(DateTime.UtcNow - t0).TotalMilliseconds:0}ms");

            return GetProp(result, "TransformedDocument") as string ?? "";
        }

        // O Sysmiddle não tem contrato documentado para listagem; procura um método sem parâmetros do APIExecutor
        // que retorne uma coleção de mappers. Ajuste MapperListMethodNames após validar com as DLLs reais.
        private static readonly string[] MapperListMethodNames = { "GetMappers", "GetAllMappers", "ListMappers", "GetMapperList" };

        public IList<MapperInfo> ListMappers(string correlationId)
        {
            EnsureInitialized(correlationId);

            MethodInfo list = null;
            foreach (var name in MapperListMethodNames)
            {
                list = _executorType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (list != null) break;
            }
            if (list == null)
                throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed,
                    "APIExecutor não expõe método de listagem de mappers (" + string.Join("/", MapperListMethodNames) + ")");

            object raw;
            try { raw = list.Invoke(_executor, null); }
            catch (TargetInvocationException tie) { throw new RunnerException(RunnerExitCode.Unexpected, "Falha ao listar mappers: " + Unwrap(tie).Message, Unwrap(tie)); }

            var items = new List<MapperInfo>();
            if (raw is IEnumerable en)
            {
                foreach (var m in en)
                {
                    if (m == null) continue;
                    items.Add(new MapperInfo
                    {
                        Id = Convert.ToString(TryGetProp(m, "IdentifierGuid")),
                        Name = Convert.ToString(TryGetProp(m, "Name"))
                    });
                }
            }
            return items;
        }

        private void EnsureInitialized(string correlationId)
        {
            if (_initialized) return;
            lock (_initGate)
            {
                if (_initialized) return;
                Initialize(correlationId);
                _initialized = true;
            }
        }

        private void Initialize(string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_opt.SysmiddleDir) || string.IsNullOrWhiteSpace(_opt.GlobalFolder))
                throw new RunnerException(RunnerExitCode.ConfigurationInvalid, "sysmiddleDir e globalFolder são obrigatórios");
            if (_opt.RequirePackage && string.IsNullOrWhiteSpace(_opt.Package))
                throw new RunnerException(RunnerExitCode.PackageNotConfigured, "package não configurado");
            if (!Directory.Exists(_opt.SysmiddleDir))
                throw new RunnerException(RunnerExitCode.ConfigurationInvalid, $"sysmiddleDir não existe: {_opt.SysmiddleDir}");
            if (!Directory.Exists(_opt.GlobalFolder))
                throw new RunnerException(RunnerExitCode.ConfigurationInvalid, $"globalFolder não existe: {_opt.GlobalFolder}");

            var globalConfigPath = Path.Combine(_opt.GlobalFolder, "global.config");
            if (!File.Exists(globalConfigPath))
                throw new RunnerException(RunnerExitCode.ConfigurationInvalid, $"global.config não encontrado em: {globalConfigPath}");

            _log.Info(correlationId, $"INIT sysmiddleDir='{_opt.SysmiddleDir}' globalConfig='{globalConfigPath}' package='{_opt.Package}'");

            var runnerLoggerXmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logger.xml");
            _log.Info(correlationId, $"Runner logger.xml='{runnerLoggerXmlPath}' exists={File.Exists(runnerLoggerXmlPath)}");

            // global.config temporário apontando para o logger.xml do runner (um por processo)
            _tempGlobalConfigPath = Path.Combine(Path.GetTempPath(), "layoutparser-lowcode", $"global_{Guid.NewGuid():N}.config");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_tempGlobalConfigPath)!);
                var xdoc = XDocument.Load(globalConfigPath);
                var root = xdoc.Root;
                if (root != null)
                {
                    var loggerNode = root.Element("ConfigurationLoggerFilePath");
                    if (loggerNode == null)
                    {
                        loggerNode = new XElement("ConfigurationLoggerFilePath");
                        root.AddFirst(loggerNode);
                    }
                    loggerNode.Value = runnerLoggerXmlPath;
                }
                xdoc.Save(_tempGlobalConfigPath);
                _log.Info(correlationId, $"Temp global.config criado: '{_tempGlobalConfigPath}'");
            }
            catch (Exception ex)
            {
                _tempGlobalConfigPath = globalConfigPath;
                _log.Warn(correlationId, $"não foi possível criar global.config temporário: {ex.Message}. Usando o original.");
            }

            var sysmiddleDir = _opt.SysmiddleDir;
            AppDomain.CurrentDomain.AssemblyResolve += (_, ev) =>
            {
                try
                {
                    var candidate = Path.Combine(sysmiddleDir, new AssemblyName(ev.Name).Name + ".dll");
                    if (File.Exists(candidate))
                        return Assembly.LoadFrom(candidate);
                }
                catch { }
                return null;
            };

            var apiAsmPath = Path.Combine(sysmiddleDir, "SysMiddle.ConnectUs.API.dll");
            if (!File.Exists(apiAsmPath))
                throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed, $"SysMiddle.ConnectUs.API.dll não encontrado em: {apiAsmPath}");

            try
            {
                var apiAsm = Assembly.LoadFrom(apiAsmPath);
                var apiManagerType = apiAsm.GetType("SysMiddle.ConnectUs.API.Service.APIManager", throwOnError: true);
                _executorType = apiAsm.GetType("SysMiddle.ConnectUs.API.Service.APIExecutor", throwOnError: true);

                var globalConfProp = apiManagerType.GetProperty("GlobalConfigurationFileName", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMemberException("APIManager.GlobalConfigurationFileName não encontrado");
                globalConfProp.SetValue(null, _tempGlobalConfigPath, null);

                var instanceProp = apiManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMemberException("APIManager.Instance não encontrado");
                var apiManager = instanceProp.GetValue(null, null)
                    ?? throw new InvalidOperationException("APIManager.Instance retornou null");
                _log.Info(correlationId, "APIManager.Instance obtido");

                var getExec = apiManagerType.GetMethod("GetApiExecutorByIdentifier", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMemberException("APIManager.GetApiExecutorByIdentifier não encontrado");

                try
                {
                    _executor = getExec.Invoke(apiManager, new object[] { string.Empty, _opt.Package ?? "" });
                }
                catch (TargetInvocationException tie)
                {
                    throw new RunnerException(RunnerExitCode.PackageNotFound, $"package '{_opt.Package}' inválido: {Unwrap(tie).Message}", Unwrap(tie));
                }
                if (_executor == null)
                    throw new RunnerException(RunnerExitCode.PackageNotFound, $"package '{_opt.Package}' não encontrado (verifique package/global.config/licença)");
                _log.Info(correlationId, "APIExecutor obtido");
            }
            catch (RunnerException) { throw; }
            catch (Exception ex)
            {
                throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed, "Falha ao carregar o Sysmiddle: " + Unwrap(ex).Message, ex);
            }
        }

        private object ResolveMapper(string mapperId, string mapperName)
        {
            var byId = !string.IsNullOrWhiteSpace(mapperId); // mapperId tem prioridade
            var methodName = byId ? "GetMapperByIdentifier" : "GetMapperByName";
            var method = _executorType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed, $"APIExecutor.{methodName} não encontrado");

            object mapper;
            try
            {
                mapper = method.Invoke(_executor, new object[] { byId ? mapperId : mapperName });
            }
            catch (TargetInvocationException tie)
            {
                throw new RunnerException(RunnerExitCode.MapperNotFound, "Mapper não encontrado: " + Unwrap(tie).Message, Unwrap(tie));
            }
            if (mapper == null)
                throw new RunnerException(RunnerExitCode.MapperNotFound, "Mapper não encontrado (id/nome).");
            return mapper;
        }

        // ExecuteMapper tem overloads; usa o de 4 parâmetros (object, string, bool, string)
        private MethodInfo FindExecuteMapper()
        {
            foreach (var m in _executorType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(m.Name, "ExecuteMapper", StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length == 4 && ps[1].ParameterType == typeof(string)
                    && ps[2].ParameterType == typeof(bool) && ps[3].ParameterType == typeof(string))
                    return m;
            }
            throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed, "APIExecutor.ExecuteMapper (4 params) não encontrado");
        }

        private static object GetProp(object obj, string name)
        {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null)
                throw new RunnerException(RunnerExitCode.SysmiddleLoadFailed, $"{obj.GetType().Name}.{name} não encontrado");
            return p.GetValue(obj, null);
        }

        private static object TryGetProp(object obj, string name)
            => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj, null);

        private static Exception Unwrap(Exception ex)
            => ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
    }
}
