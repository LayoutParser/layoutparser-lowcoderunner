using LayoutParserLowCodeRunner.Service.Application;

namespace LayoutParserLowCodeRunner.Tests
{
    /// <summary>
    /// Vazamento de caminho absoluto do servidor num <b>200 OK</b>
    /// (<c>spec-entrega-da-transformacao-no-parse.md</c> §3.1).
    ///
    /// <para>A mensagem do runner virava <c>ErrorMessage</c> do candidato e saía no payload de
    /// sucesso do parse, entregando a estrutura de diretórios do servidor para o cliente. Estes
    /// testes travam o filtro; os testes de endpoint travam o uso dele na borda.</para>
    /// </summary>
    public class ErrorSanitizerTests
    {
        [Theory]
        [InlineData(@"Runner nao gerou outputFile: C:\inetpub\wwwroot\layoutparser\api\out_ab12.xml. stdout=")]
        [InlineData(@"Access to the path 'C:\inetpub\wwwroot\layoutparser\MLData' is denied.")]
        [InlineData("Falha ao ler C:/inetpub/wwwroot/layoutparser/api/out.xml")]
        [InlineData(@"Nao foi possivel acessar \\SRVFIAT01\sysmiddle\globalfolder")]
        public void Caminho_absoluto_nao_sobrevive_ao_saneamento(string mensagem)
        {
            var saneada = LowCodeErrorSanitizer.ForWire(mensagem);

            Assert.NotNull(saneada);
            Assert.DoesNotContain(@"C:\", saneada!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:/", saneada!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\\SRV", saneada!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inetpub", saneada!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LowCodeErrorSanitizer.PathPlaceholder, saneada!);
        }

        /// <summary>
        /// O filtro remove caminho, não informação: a causa continua legível — é o que o usuário
        /// (e o suporte) precisa ver no payload. Sanear até virar "erro desconhecido" seria trocar
        /// um defeito por outro.
        /// </summary>
        [Fact]
        public void Mensagem_sem_caminho_passa_intacta()
        {
            const string mensagem = "Low-code runner falhou (exit=2, corr=abc123)";

            Assert.Equal(mensagem, LowCodeErrorSanitizer.ForWire(mensagem));
        }

        [Fact]
        public void Nulo_e_vazio_nao_viram_texto_inventado()
        {
            Assert.Null(LowCodeErrorSanitizer.ForWire((string?)null));
            Assert.Equal("", LowCodeErrorSanitizer.ForWire(""));
        }

        [Fact]
        public void Excecao_e_saneada_pela_mensagem()
        {
            var ex = new IOException(@"Could not find file 'C:\inetpub\wwwroot\layoutparser\MLData\x.xml'.");

            var saneada = LowCodeErrorSanitizer.ForWire(ex);

            Assert.DoesNotContain("inetpub", saneada!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Could not find file", saneada!);
        }
    }
}
