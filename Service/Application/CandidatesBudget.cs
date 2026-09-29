using System;

namespace LayoutParserLowCodeRunner.Service.Application
{
    /// <summary>
    /// Orçamento de tempo do batch multi-candidato. Separa "quanto o trabalho pode demorar" de "quanto o
    /// cliente espera": ondas = ceil(candidatos/slots); trabalho = ondas x timeout; efetivo =
    /// min(trabalho, tetoRequest), com tetoRequest=90 s quando inválido. Portado de LowCodeCandidatesBudget.
    /// </summary>
    public static class LowCodeCandidatesBudget
    {
        public const int DefaultRequestTimeoutSeconds = 90;

        public static LowCodeCandidatesBudgetResult Calculate(
            int multiCandidateTopN,
            int maxConcurrentRunners,
            int runnerTimeoutSeconds,
            int candidatesRequestTimeoutSeconds)
        {
            // Max(1, ...) trata config inválida como "pelo menos uma unidade": degradar, nunca derrubar.
            var slots = Math.Max(1, maxConcurrentRunners);
            var candidatos = Math.Max(1, multiCandidateTopN);
            var ondas = (int)Math.Ceiling(candidatos / (double)slots);

            var budgetTrabalho = ondas * Math.Max(1, runnerTimeoutSeconds);

            var tetoRequest = candidatesRequestTimeoutSeconds > 0
                ? candidatesRequestTimeoutSeconds
                : DefaultRequestTimeoutSeconds;

            return new LowCodeCandidatesBudgetResult
            {
                Ondas = ondas,
                BudgetTrabalhoSeconds = budgetTrabalho,
                TetoRequestSeconds = tetoRequest,
                EffectiveSeconds = Math.Min(budgetTrabalho, tetoRequest)
            };
        }
    }

    public class LowCodeCandidatesBudgetResult
    {
        public int Ondas { get; set; }
        public int BudgetTrabalhoSeconds { get; set; }
        public int TetoRequestSeconds { get; set; }
        public int EffectiveSeconds { get; set; }
    }
}
