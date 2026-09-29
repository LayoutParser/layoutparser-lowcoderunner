// Nullable desligado explicitamente: este arquivo é compilado nos DOIS lados — no runner
// (net481, <Nullable>disable</Nullable>) e, via <Compile Include ... Link>, no projeto de testes
// (net10.0, <Nullable>enable</Nullable>). Fixar aqui garante semântica idêntica nas duas compilações.
#nullable disable

using System;
using System.IO;
using System.Xml;

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// Regras de documento portadas do <c>appConnector.Client.Core.Util.MappersHelper</c> —
    /// a parte PURA (só string/XML, sem SDK Sysmiddle), justamente para poder ser travada por teste.
    ///
    /// <para><b>Por que isto existe num arquivo próprio:</b> as duas funções do MappersHelper
    /// <b>não são equivalentes</b>, e a diferença mora aqui. O caminho vivo — o que produziu o
    /// gabarito byte a byte de <c>.claude/tmp/exemplos/</c> — é <c>ExecuteMappingDocumentById</c>;
    /// o port antigo deste runner replicava <c>ExecuteMappingDocument</c>, que diverge em três
    /// pontos (verificados no decompilado, ver docs/architecture/decisao-remover-dependencia-appconnector.md §3):</para>
    ///
    /// <list type="number">
    ///   <item><description><c>InsertDeclaration</c>: a <c>ById</c> só aplica se <b>não</b> começar
    ///   com <c>&lt;?xml</c>; o port aplicava sempre (e reescrevia a declaração existente).</description></item>
    ///   <item><description><c>ExecuteParser("", document)</c>: a <c>ById</c> chama (descartando o
    ///   resultado); o port não chamava. Fica no executor, não aqui — depende do SDK.</description></item>
    ///   <item><description>Pós-processamento NF-e: a <c>ById</c> <b>não</b> aplica; o port aplicava
    ///   os três. Aqui ele existe, mas <b>desligado por padrão</b>.</description></item>
    /// </list>
    ///
    /// <para>Só o <see cref="SysmiddleDocumentRules"/> pode ser exercitado na suíte: o
    /// <c>SysmiddleMapperExecutor</c> depende das DLLs x86 do Sysmiddle e não compila em net10.0.
    /// Por isso a divergência de §3 vira invariante travada em teste, e não comentário.</para>
    /// </summary>
    internal static class SysmiddleDocumentRules
    {
        /// <summary>
        /// Insere a declaração XML no documento <b>quando a regra da <c>ExecuteMappingDocumentById</c> manda</b>:
        /// documento parece XML (começa com <c>&lt;</c> e termina com <c>&gt;</c>) <b>e não</b> já começa
        /// com <c>&lt;?xml</c>.
        ///
        /// <para>A exclusão do <c>&lt;?xml</c> é o ponto 1 da divergência: sem ela (comportamento da
        /// <c>ExecuteMappingDocument</c>), um documento que JÁ tem declaração teria a sua reescrita
        /// para <c>encoding="utf-8"</c>, mudando o que entra no mapeador.</para>
        /// </summary>
        public static string AplicarDeclaracaoXmlSeNecessario(string document)
        {
            if (!DeveInserirDeclaracao(document))
                return document;

            return InsertDeclaration(document);
        }

        /// <summary>
        /// Predicado da <c>ExecuteMappingDocumentById</c>, isolado para ficar testável.
        /// O <c>document != null</c> é guarda defensiva: no original um documento nulo estouraria
        /// NullReference (e seria engolido pelo catch); o runner nunca passa nulo (vem de
        /// <c>File.ReadAllText</c>), então a guarda é inalcançável na prática e não altera o
        /// caminho feliz.
        /// </summary>
        public static bool DeveInserirDeclaracao(string document)
        {
            if (document == null)
                return false;

            string aparado = document.Trim();

            return aparado.StartsWith("<", StringComparison.Ordinal)
                && aparado.EndsWith(">", StringComparison.Ordinal)
                && !aparado.StartsWith("<?xml", StringComparison.Ordinal);
        }

        /// <summary>
        /// Cópia literal do <c>MappersHelper.InsertDeclaration</c>: remove tudo até o primeiro
        /// <c>?&gt;</c> (se houver) e prefixa a declaração utf-8.
        /// </summary>
        public static string InsertDeclaration(string document)
        {
            if (document.Contains("?>"))
                document = document.Remove(0, document.IndexOf("?>", StringComparison.Ordinal) + 2);

            return document.Insert(0, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        }

        /// <summary>
        /// Pós-processamento NF-e (escape de <c>&lt;</c>/<c>&gt;</c> em infCpl, infAdFisco e infAdProd).
        ///
        /// <para><b>Desligado por padrão de propósito.</b> A <c>ExecuteMappingDocumentById</c> — o
        /// caminho vivo — <b>não</b> aplica nenhum dos três. Mesmo quando nenhum nó casa, os três
        /// passam o documento por <c>XmlDocument.LoadXml</c> + <c>XmlWriter</c>, o que RESERIALIZA o
        /// XML e altera bytes.</para>
        ///
        /// <para>⚠️ <b>Medido em 2026-08-10, e o resultado é contraintuitivo:</b> no par real de
        /// <c>.claude/tmp/exemplos/</c>, ligar a flag faz a saída bater com o gabarito
        /// <b>exatamente</b> — 4245 bytes, <c>cmp</c> limpo, sem precisar tolerar nada. Com a flag
        /// desligada saem 4246 bytes e a única diferença é o espaço duplo em <c>&lt;?xml  version=</c>,
        /// que a reserialização normaliza. Os escapes NF-e em si não mudam nada neste documento
        /// (nenhum infCpl/infAdFisco/infAdProd casa): o que muda é só o round-trip.</para>
        ///
        /// <para>Isso <b>não</b> autoriza inverter o default. O gabarito saiu pelo pipeline de saída
        /// do host (o nome do arquivo, <c>...-11072026094950273-env.xml</c>, é a nomenclatura do
        /// connector), então a normalização da declaração pode ter vindo de lá e não do mapeador — e
        /// num documento que TENHA infCpl/infAdFisco/infAdProd os três escapes mudam conteúdo de
        /// verdade, sem evidência que sustente. Inverter o default é decisão de arquitetura, com
        /// gabarito novo que a justifique.</para>
        /// </summary>
        public static string AplicarPosProcessamentoNFe(string document, bool ativo)
        {
            if (!ativo || string.IsNullOrEmpty(document))
                return document;

            document = ChangeInfCplValues(document);
            document = ChangeInfIdFiscoValues(document);
            return ChangeInfAdProdValues(document);
        }

        private static string ChangeInfCplValues(string document)
        {
            return EscaparNo(document, "descendant::nfeProc/NFe/infNFe/infAdic/infCpl");
        }

        private static string ChangeInfIdFiscoValues(string document)
        {
            return EscaparNo(document, "descendant::nfeProc/NFe/infNFe/infAdic/infAdFisco");
        }

        private static string ChangeInfAdProdValues(string document)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(document);

                int total = 0;
                if (xml.DocumentElement != null)
                {
                    var nos = xml.DocumentElement.SelectNodes("/nfeProc/NFe/infNFe/det");
                    if (nos != null)
                        total = nos.Count;
                }

                for (int i = 1; i <= total; i++)
                {
                    var no = xml.SelectSingleNode("nfeProc/NFe/infNFe/det[@nItem=" + i + "]/infAdProd");
                    if (no != null)
                        no.InnerText = no.InnerText.Replace(">", "&gt;").Replace("<", "&lt;");
                }

                return Serializar(xml, document);
            }
            catch
            {
                // Degradação graciosa (igual ao original): documento que não é XML válido passa reto.
                return document;
            }
        }

        private static string EscaparNo(string document, string xpath)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(document);

                var no = xml.SelectSingleNode(xpath);
                if (no != null)
                    no.InnerText = no.InnerText.Replace(">", "&gt;").Replace("<", "&lt;");

                return Serializar(xml, document);
            }
            catch
            {
                return document;
            }
        }

        private static string Serializar(XmlDocument xml, string fallback)
        {
            try
            {
                using (var sw = new StringWriter())
                using (var xw = XmlWriter.Create(sw))
                {
                    xml.WriteTo(xw);
                    xw.Flush();
                    return sw.GetStringBuilder().ToString();
                }
            }
            catch
            {
                return fallback;
            }
        }
    }
}
