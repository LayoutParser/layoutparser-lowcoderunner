using System.Text;
using System.Net;
using LayoutParserLowCodeRunner.Service.Application;

// O worker falso herda variáveis de ambiente do processo de teste (FAKE_RUNNER_TRACK): sem paralelismo.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LayoutParserLowCodeRunner.Tests
{
    /// <summary>
    /// Contrato HTTP v1 contra o serviço REAL (host, fila, launcher, worker em processo filho) com o dublê
    /// FakeLowCodeRunner no lugar do SDK Sysmiddle.
    /// </summary>
    public class HttpContractTests
    {
        [Fact]
        public async Task Health_ok_e_correlation_id_e_espelhado()
        {
            using var t = new TestHost();
            var req = new HttpRequestMessage(HttpMethod.Get, "v1/health");
            req.Headers.Add("X-Correlation-ID", "corr-123");
            var r = await t.Http.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("corr-123", r.Headers.GetValues("X-Correlation-ID").Single());
            var h = await TestHost.Read<HealthResponse>(r);
            Assert.Equal("ok", h.Status);
            Assert.Equal("unchecked", h.License); // só o ?deep=true sobe o SDK
        }

        [Fact]
        public async Task Correlation_id_invalido_ou_ausente_gera_um_novo()
        {
            using var t = new TestHost();
            var req = new HttpRequestMessage(HttpMethod.Get, "v1/health");
            req.Headers.Add("X-Correlation-ID", "inv alido/../x");
            var r = await t.Http.SendAsync(req);

            var id = r.Headers.GetValues("X-Correlation-ID").Single();
            Assert.Matches("^[0-9a-f]{32}$", id);
        }

        [Fact]
        public async Task Health_deep_sobe_o_sdk_e_confirma_licenca()
        {
            using var t = new TestHost();
            var h = await TestHost.Read<HealthResponse>(await t.Http.GetAsync("v1/health?deep=true"));
            Assert.Equal("ok", h.Status);
            Assert.Equal("ok", h.License);
        }

        [Fact]
        public async Task Health_degradado_sem_package_devolve_503()
        {
            using var t = new TestHost(new() { ["Package"] = "" });
            var r = await t.Http.GetAsync("v1/health");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            var h = await TestHost.Read<HealthResponse>(r);
            Assert.Equal("degraded", h.Status);
            Assert.Equal("failed", h.Package);
        }

        [Fact]
        public async Task Info_expoe_slots_e_pacote()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "3" });
            var i = await TestHost.Read<InfoResponse>(await t.Http.GetAsync("v1/info"));

            Assert.Equal(3, i.MaxConcurrent);
            Assert.Equal("PKG", i.Package);
            Assert.Equal(0, i.Running);
            Assert.Equal(0, i.Queued);
            Assert.False(string.IsNullOrEmpty(i.Version));
        }

        [Fact]
        public async Task Transform_200_devolve_o_xml_do_worker()
        {
            using var t = new TestHost();
            var r = await t.Transform("conteudo", mapperId: "MAP_1", mapperName: null, fileName: "NFE.txt");

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await TestHost.Read<TransformResponse>(r);
            Assert.Contains("mapper=\"MAP_1\"", body.Output);
            Assert.Contains("file=\"NFE.txt\"", body.Output);
            Assert.Contains("chars=\"8\"", body.Output);
            Assert.Contains("nfe=\"false\"", body.Output);
            Assert.Equal("MAP_1", body.MapperId);
            Assert.True(body.DurationMs >= 0);
        }

        [Fact]
        public async Task Transform_repassa_nfePostProcessing()
        {
            using var t = new TestHost();
            var body = await TestHost.Read<TransformResponse>(await t.Transform("x", nfe: true));
            Assert.Contains("nfe=\"true\"", body.Output);
        }

        [Fact]
        public async Task Transform_usa_o_mapper_default_so_quando_nenhum_foi_informado()
        {
            using var t = new TestHost(new() { ["DefaultMapperName"] = "PADRAO" });
            var r = await t.Transform("x", mapperName: null);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("mapper=\"PADRAO\"", (await TestHost.Read<TransformResponse>(r)).Output);
        }

        [Fact]
        public async Task Transform_400_para_json_invalido()
        {
            using var t = new TestHost();
            var r = await t.PostJson("v1/transform", Encoding.UTF8.GetBytes("{nao e json"));

            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            var e = await TestHost.Read<ErrorResponse>(r);
            Assert.Equal(7, e.ExitCode);
            Assert.False(string.IsNullOrEmpty(e.CorrelationId));
        }

        [Theory]
        [InlineData("M1", "MAP_1")] // ambíguo: os dois
        [InlineData(null, null)]    // nenhum, sem DefaultMapperName
        public async Task Transform_400_para_mapper_ausente_ou_ambiguo(string name, string id)
        {
            using var t = new TestHost();
            var r = await t.Transform("x", mapperName: name, mapperId: id);

            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(7, (await TestHost.Read<ErrorResponse>(r)).ExitCode);
        }

        [Fact]
        public async Task Transform_404_quando_o_mapper_nao_resolve()
        {
            using var t = new TestHost();
            var r = await t.Transform("x", mapperName: "NAOEXISTE");

            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal(8, (await TestHost.Read<ErrorResponse>(r)).ExitCode);
        }

        [Theory]
        [InlineData("EMPTY", 5)]   // resultado vazio
        [InlineData("EXIT:1", 1)]  // falha de transformação
        [InlineData("EXIT:4", 4)]  // entrada não encontrada
        [InlineData("EXIT:10", 10)] // package não encontrado
        public async Task Transform_422_para_falhas_de_transformacao(string documento, int exitEsperado)
        {
            using var t = new TestHost();
            var r = await t.Transform(documento);

            Assert.Equal((HttpStatusCode)422, r.StatusCode);
            Assert.Equal(exitEsperado, (await TestHost.Read<ErrorResponse>(r)).ExitCode);
        }

        [Fact]
        public async Task Transform_422_para_documento_vazio_sem_subir_worker()
        {
            using var t = new TestHost();
            var r = await t.Transform("   ");

            Assert.Equal((HttpStatusCode)422, r.StatusCode);
            Assert.Empty(t.Track().Pids);
        }

        [Fact]
        public async Task Transform_422_exit9_quando_o_package_nao_esta_configurado()
        {
            using var t = new TestHost(new() { ["Package"] = "" });
            var r = await t.Transform("x");

            Assert.Equal((HttpStatusCode)422, r.StatusCode);
            Assert.Equal(9, (await TestHost.Read<ErrorResponse>(r)).ExitCode);
        }

        [Fact]
        public async Task Transform_503_para_falha_de_bootstrap_ou_licenca()
        {
            using var t = new TestHost();
            var r = await t.Transform("EXIT:3");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Equal(3, (await TestHost.Read<ErrorResponse>(r)).ExitCode);
        }

        [Fact]
        public async Task Transform_504_no_timeout_e_o_worker_e_morto()
        {
            using var t = new TestHost(new() { ["RunnerTimeoutSeconds"] = "1" });
            var r = await t.Transform("SLEEP:30000");

            Assert.Equal(HttpStatusCode.GatewayTimeout, r.StatusCode);
            var pid = Assert.Single(t.Track().Pids);
            await EsperarAsync(() => !TestHost.IsAlive(pid), "o worker nao foi morto no timeout");
        }

        [Fact]
        public async Task Fila_cheia_devolve_503_com_retry_after()
        {
            using var t = new TestHost(new() { ["MaxConcurrentRunners"] = "1", ["MaxQueue"] = "0" });
            var primeiro = t.Transform("SLEEP:2500");
            await EsperarAsync(() => t.Track().Pids.Count == 1, "o primeiro worker nao subiu");

            var r = await t.Transform("x");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.True(r.Headers.Contains("Retry-After"));
            Assert.Equal(HttpStatusCode.OK, (await primeiro).StatusCode);
        }

        [Fact]
        public async Task Corpo_acima_do_limite_devolve_413()
        {
            using var t = new TestHost(new() { ["MaxBodyBytes"] = "1024" });
            var r = await t.Transform(new string('a', 5000));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        }

        [Fact]
        public async Task Rota_desconhecida_404_e_metodo_errado_405()
        {
            using var t = new TestHost();
            Assert.Equal(HttpStatusCode.NotFound, (await t.Http.GetAsync("v1/nada")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await t.Http.GetAsync("v1/transform")).StatusCode);
        }

        [Fact]
        public async Task Mappers_lista_o_catalogo_do_package_e_usa_cache()
        {
            using var t = new TestHost();
            var r = await t.Http.GetAsync("v1/mappers");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var lista = await TestHost.Read<List<MapperItem>>(r);
            Assert.Equal(2, lista.Count);
            Assert.Equal("MAP_1", lista[0].Id);
            Assert.Equal("Mapper Um", lista[0].Name);

            await t.Http.GetAsync("v1/mappers");
            Assert.Single(t.Track().Pids); // segunda chamada veio do cache: um único worker
        }

        [Fact]
        public async Task Erros_nao_vazam_caminho_nem_conteudo_e_o_log_tambem_nao()
        {
            using var t = new TestHost();
            const string segredo = "SEGREDO-DO-CLIENTE-XYZ";

            var ok = await t.Transform(segredo);
            var falha = await t.Transform("EXIT:1 " + segredo);

            var corpoErro = await falha.Content.ReadAsStringAsync();
            Assert.DoesNotContain(segredo, corpoErro);
            Assert.DoesNotContain(@":\", corpoErro);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            var logs = t.AllLogs();
            Assert.NotEmpty(logs);
            Assert.DoesNotContain(segredo, logs);
            Assert.DoesNotContain(t.Root, logs); // sem caminhos internos
            Assert.DoesNotContain(" at ", logs);  // sem stack trace
        }

        [Fact]
        public async Task Pasta_temporaria_do_worker_e_limpa_apos_cada_execucao()
        {
            using var t = new TestHost();
            await t.Transform("x");
            await t.Transform("EXIT:1");

            var work = Path.Combine(t.Root, "work");
            Assert.Empty(Directory.GetDirectories(work));
        }

        /// <summary>
        /// Formato de erro: a API constrói o cliente a partir dele. Exatamente as chaves error (string),
        /// exitCode (número) e correlationId (string), em application/json, com o mesmo id no header — para
        /// TODO erro, inclusive os de protocolo (404/405/413) e os do worker (400/404/422/503/504).
        /// </summary>
        [Theory]
        [InlineData("POST", "v1/transform", "{nao e json", 400, 7)]
        [InlineData("POST", "v1/transform", "{\"document\":\"x\"}", 400, 7)]
        [InlineData("POST", "v1/transform", "{\"document\":\"x\",\"mapperName\":\"NAOEXISTE\"}", 404, 8)]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:1\",\"mapperName\":\"M\"}", 422, 1)]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:3\",\"mapperName\":\"M\"}", 503, 3)]
        [InlineData("POST", "v1/transform", "{\"document\":\"SLEEP:30000\",\"mapperName\":\"M\"}", 504, -1)]
        [InlineData("GET", "v1/nada", null, 404, 0)]
        [InlineData("GET", "v1/transform", null, 405, 0)]
        [InlineData("POST", "v1/transform", "corpo-grande", 413, 0)]
        public async Task Corpo_de_erro_tem_exatamente_as_quatro_chaves_do_contrato(
            string metodo, string rota, string corpo, int status, int exitCode)
        {
            using var t = new TestHost(new() { ["RunnerTimeoutSeconds"] = "1", ["MaxBodyBytes"] = "1024" });
            var req = new HttpRequestMessage(new HttpMethod(metodo), rota);
            if (corpo != null)
            {
                var texto = corpo == "corpo-grande" ? new string('a', 5000) : corpo;
                req.Content = new StringContent(texto, Encoding.UTF8, "application/json");
            }
            var r = await t.Http.SendAsync(req);
            var bruto = await r.Content.ReadAsStringAsync();

            Assert.Equal(status, (int)r.StatusCode);
            Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);

            using var doc = System.Text.Json.JsonDocument.Parse(bruto);
            var raiz = doc.RootElement;
            Assert.Equal(new[] { "code", "correlationId", "error", "exitCode" },
                raiz.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(System.Text.Json.JsonValueKind.String, raiz.GetProperty("error").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(raiz.GetProperty("error").GetString()));
            Assert.Equal(exitCode, raiz.GetProperty("exitCode").GetInt32());
            Assert.Equal(System.Text.Json.JsonValueKind.String, raiz.GetProperty("code").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(raiz.GetProperty("code").GetString()));
            Assert.Equal(r.Headers.GetValues("X-Correlation-ID").Single(), raiz.GetProperty("correlationId").GetString());
            Assert.DoesNotContain(@":\", bruto); // sem caminho
        }

        /// <summary>Campo estável <c>code</c> por situação (mapeamento único em ErrorCodes).</summary>
        [Theory]
        [InlineData("POST", "v1/transform", "{nao e json", 400, "invalid_request")]
        [InlineData("POST", "v1/transform", "{\"document\":\"x\"}", 400, "invalid_request")]
        [InlineData("POST", "v1/transform", "{\"document\":\"x\",\"mapperName\":\"NAOEXISTE\"}", 404, "mapper_not_found")]
        [InlineData("GET", "v1/nada", null, 404, "route_not_found")]
        [InlineData("GET", "v1/transform", null, 405, "invalid_request")]
        [InlineData("POST", "v1/transform", "corpo-grande", 413, "invalid_request")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:1\",\"mapperName\":\"M\"}", 422, "transform_failed")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EMPTY\",\"mapperName\":\"M\"}", 422, "empty_result")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:4\",\"mapperName\":\"M\"}", 422, "input_not_found")]
        [InlineData("POST", "v1/transform", "{\"document\":\"   \",\"mapperName\":\"M\"}", 422, "empty_document")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:10\",\"mapperName\":\"M\"}", 422, "package_not_found")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:3\",\"mapperName\":\"M\"}", 503, "runner_unavailable")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:2\",\"mapperName\":\"M\"}", 500, "runtime_error")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:6\",\"mapperName\":\"M\"}", 500, "runtime_error")]
        [InlineData("POST", "v1/transform", "{\"document\":\"EXIT:77\",\"mapperName\":\"M\"}", 500, "runtime_error")]
        [InlineData("POST", "v1/transform", "{\"document\":\"SLEEP:30000\",\"mapperName\":\"M\"}", 504, "timeout")]
        public async Task Erro_traz_o_code_estavel_por_situacao(string metodo, string rota, string corpo, int status, string code)
        {
            using var t = new TestHost(new() { ["RunnerTimeoutSeconds"] = "1", ["MaxBodyBytes"] = "1024" });
            var req = new HttpRequestMessage(new HttpMethod(metodo), rota);
            if (corpo != null)
                req.Content = new StringContent(corpo == "corpo-grande" ? new string('a', 5000) : corpo, Encoding.UTF8, "application/json");
            var r = await t.Http.SendAsync(req);

            Assert.Equal(status, (int)r.StatusCode);
            Assert.Equal(code, (await TestHost.Read<ErrorResponse>(r)).Code);
        }

        [Fact]
        public async Task Fila_cheia_tem_code_queue_full_e_sem_globalFolder_runner_unavailable()
        {
            using (var t = new TestHost(new() { ["MaxConcurrentRunners"] = "1", ["MaxQueue"] = "0" }))
            {
                var primeiro = t.Transform("SLEEP:2500");
                await EsperarAsync(() => t.Track().Pids.Count == 1, "o primeiro worker nao subiu");
                var r = await t.Transform("x");
                Assert.True(r.Headers.Contains("Retry-After"));
                Assert.Equal("queue_full", (await TestHost.Read<ErrorResponse>(r)).Code);
                await primeiro;
            }
            using (var t = new TestHost(new() { ["GlobalFolder"] = "" }))
            {
                var r = await t.Transform("x");
                Assert.False(r.Headers.Contains("Retry-After"));
                Assert.Equal("runner_unavailable", (await TestHost.Read<ErrorResponse>(r)).Code);
            }
        }

        [Fact]
        public async Task Chunked_411_tem_code_invalid_request()
        {
            using var t = new TestHost();
            var req = new HttpRequestMessage(HttpMethod.Post, "v1/transform");
            req.Headers.TransferEncodingChunked = true;
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var r = await t.Http.SendAsync(req);

            Assert.Equal(HttpStatusCode.LengthRequired, r.StatusCode);
            Assert.Equal("invalid_request", (await TestHost.Read<ErrorResponse>(r)).Code);
        }

        [Fact]
        public async Task Sucesso_do_transform_tem_o_formato_do_contrato()
        {
            using var t = new TestHost();
            var r = await t.Transform("x", mapperId: "MAP_1", mapperName: null);
            using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());

            Assert.Equal(new[] { "durationMs", "mapperId", "output", "warnings" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.GetProperty("warnings").ValueKind);
        }

        [Fact]
        public async Task Servico_sem_globalFolder_e_503_e_nao_um_400_enganoso()
        {
            using var t = new TestHost(new() { ["GlobalFolder"] = "" });
            var r = await t.Transform("x");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Empty(t.Track().Pids);
        }

        internal static async Task EsperarAsync(Func<bool> cond, string falha, int timeoutMs = 10000)
        {
            var fim = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!cond())
            {
                if (DateTime.UtcNow > fim) throw new Xunit.Sdk.XunitException(falha);
                await Task.Delay(50);
            }
        }
    }
}
