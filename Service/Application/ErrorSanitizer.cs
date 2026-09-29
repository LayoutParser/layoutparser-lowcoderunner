using System;
using System.Text.RegularExpressions;

namespace LayoutParserLowCodeRunner.Service.Application
{
    /// <summary>
    /// Saneia mensagens de erro ANTES de irem para o wire: troca caminhos internos por
    /// <see cref="PathPlaceholder"/>. Portado de LowCodeErrorSanitizer (layoutparser-api), agora dono deste
    /// serviço. O detalhe fica no log; no wire vai a versão sem caminho.
    /// </summary>
    public static class LowCodeErrorSanitizer
    {
        public const string PathPlaceholder = "[caminho interno]";

        // Caminho absoluto do Windows (C:\..., C:/...) ou UNC (\\servidor\share\...).
        private static readonly Regex CaminhoAbsoluto = new Regex(
            @"(?:[A-Za-z]:[\\/]|\\\\)[^\s""'<>|]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string ForWire(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return message;

            return CaminhoAbsoluto.Replace(message, PathPlaceholder);
        }

        public static string ForWire(Exception ex)
        {
            return ForWire(ex == null ? null : ex.Message);
        }
    }
}
