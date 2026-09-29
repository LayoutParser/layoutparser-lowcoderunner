// Dublê do worker LowCode para os testes de contrato do serviço HTTP. Imita SÓ o contrato de processo:
// argumentos (forma nomeada e posicional LIST), arquivo de saída, exit code, stdout do LIST e log. Não
// reimplementa nenhuma lógica Sysmiddle. Baseado em tools/FakeLowCodeRunner da API (mesmos exit codes).
//
// O cenário de cada execução vem do PRIMEIRO TOKEN do documento de entrada:
//   SLEEP:<ms>   dorme e depois responde sucesso (concorrência/timeout/cancelamento)
//   EXIT:<n>     sai com o exit code n sem escrever saída
//   EMPTY        exit 5 (EmptyResult)
//   (outro)      sucesso: <fake mapper="..." file="..." chars="N" nfe="true|false"/>
// Variáveis de ambiente (opcionais):
//   FAKE_RUNNER_TRACK=<arquivo>   registra "S <pid> <ticks>" / "E <pid> <ticks>" (medir sobreposição e pids)
//   FAKE_RUNNER_MAPPERS="id1|Nome 1;id2|Nome 2"  catálogo do LIST
//   FAKE_RUNNER_LIST_EXIT=<n>     exit code do LIST
//   FAKE_RUNNER_NOPACKAGE=1       (padrão) package vazio => exit 9, como o worker real
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FakeLowCodeRunner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var pid = Process.GetCurrentProcess().Id;
            Track("S", pid);
            try { return Run(args); }
            finally { Track("E", pid); }
        }

        private static int Run(string[] args)
        {
            if (args.Length >= 5 && !args[0].StartsWith("--", StringComparison.Ordinal) && string.Equals(args[2], "LIST", StringComparison.OrdinalIgnoreCase))
                return List(args[1]);

            string input = null, output = null, package = null, mapperId = null, mapperName = null, fileName = null, nfe = "false";
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var name = args[i].Substring(2);
                var v = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
                switch (name)
                {
                    case "inputFile": input = v; break;
                    case "outputFile": output = v; break;
                    case "package": package = v; break;
                    case "mapperId": mapperId = v; break;
                    case "mapperName": mapperName = v; break;
                    case "fileName": fileName = v; break;
                    case "nfePostProcessing": nfe = v; break;
                }
            }

            if (string.IsNullOrWhiteSpace(package)) return 9;
            if (input == null || output == null || (mapperId == null && mapperName == null)) return 7;
            if (!File.Exists(input)) return 4;

            var doc = File.ReadAllText(input);
            if (doc.StartsWith("SLEEP:", StringComparison.Ordinal))
            {
                var end = doc.IndexOfAny(new[] { ' ', '\r', '\n' });
                var ms = int.Parse((end < 0 ? doc : doc.Substring(0, end)).Substring(6));
                Thread.Sleep(ms);
            }
            else if (doc.StartsWith("EXIT:", StringComparison.Ordinal))
            {
                var end = doc.IndexOfAny(new[] { ' ', '\r', '\n' });
                return int.Parse((end < 0 ? doc : doc.Substring(0, end)).Substring(5));
            }
            else if (doc.StartsWith("EMPTY", StringComparison.Ordinal))
            {
                File.WriteAllText(output, "");
                return 5;
            }

            if (mapperName == "NAOEXISTE" || mapperId == "MAP_NAOEXISTE") return 8;

            // Cenário por mapeador (batch): "SLOW:<ms>:x" dorme; "FAIL:<n>:x" sai com n.
            var mapper = mapperId ?? mapperName ?? "";
            if (mapper.StartsWith("SLOW:", StringComparison.Ordinal))
                Thread.Sleep(int.Parse(mapper.Split(':')[1]));
            else if (mapper.StartsWith("FAIL:", StringComparison.Ordinal))
                return int.Parse(mapper.Split(':')[1]);

            File.WriteAllText(output,
                "<fake mapper=\"" + (mapperId ?? mapperName) + "\" file=\"" + fileName + "\" chars=\"" + doc.Length + "\" nfe=\"" + nfe + "\"/>",
                new UTF8Encoding(false));
            return 0;
        }

        private static int List(string package)
        {
            var listExit = Environment.GetEnvironmentVariable("FAKE_RUNNER_LIST_EXIT");
            if (!string.IsNullOrEmpty(listExit)) return int.Parse(listExit);
            if (string.IsNullOrWhiteSpace(package)) return 9;

            var mappers = Environment.GetEnvironmentVariable("FAKE_RUNNER_MAPPERS") ?? "MAP_1|Mapper Um;MAP_2|Mapper Dois";
            foreach (var m in mappers.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = m.Split('|');
                Console.Out.WriteLine("{0}\t{1}", p[0], p.Length > 1 ? p[1] : p[0]);
            }
            return 0;
        }

        private static void Track(string kind, int pid)
        {
            var file = Environment.GetEnvironmentVariable("FAKE_RUNNER_TRACK");
            if (string.IsNullOrEmpty(file)) return;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var w = new StreamWriter(fs))
                        w.WriteLine("{0} {1} {2}", kind, pid, DateTime.UtcNow.Ticks);
                    return;
                }
                catch (IOException) { Thread.Sleep(10); }
            }
        }
    }
}
