#nullable disable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using LayoutParserLowCodeRunner.Service.Application;

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Log do runner: escreve SEMPRE no stderr (que a API captura e loga quando o exit != 0) e,
    /// quando --runnerLogFile é informado, também no arquivo — que a API lê de volta em
    /// LowCodeTransformationService para anexar ao seu próprio log de erro.
    ///
    /// <para>Formato: <c>{DateTime:O} [LVL] [Corr:xxx] mensagem</c> — o MESMO do RollingFileLogger
    /// do LayoutParserLib/LayoutParserDecrypt, que o UnifiedLogReaderService já parseia
    /// (SimpleLinePattern). Não é enfeite: torna o log do runner uma 5ª fonte do painel unificado
    /// sem exigir regex novo — basta registrar o arquivo lá quando se decidir por isso.</para>
    ///
    /// <para>Toda escrita é best effort: log NUNCA pode derrubar a transformação (princípio de
    /// degradação graciosa do projeto). Falha de I/O no arquivo é silenciosa — o stderr continua
    /// carregando a informação.</para>
    /// </summary>
    internal static class RunnerLog
    {
        // "-" e não vazio: mantém o grupo [Corr:...] sempre presente e parseável mesmo no uso
        // offline (posicional), onde não existe request nem correlationId.
        private static string _correlationId = "-";
        private static string _logFilePath;
        private static readonly object _fileLock = new object();

        /// <summary>
        /// Liga o log ao contexto da chamada. Chamado logo após o parse dos argumentos e antes de
        /// qualquer trabalho pesado, para que até as falhas de bootstrap já saiam correlacionadas.
        /// </summary>
        public static void Configure(string correlationId, string logFilePath)
        {
            if (!string.IsNullOrWhiteSpace(correlationId))
                _correlationId = correlationId;

            if (!string.IsNullOrWhiteSpace(logFilePath))
                _logFilePath = logFilePath;
        }

        public static void Info(string format, params object[] args) { Write("INF", format, args); }
        public static void Warn(string format, params object[] args) { Write("WRN", format, args); }
        public static void Error(string format, params object[] args) { Write("ERR", format, args); }
        public static void Fatal(string format, params object[] args) { Write("FTL", format, args); }

        private static void Write(string level, string format, object[] args)
        {
            string mensagem;
            try
            {
                mensagem = (args == null || args.Length == 0)
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                // Mensagem com chaves literais que não são placeholder ({0} ausente etc.): melhor
                // logar cru do que perder a linha inteira por um erro de formatação.
                mensagem = format;
            }

            string linha = string.Format(
                CultureInfo.InvariantCulture,
                "{0:O} [{1}] [Corr:{2}] {3}",
                DateTime.Now, level, _correlationId, mensagem);

            try { Console.Error.WriteLine(linha); } catch { }

            AppendToFile(linha);
        }

        private static void AppendToFile(string linha)
        {
            string caminho = _logFilePath;
            if (string.IsNullOrEmpty(caminho))
                return;

            // No arquivo (que o serviço mantém em LogDir) não ficam caminhos internos: só ids, tamanhos, tempos e
            // exit codes. O stderr do worker segue completo para o uso manual (o serviço o descarta).
            linha = LowCodeErrorSanitizer.ForWire(linha);

            try
            {
                // FileShare.ReadWrite: com LowCode:MaxConcurrentRunners > 1 há vários processos do
                // runner apontando pro MESMO arquivo. Sem o share, o segundo processo levaria
                // IOException a cada linha. Entrelaçamento de linhas é aceitável — cada linha carrega
                // seu próprio [Corr:...], que é o que permite separar as execuções depois.
                lock (_fileLock)
                {
                    using (var stream = new FileStream(caminho, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(linha);
                    }
                }
            }
            catch
            {
                // Silencioso de propósito: disco cheio / pasta inexistente / permissão negada não
                // podem abortar a transformação. O stderr já levou a informação pra API.
            }
        }
    }
}
