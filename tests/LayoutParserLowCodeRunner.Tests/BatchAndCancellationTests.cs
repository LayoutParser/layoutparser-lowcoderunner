using System.Net;
using LayoutParserLowCodeRunner.Service.Application;

namespace LayoutParserLowCodeRunner.Tests
{
    /// <summary>Batch (ondas, orçamento, parcial), concorrência e cancelamento (slot devolvido) — dublê no lugar do SDK.</summary>
    public class BatchAndCancellationTests
    {
        [Fact]
        public async Task Batch_devolve_um_resultado_por_candidato_na_ordem_e_mede_ondas()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "2" });
            var r = await t.Batch("doc", new[] { "A", "B", "C" });

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var b = await TestHost.Read<BatchResponse>(r);
            Assert.Equal(2, b.Waves); // ceil(3/2)
            Assert.Equal(new[] { 0, 1, 2 }, b.Results.Select(x => x.Index).ToArray());
            Assert.All(b.Results, x => Assert.Equal("ok", x.Status));
            Assert.Equal(new[] { "A", "B", "C" }, b.Results.Select(x => x.MapperName).ToArray());
            Assert.Equal(3, b.Completed);
            Assert.False(b.Partial);
        }

        [Fact]
        public async Task Batch_200_mesmo_com_falha_por_candidato()
        {
            using var t = new TestHost();
            var b = await TestHost.Read<BatchResponse>(await t.Batch("doc", new[] { "OK1", "FAIL:9:x", "NAOEXISTE" }));

            Assert.Equal("ok", b.Results[0].Status);
            Assert.Equal("failed", b.Results[1].Status);
            Assert.Equal(9, b.Results[1].ExitCode);
            Assert.Equal("failed", b.Results[2].Status);
            Assert.Equal(8, b.Results[2].ExitCode);
            Assert.Null(b.Results[0].Code);
            Assert.Equal("package_not_configured", b.Results[1].Code);
            Assert.Equal("mapper_not_found", b.Results[2].Code);
            Assert.Equal(3, b.Completed);
            Assert.False(b.Partial);
        }

        [Fact]
        public async Task Batch_nunca_excede_o_limite_de_concorrencia()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "2" });
            var r = await t.Batch("SLEEP:400", new[] { "A", "B", "C", "D", "E" });

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var (maxOverlap, pids) = t.Track();
            Assert.Equal(5, pids.Count);
            Assert.True(maxOverlap <= 2, "sobreposicao de " + maxOverlap + " workers com limite 2");
            Assert.True(maxOverlap >= 2, "o limite nunca foi usado; teste inconclusivo");
        }

        [Fact]
        public async Task Batch_devolve_parcial_quando_o_orcamento_estoura_sem_descartar_o_que_ficou_pronto()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "1" });
            // 1 slot: A termina, B trava (é morto no orçamento), C nem chega a rodar (skipped)
            var r = await t.Batch("doc", new[] { "A", "SLOW:30000:b", "C" }, budgetSeconds: 3);

            var b = await TestHost.Read<BatchResponse>(r);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.True(b.Partial);
            Assert.Equal("ok", b.Results[0].Status);
            Assert.Contains("mapper=\"A\"", b.Results[0].Output);
            Assert.Equal("timeout", b.Results[1].Status);
            Assert.Equal("skipped", b.Results[2].Status);
            Assert.Null(b.Results[0].Code);
            Assert.Equal("timeout", b.Results[1].Code);
            Assert.Equal("not_executed", b.Results[2].Code);
            Assert.Equal(1, b.Completed);
            Assert.Equal(3, b.BudgetSeconds); // min(trabalho, teto do request)
        }

        [Fact]
        public async Task Batch_400_para_lista_vazia_e_para_candidato_ambiguo()
        {
            using var t = new TestHost();
            Assert.Equal(HttpStatusCode.BadRequest, (await t.Batch("doc", Array.Empty<string>())).StatusCode);

            var ambiguo = Json.Serialize(new BatchRequest
            {
                Document = "doc",
                Candidates = new List<CandidateRef> { new CandidateRef { MapperId = "MAP_1", MapperName = "M" } }
            });
            Assert.Equal(HttpStatusCode.BadRequest, (await t.PostJson("v1/transform/batch", ambiguo)).StatusCode);
        }

        [Fact]
        public async Task Batch_400_acima_de_MaxCandidates()
        {
            using var t = new TestHost(new() { ["MaxCandidates"] = "2" });
            Assert.Equal(HttpStatusCode.BadRequest, (await t.Batch("doc", new[] { "A", "B", "C" })).StatusCode);
        }

        [Fact]
        public async Task Cliente_que_desiste_mata_o_worker_e_devolve_o_slot()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "1", ["MaxQueue"] = "0" });
            using var cts = new CancellationTokenSource();

            var pendente = t.Transform("SLEEP:30000", ct: cts.Token);
            await HttpContractTests.EsperarAsync(() => t.Track().Pids.Count == 1, "o worker nao subiu");
            var pid = t.Track().Pids[0];

            cts.Cancel(); // fecha a conexão TCP
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendente);

            await HttpContractTests.EsperarAsync(() => !TestHost.IsAlive(pid), "o worker continuou vivo apos o cliente desistir", 8000);

            // Com 1 slot e fila 0, só passa se o slot foi devolvido (a devolução acontece logo após a morte do worker):
            await HttpContractTests.EsperarAsync(
                () => TestHost.Read<InfoResponse>(t.Http.GetAsync("v1/info").Result).Result.Running == 0,
                "slot nao devolvido apos o cliente desistir", 8000);
            var r = await t.Transform("x");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var info = await TestHost.Read<InfoResponse>(await t.Http.GetAsync("v1/info"));
            Assert.Equal(0, info.Running);
        }

        [Fact]
        public async Task Batch_cancelado_pelo_cliente_libera_todos_os_slots()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "1", ["MaxQueue"] = "0" });
            using var cts = new CancellationTokenSource();

            var body = Json.Serialize(new BatchRequest
            {
                Document = "doc",
                Candidates = new List<CandidateRef> { new CandidateRef { MapperName = "SLOW:30000:a" }, new CandidateRef { MapperName = "B" } }
            });
            var pendente = t.PostJson("v1/transform/batch", body, cts.Token);
            await HttpContractTests.EsperarAsync(() => t.Track().Pids.Count == 1, "o worker nao subiu");

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendente);

            await HttpContractTests.EsperarAsync(
                () => TestHost.Read<InfoResponse>(t.Http.GetAsync("v1/info").Result).Result.Running == 0,
                "slot nao devolvido apos cancelamento do batch", 8000);
            Assert.Equal(HttpStatusCode.OK, (await t.Transform("x")).StatusCode);
        }

        [Fact]
        public async Task Carga_leve_requisicoes_acima_do_limite_nao_vazam_processos_nem_arquivos()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "2", ["MaxQueue"] = "20" });
            var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => t.Transform("SLEEP:150")));

            Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var (maxOverlap, pids) = t.Track();
            Assert.Equal(8, pids.Count);
            Assert.True(maxOverlap <= 2, "sobreposicao de " + maxOverlap);
            Assert.All(pids, p => Assert.False(TestHost.IsAlive(p), "worker " + p + " vazou"));
            Assert.Empty(Directory.GetDirectories(Path.Combine(t.Root, "work")));
        }
    }
}
