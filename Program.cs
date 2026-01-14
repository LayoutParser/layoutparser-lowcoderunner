using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace LayoutParserLowCodeRunner
{
    internal static class Program
    {
        // CLI:
        // LayoutParserLowCodeRunner.exe --sysmiddleDir "C:\...\Sysmiddle" --globalFolder "C:\...\global" --package "XYZ" --mapperName "..." --mapperId "..." --inputFile "..." --outputFile "..." --fileName "..."
        // - inputFile: texto/XML de entrada (UTF-8)
        // - outputFile: XML transformado (UTF-8)
        // Regras:
        // - mapperId tem prioridade sobre mapperName
        // - exige sysmiddleDir e globalFolder
        public static int Main(string[] args)
        {
            try
            {
                var a = ParseArgs(args);

                var sysmiddleDir = Require(a, "--sysmiddleDir");
                var globalFolder = Require(a, "--globalFolder");
                var package = Get(a, "--package") ?? "";
                var mapperId = Get(a, "--mapperId");
                var mapperName = Get(a, "--mapperName");
                var inputFile = Require(a, "--inputFile");
                var outputFile = Require(a, "--outputFile");
                var fileName = Get(a, "--fileName") ?? Path.GetFileName(inputFile);

                if (string.IsNullOrWhiteSpace(mapperId) && string.IsNullOrWhiteSpace(mapperName))
                    throw new ArgumentException("Informe --mapperId ou --mapperName");

                if (!Directory.Exists(sysmiddleDir))
                    throw new DirectoryNotFoundException($"sysmiddleDir não existe: {sysmiddleDir}");

                if (!Directory.Exists(globalFolder))
                    throw new DirectoryNotFoundException($"globalFolder não existe: {globalFolder}");

                var globalConfigPath = Path.Combine(globalFolder, "global.config");
                if (!File.Exists(globalConfigPath))
                    throw new FileNotFoundException($"global.config não encontrado em: {globalConfigPath}");

                // Garantir que dependências sejam resolvidas a partir do sysmiddleDir
                AppDomain.CurrentDomain.AssemblyResolve += (_, ev) =>
                {
                    try
                    {
                        var name = new AssemblyName(ev.Name).Name + ".dll";
                        var candidate = Path.Combine(sysmiddleDir, name);
                        if (File.Exists(candidate))
                            return Assembly.LoadFrom(candidate);
                    }
                    catch { }
                    return null;
                };

                // Carregar a assembly principal do SysMiddle
                var apiAsmPath = Path.Combine(sysmiddleDir, "SysMiddle.ConnectUs.API.dll");
                if (!File.Exists(apiAsmPath))
                    throw new FileNotFoundException($"SysMiddle.ConnectUs.API.dll não encontrado em: {apiAsmPath}");

                var apiAsm = Assembly.LoadFrom(apiAsmPath);

                // Tipos
                var apiManagerType = apiAsm.GetType("SysMiddle.ConnectUs.API.Service.APIManager", throwOnError: true);
                var apiExecutorType = apiAsm.GetType("SysMiddle.ConnectUs.API.Service.APIExecutor", throwOnError: true);

                // APIManager.GlobalConfigurationFileName = ...\global.config
                var globalConfProp = apiManagerType.GetProperty("GlobalConfigurationFileName", BindingFlags.Public | BindingFlags.Static);
                if (globalConfProp == null)
                    throw new MissingMemberException("APIManager.GlobalConfigurationFileName não encontrado");
                globalConfProp.SetValue(null, globalConfigPath, null);

                // apiManager = APIManager.Instance
                var instanceProp = apiManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp == null)
                    throw new MissingMemberException("APIManager.Instance não encontrado");
                var apiManager = instanceProp.GetValue(null, null);
                if (apiManager == null)
                    throw new Exception("APIManager.Instance retornou null");

                // apiExecutor = APIManager.Instance.GetApiExecutorByIdentifier(string.Empty, package)
                var getExec = apiManagerType.GetMethod("GetApiExecutorByIdentifier", BindingFlags.Public | BindingFlags.Instance);
                if (getExec == null)
                    throw new MissingMemberException("APIManager.GetApiExecutorByIdentifier não encontrado");
                var apiExecutor = getExec.Invoke(apiManager, new object[] { string.Empty, package });
                if (apiExecutor == null)
                    throw new Exception("GetApiExecutorByIdentifier retornou null (verifique package/global.config/licença)");

                // Ler input
                var inputContent = File.ReadAllText(inputFile, Encoding.UTF8);

                // Resolver mapper
                object mapper;
                if (!string.IsNullOrWhiteSpace(mapperId))
                {
                    var getMapperById = apiExecutorType.GetMethod("GetMapperByIdentifier", BindingFlags.Public | BindingFlags.Instance);
                    if (getMapperById == null)
                        throw new MissingMemberException("APIExecutor.GetMapperByIdentifier não encontrado");
                    mapper = getMapperById.Invoke(apiExecutor, new object[] { mapperId });
                }
                else
                {
                    var getMapperByName = apiExecutorType.GetMethod("GetMapperByName", BindingFlags.Public | BindingFlags.Instance);
                    if (getMapperByName == null)
                        throw new MissingMemberException("APIExecutor.GetMapperByName não encontrado");
                    mapper = getMapperByName.Invoke(apiExecutor, new object[] { mapperName });
                }

                if (mapper == null)
                    throw new Exception("Mapper não encontrado (id/nome).");

                // MapperBasicVO deve ter IdentifierGuid
                var mapperType = mapper.GetType();
                var idGuidProp = mapperType.GetProperty("IdentifierGuid", BindingFlags.Public | BindingFlags.Instance);
                if (idGuidProp == null)
                    throw new MissingMemberException("Mapper.IdentifierGuid não encontrado");
                var mapperGuid = idGuidProp.GetValue(mapper, null);

                // Executar mapper:
                // ExecuteMapper(Guid/string, string document, bool?, string fileName)
                // Existem overloads; vamos escolher o que tem 4 parâmetros e aceita (object,string,bool,string)
                var execMapperCandidates = apiExecutorType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                MethodInfo execMapper = null;
                foreach (var m in execMapperCandidates)
                {
                    if (!string.Equals(m.Name, "ExecuteMapper", StringComparison.Ordinal)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 4) continue;
                    if (ps[1].ParameterType != typeof(string)) continue;
                    if (ps[2].ParameterType != typeof(bool)) continue;
                    if (ps[3].ParameterType != typeof(string)) continue;
                    execMapper = m;
                    break;
                }
                if (execMapper == null)
                    throw new MissingMemberException("APIExecutor.ExecuteMapper (4 params) não encontrado");

                var mapperResult = execMapper.Invoke(apiExecutor, new object[] { mapperGuid, inputContent, true, fileName });
                if (mapperResult == null)
                    throw new Exception("ExecuteMapper retornou null");

                // MapperResultBasicVO.TransformedDocument
                var transformedProp = mapperResult.GetType().GetProperty("TransformedDocument", BindingFlags.Public | BindingFlags.Instance);
                if (transformedProp == null)
                    throw new MissingMemberException("MapperResult.TransformedDocument não encontrado");

                var transformed = transformedProp.GetValue(mapperResult, null) as string ?? "";
                File.WriteAllText(outputFile, transformed, Encoding.UTF8);

                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine(ex.ToString());
                }
                catch { }
                return 2;
            }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--")) continue;
                var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                d[key] = value;
            }
            return d;
        }

        private static string Require(Dictionary<string, string> args, string key)
        {
            var v = Get(args, key);
            if (string.IsNullOrWhiteSpace(v))
                throw new ArgumentException($"Argumento obrigatório ausente: {key}");
            return v;
        }

        private static string Get(Dictionary<string, string> args, string key)
            => args.TryGetValue(key, out var v) ? v : null;
    }
}