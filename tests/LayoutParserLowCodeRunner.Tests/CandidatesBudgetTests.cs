using LayoutParserLowCodeRunner.Service.Application;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Tests
{
    /// <summary>
    /// Trava o budget da request de multi-candidato. O que estes testes protegem não é a aritmética
    /// — é a separação entre "quanto o trabalho demora" e "quanto o cliente espera", que a fórmula
    /// antiga (<c>RunnerTimeoutSeconds * MaxConcurrentRunners</c>) confundia num número só.
    /// </summary>
    public class CandidatesBudgetTests
    {
        /// <summary>
        /// O caso que motivou a mudança: com o timeout do runner corrigido para 180s, a fórmula
        /// antiga dava 180*2 = 360s e o endpoint segurava o cliente HTTP por seis minutos.
        /// </summary>
        [Fact]
        public void Teto_de_request_limita_o_budget_quando_o_trabalho_e_mais_longo()
        {
            var budget = LowCodeCandidatesBudget.Calculate(
                multiCandidateTopN: 4,
                maxConcurrentRunners: 2,
                runnerTimeoutSeconds: 180,
                candidatesRequestTimeoutSeconds: 90);

            Assert.Equal(2, budget.Ondas);
            Assert.Equal(360, budget.BudgetTrabalhoSeconds);
            Assert.Equal(90, budget.EffectiveSeconds);
            Assert.NotEqual(360, budget.EffectiveSeconds);
        }

        /// <summary>
        /// A inversão que denunciava o erro da fórmula antiga: mais slots simultâneos precisam
        /// ENCURTAR a fila. Antes, dobrar MaxConcurrentRunners dobrava o teto de espera.
        /// </summary>
        [Fact]
        public void Mais_slots_simultaneos_reduzem_o_budget_de_trabalho_em_vez_de_aumentar()
        {
            var doisSlots = LowCodeCandidatesBudget.Calculate(4, 2, 180, candidatesRequestTimeoutSeconds: 9999);
            var quatroSlots = LowCodeCandidatesBudget.Calculate(4, 4, 180, candidatesRequestTimeoutSeconds: 9999);

            Assert.Equal(360, doisSlots.BudgetTrabalhoSeconds);
            Assert.Equal(180, quatroSlots.BudgetTrabalhoSeconds);
            Assert.True(quatroSlots.BudgetTrabalhoSeconds < doisSlots.BudgetTrabalhoSeconds);
        }

        /// <summary>
        /// Quando o trabalho cabe folgado no teto, vale o trabalho — o teto é limite, não piso.
        /// Sem isso, uma configuração enxuta esperaria o teto inteiro à toa.
        /// </summary>
        [Fact]
        public void Budget_de_trabalho_menor_que_o_teto_prevalece()
        {
            var budget = LowCodeCandidatesBudget.Calculate(
                multiCandidateTopN: 2,
                maxConcurrentRunners: 2,
                runnerTimeoutSeconds: 30,
                candidatesRequestTimeoutSeconds: 90);

            Assert.Equal(1, budget.Ondas);
            Assert.Equal(30, budget.BudgetTrabalhoSeconds);
            Assert.Equal(30, budget.EffectiveSeconds);
        }

        /// <summary>
        /// Candidatos que não dividem exatamente pelos slots exigem uma onda a mais — arredondar
        /// para baixo prometeria um tempo que a última onda não cumpre.
        /// </summary>
        [Theory]
        [InlineData(1, 2, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(3, 2, 2)]
        [InlineData(4, 2, 2)]
        [InlineData(5, 2, 3)]
        [InlineData(7, 3, 3)]
        public void Ondas_arredondam_para_cima(int topN, int slots, int ondasEsperadas)
        {
            var budget = LowCodeCandidatesBudget.Calculate(topN, slots, 10, 9999);

            Assert.Equal(ondasEsperadas, budget.Ondas);
        }

        /// <summary>
        /// Config inválida degrada, não derruba: zero em MaxConcurrentRunners dividiria por zero e
        /// zero no teto de request zeraria o budget (todo request morreria em 504 instantâneo).
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(-5, -5, -5, -5)]
        public void Config_invalida_nao_zera_nem_estoura_o_budget(int topN, int slots, int runnerTimeout, int tetoRequest)
        {
            var budget = LowCodeCandidatesBudget.Calculate(topN, slots, runnerTimeout, tetoRequest);

            Assert.Equal(1, budget.Ondas);
            Assert.True(budget.EffectiveSeconds > 0);
            Assert.Equal(LowCodeCandidatesBudget.DefaultRequestTimeoutSeconds, budget.TetoRequestSeconds);
            // O trabalho vale 1 onda x 1s (piso), então é ele que prevalece sobre o teto default.
            Assert.Equal(1, budget.EffectiveSeconds);
        }

        /// <summary>
        /// O default de produção precisa ser um número que o cliente HTTP aguenta. Trava o contrato
        /// de que os defaults do <c>ServiceOptions</c> não voltem a produzir minutos.
        /// </summary>
        [Fact]
        public void Defaults_de_producao_nao_seguram_o_cliente_por_minutos()
        {
            var opt = ServiceOptions.Load(_ => null);

            var budget = LowCodeCandidatesBudget.Calculate(
                4, // MultiCandidateTopN default da API
                opt.MaxConcurrentRunners,
                opt.RunnerTimeoutSeconds,
                0); // teto de request invalido => default de 90s

            Assert.True(budget.EffectiveSeconds <= 120,
                $"Budget efetivo de {budget.EffectiveSeconds}s excede o limite aceitável de espera do cliente.");
        }
    }
}
