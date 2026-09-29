using LayoutParserLowCodeRunner.Service.Application;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Tests
{
    public class ServiceOptionsTests
    {
        [Fact]
        public void Defaults_seguem_a_especificacao()
        {
            var o = ServiceOptions.Load(_ => null);

            Assert.Equal(180, o.RunnerTimeoutSeconds);
            Assert.Equal(2, o.MaxConcurrentRunners);
            Assert.Equal(20 * 1024 * 1024, o.MaxBodyBytes);
            Assert.False(o.NfePostProcessingDefault);
            Assert.Equal("http://localhost:5230/", o.ListenPrefix);
            Assert.Null(o.DefaultMapperName);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("abc")]
        public void Valor_invalido_cai_no_default_com_aviso(string valor)
        {
            var o = ServiceOptions.Load(k => k == "RunnerTimeoutSeconds" || k == "MaxConcurrentRunners" ? valor : null);

            Assert.Equal(180, o.RunnerTimeoutSeconds);
            Assert.Equal(2, o.MaxConcurrentRunners);
            Assert.Contains(o.Warnings, w => w.StartsWith("RunnerTimeoutSeconds"));
        }

        [Fact]
        public void Ambiente_tem_prioridade_sobre_o_arquivo_de_config()
        {
            var antesPkg = Environment.GetEnvironmentVariable(ServiceOptions.EnvPrefix + "Package");
            var antesGf = Environment.GetEnvironmentVariable(ServiceOptions.EnvPrefix + "GlobalFolder");
            Environment.SetEnvironmentVariable(ServiceOptions.EnvPrefix + "Package", "DO-AMBIENTE");
            Environment.SetEnvironmentVariable(ServiceOptions.EnvPrefix + "GlobalFolder", null);
            try
            {
                var o = ServiceOptions.Load(ServiceOptions.FromEnvironmentAndConfig(_ => "DO-ARQUIVO"));
                Assert.Equal("DO-AMBIENTE", o.Package);
                Assert.Equal("DO-ARQUIVO", o.GlobalFolder);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ServiceOptions.EnvPrefix + "Package", antesPkg);
                Environment.SetEnvironmentVariable(ServiceOptions.EnvPrefix + "GlobalFolder", antesGf);
            }
        }

        [Fact]
        public void Package_vazio_gera_aviso()
        {
            Assert.Contains(ServiceOptions.Load(_ => null).Warnings, w => w.Contains("Package"));
        }
    }

    public class ExitMapTests
    {
        [Theory]
        [InlineData(0, 200)]
        [InlineData(1, 422)]
        [InlineData(3, 503)]
        [InlineData(4, 422)]
        [InlineData(5, 422)]
        [InlineData(7, 400)]
        [InlineData(8, 404)]
        [InlineData(9, 422)]
        [InlineData(10, 422)]
        [InlineData(2, 500)]
        [InlineData(6, 500)]
        [InlineData(99, 500)]
        public void Exit_code_mapeia_para_o_status_do_contrato(int exit, int http)
        {
            Assert.Equal(http, ExitMap.Status(exit));
        }

        [Fact]
        public void Mensagens_do_wire_nunca_contem_caminho()
        {
            for (int i = 0; i <= 12; i++)
                Assert.DoesNotContain(@":\", ExitMap.Message(i));
        }
    }

    public class WorkerLauncherQuoteTests
    {
        [Theory]
        [InlineData("simples", "simples")]
        [InlineData("", "\"\"")]
        [InlineData("com espaco", "\"com espaco\"")]
        [InlineData(@"C:\pasta com espaco\arq.txt", "\"C:\\pasta com espaco\\arq.txt\"")]
        [InlineData(@"termina\", @"termina\")]
        [InlineData("diz \"oi\"", "\"diz \\\"oi\\\"\"")]
        public void Quote_segue_a_convencao_do_CommandLineToArgvW(string entrada, string esperado)
        {
            Assert.Equal(esperado, WorkerLauncher.Quote(entrada));
        }

        [Fact]
        public void Argumento_vazio_sobrevive_como_token_para_o_parser_do_worker()
        {
            // O runner aceita --package "" (valor vazio é legítimo); o parser nomeado precisa vê-lo.
            var linha = WorkerLauncher.BuildArguments("--package", "", "--inputFile", "a b");
            Assert.Equal("--package \"\" --inputFile \"a b\"", linha);
        }
    }

    public class MapperCatalogParseTests
    {
        [Fact]
        public void Parse_le_guid_tab_nome_e_ignora_ruido()
        {
            var lista = MapperCatalogService.Parse("MAP_1\tNome Um\r\nlinha sem tab\r\n\r\nMAP_2\tNome Dois\n");

            Assert.Equal(2, lista.Count);
            Assert.Equal("MAP_2", lista[1].Id);
            Assert.Equal("Nome Dois", lista[1].Name);
        }
    }

    public class ExecutionGateTests
    {
        [Fact]
        public async Task Cancelar_na_fila_nao_consome_slot()
        {
            var gate = new ExecutionGate(1, 5, 30);
            var primeiro = await gate.AcquireAsync(CancellationToken.None, true);

            using var cts = new CancellationTokenSource();
            var espera = gate.AcquireAsync(cts.Token, true);
            await Task.Delay(100);
            Assert.Equal(1, gate.Queued);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => espera);
            Assert.Equal(0, gate.Queued);

            primeiro.Dispose();
            using var proximo = await gate.AcquireAsync(CancellationToken.None, true); // slot livre de novo
            Assert.Equal(1, gate.Running);
        }

        [Fact]
        public async Task Fila_cheia_estoura_com_503_e_retry_after()
        {
            var gate = new ExecutionGate(1, 0, 30);
            using var _ = await gate.AcquireAsync(CancellationToken.None, true);

            var ex = await Assert.ThrowsAsync<ServiceException>(() => gate.AcquireAsync(CancellationToken.None, true));
            Assert.Equal(503, ex.Status);
            Assert.Equal(30, ex.RetryAfterSeconds);
        }

        [Fact]
        public async Task Batch_ignora_o_limite_da_fila()
        {
            var gate = new ExecutionGate(1, 0, 30);
            using var primeiro = await gate.AcquireAsync(CancellationToken.None, true);

            var espera = gate.AcquireAsync(CancellationToken.None, enforceQueueLimit: false);
            Assert.False(espera.IsCompleted);
            primeiro.Dispose();
            using var _ = await espera;
        }

        [Fact]
        public async Task Dispose_duplo_do_ticket_nao_libera_dois_slots()
        {
            var gate = new ExecutionGate(1, 5, 30);
            var t = await gate.AcquireAsync(CancellationToken.None, true);
            t.Dispose();
            t.Dispose();

            using var a = await gate.AcquireAsync(CancellationToken.None, true);
            var segundo = gate.AcquireAsync(CancellationToken.None, true);
            await Task.Delay(100);
            Assert.False(segundo.IsCompleted);
        }
    }

    public class DocumentRulesTests
    {
        [Theory]
        [InlineData("<a/>", true)]
        [InlineData("  <a/>  ", true)]
        [InlineData("<?xml version=\"1.0\"?><a/>", false)] // já tem declaração: a ById NÃO reescreve
        [InlineData("texto puro", false)]
        [InlineData("", false)]
        public void Declaracao_xml_so_entra_em_xml_sem_declaracao(string doc, bool esperado)
        {
            Assert.Equal(esperado, SysmiddleDocumentRules.DeveInserirDeclaracao(doc));
        }

        [Fact]
        public void Pos_processamento_nfe_desligado_devolve_o_documento_intacto()
        {
            const string doc = "<nfeProc><NFe><infNFe><infAdic><infCpl>a>b</infCpl></infAdic></infNFe></NFe></nfeProc>";
            Assert.Same(doc, SysmiddleDocumentRules.AplicarPosProcessamentoNFe(doc, false));
        }

        [Fact]
        public void Pos_processamento_nfe_ligado_escapa_infCpl()
        {
            const string doc = "<nfeProc><NFe><infNFe><infAdic><infCpl>a&gt;b</infCpl></infAdic></infNFe></NFe></nfeProc>";
            var r = SysmiddleDocumentRules.AplicarPosProcessamentoNFe(doc, true);
            Assert.Contains("infCpl", r);
        }
    }
}
