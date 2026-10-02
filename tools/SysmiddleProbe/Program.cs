// Probe descartável: prova, com o SDK Sysmiddle REAL, se um mesmo processo carrega e executa mais de um projeto
// (<PackageMappers>). Responde: (1) quais projetos o SDK carrega; (2) se GetApiExecutorByIdentifier devolve um
// executor para cada um (ou null: projeto fora do global.config / licença); (3) quantos mapeadores cada um tem e
// quanto leva o init; (4) se o mesmo MapperGuid existe em mais de um projeto; (5) opcionalmente, executa um mapper.
//
// Imprime SÓ guids de projeto/mapper, contagens e tempos. Nunca conteúdo de documento nem connection string.
//
// Uso (a partir da Bin Sysmiddle, onde o exe é copiado/rodado, ou com as DLLs no app base):
//   SysmiddleProbe.exe --globalFolder <pasta do global.config>
//   SysmiddleProbe.exe --globalFolder <pasta> --execute <packageGuid>:<mapperGuid> --document <arquivo> [--fileName x.txt]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SysMiddle.API;
using SysMiddle.Base.Model.API;
using SysMiddle.Base.Model.Structure;

namespace SysmiddleProbe
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string globalFolder = Arg(args, "--globalFolder");
            string execute = Arg(args, "--execute");
            string document = Arg(args, "--document");
            string fileName = Arg(args, "--fileName") ?? "documento.txt";

            if (string.IsNullOrWhiteSpace(globalFolder) || !File.Exists(Path.Combine(globalFolder, "global.config")))
            {
                Console.WriteLine("Uso: SysmiddleProbe.exe --globalFolder <pasta com global.config> [--execute <package>:<mapper> --document <arquivo> [--fileName x.txt]]");
                return 2;
            }

            int exit = 0;
            try { exit = Run(globalFolder, execute, document, fileName); }
            catch (Exception ex)
            {
                Console.WriteLine("FALHA no bootstrap: {0}: {1}", ex.GetType().Name, FirstLine(ex.Message));
                exit = 3;
            }
            // O SDK pode deixar threads vivas; o worker do runner também encerra explicitamente.
            Environment.Exit(exit);
            return exit;
        }

        private static int Run(string globalFolder, string execute, string document, string fileName)
        {
            var sw = Stopwatch.StartNew();

            // Mesmo bootstrap do worker do runner (Runner/SysmiddleMapperExecutor.cs).
            SysMiddle.Base.InstanceFactory.Instance.CreateType(
                typeof(SysMiddle.Base.Interface.ILicenseController),
                typeof(SysMiddle.ConnectUs.Core.Helper.General.LicenseController));
            APIManager.GlobalConfigurationFileName = Path.Combine(globalFolder, "global.config");

            var manager = APIManager.Instance;
            Console.WriteLine("APIManager.Instance: {0} ms", sw.ElapsedMilliseconds);

            // (1) Projetos que o SDK carrega.
            sw.Restart();
            List<ProjectVO> projects = manager.GetAvailableProjects();
            Console.WriteLine("GetAvailableProjects: {0} ms, {1} projeto(s)", sw.ElapsedMilliseconds, projects == null ? 0 : projects.Count);
            if (projects == null || projects.Count == 0)
            {
                Console.WriteLine("Nenhum projeto carregado: o global.config/instancia nao expoe projetos neste processo.");
                return 4;
            }

            // (2)+(3) Um executor por projeto, no MESMO processo.
            var guidsPorProjeto = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            int semExecutor = 0;
            Console.WriteLine();
            Console.WriteLine("projeto (IdentifierGuid)                 | nome | executor | init ms | mapeadores");
            foreach (var p in projects)
            {
                sw.Restart();
                APIExecutor executor = null;
                string erro = null;
                try { executor = manager.GetApiExecutorByIdentifier(string.Empty, p.IdentifierGuid); }
                catch (Exception ex) { erro = ex.GetType().Name + ": " + FirstLine(ex.Message); }
                long ms = sw.ElapsedMilliseconds;

                if (executor == null)
                {
                    semExecutor++;
                    Console.WriteLine("{0} | {1} | NULL{2} | {3} | -", p.IdentifierGuid, p.Name, erro == null ? "" : " (" + erro + ")", ms);
                    continue;
                }

                int count = -1;
                try
                {
                    var mappers = executor.GetMappers();
                    count = mappers == null ? 0 : mappers.Count;
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (mappers != null) foreach (var m in mappers.Values) set.Add(m.IdentifierGuid);
                    guidsPorProjeto[p.IdentifierGuid] = set;
                }
                catch (Exception ex) { erro = ex.GetType().Name + ": " + FirstLine(ex.Message); }
                Console.WriteLine("{0} | {1} | ok{2} | {3} | {4}", p.IdentifierGuid, p.Name, erro == null ? "" : " (GetMappers: " + erro + ")", ms, count);
            }

            // Reentrada: pedir de novo o primeiro projeto (cache do APIManager?).
            sw.Restart();
            var again = manager.GetApiExecutorByIdentifier(string.Empty, projects[0].IdentifierGuid);
            Console.WriteLine();
            Console.WriteLine("2a chamada do 1o projeto: {0} ms, executor {1}", sw.ElapsedMilliseconds, again == null ? "NULL" : "ok");

            // (4) MapperGuid repetido entre projetos.
            var visto = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int repetidos = 0;
            foreach (var kv in guidsPorProjeto)
                foreach (var g in kv.Value)
                {
                    string outro;
                    if (visto.TryGetValue(g, out outro) && !string.Equals(outro, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        repetidos++;
                        Console.WriteLine("MapperGuid repetido: {0} em {1} e {2}", g, outro, kv.Key);
                    }
                    else visto[g] = kv.Key;
                }
            Console.WriteLine();
            Console.WriteLine("Resumo: {0} projeto(s), {1} sem executor, {2} MapperGuid repetido(s) entre projetos.", projects.Count, semExecutor, repetidos);

            // (5) Execução opcional de UM mapper (prova licença + execução; imprime só tamanho e tempo).
            if (!string.IsNullOrWhiteSpace(execute))
            {
                var parts = execute.Split(':');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(document) || !File.Exists(document))
                {
                    Console.WriteLine("--execute exige <package>:<mapper> e --document <arquivo existente>.");
                    return 2;
                }
                sw.Restart();
                var ex = manager.GetApiExecutorByIdentifier(string.Empty, parts[0]);
                if (ex == null) { Console.WriteLine("Execute: executor NULL para o package informado."); return 4; }
                var result = ex.ExecuteMapper(parts[1], File.ReadAllText(document), true, fileName);
                Console.WriteLine("Execute: {0} ms, resultado {1}", sw.ElapsedMilliseconds, result == null ? "NULL" : "ok");
            }

            return semExecutor == 0 ? 0 : 1;
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }
    }
}
