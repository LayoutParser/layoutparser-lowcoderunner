using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service.Application
{
    /// <summary>POST /v1/transform e POST /v1/transform/batch.</summary>
    internal sealed class TransformService
    {
        private readonly ServiceOptions _opt;
        private readonly LowCodeExecutor _exec;
        private readonly RollingLogger _log;

        public TransformService(ServiceOptions opt, LowCodeExecutor exec, RollingLogger log)
        {
            _opt = opt;
            _exec = exec;
            _log = log;
        }

        public async Task<TransformResponse> TransformAsync(TransformRequest req, string corr, CancellationToken ct)
        {
            if (req == null)
                throw new ServiceException(400, RunnerExitCodes.InvalidNamedArgument, "Body invalido.");

            var id = Blank(req.MapperId);
            var name = Blank(req.MapperName);
            if (id == null && name == null)
                name = _opt.DefaultMapperName; // só quando NENHUM foi informado
            if ((id == null) == (name == null))
                throw new ServiceException(400, RunnerExitCodes.InvalidNamedArgument,
                    "Informe exatamente um entre mapperId e mapperName.");

            if (string.IsNullOrWhiteSpace(req.Document))
                throw new ServiceException(422, RunnerExitCodes.InputNotFound, "Documento de entrada vazio.");

            _exec.EnsureConfigured();
            var o = await _exec.ExecuteAsync(req.Document, req.FileName, id, name,
                req.NfePostProcessing ?? _opt.NfePostProcessingDefault, corr, true, ct).ConfigureAwait(false);

            if (o.TimedOut || o.Cancelled || o.ExitCode != RunnerExitCodes.Ok)
                throw ExitMap.ToException(o);

            return new TransformResponse { Output = o.Output, DurationMs = o.DurationMs, MapperId = id };
        }

        public async Task<BatchResponse> BatchAsync(BatchRequest req, string corr, CancellationToken ct)
        {
            if (req == null || req.Candidates == null || req.Candidates.Count == 0)
                throw new ServiceException(400, RunnerExitCodes.InvalidNamedArgument, "Informe ao menos um candidato.");
            if (req.Candidates.Count > _opt.MaxCandidates)
                throw new ServiceException(400, RunnerExitCodes.InvalidNamedArgument,
                    "Candidatos demais (maximo " + _opt.MaxCandidates + ").");
            if (string.IsNullOrWhiteSpace(req.Document))
                throw new ServiceException(422, RunnerExitCodes.InputNotFound, "Documento de entrada vazio.");

            for (int i = 0; i < req.Candidates.Count; i++)
            {
                var c = req.Candidates[i];
                if (c == null || (Blank(c.MapperId) == null) == (Blank(c.MapperName) == null))
                    throw new ServiceException(400, RunnerExitCodes.InvalidNamedArgument,
                        "Candidato " + i + ": informe exatamente um entre mapperId e mapperName.");
            }

            _exec.EnsureConfigured();
            var budget = LowCodeCandidatesBudget.Calculate(
                req.Candidates.Count, _opt.MaxConcurrentRunners, _opt.RunnerTimeoutSeconds, req.BudgetSeconds ?? 0);
            _log.Info(corr, string.Format("batch candidates={0} waves={1} budget={2}s", req.Candidates.Count, budget.Ondas, budget.EffectiveSeconds));

            var nfe = req.NfePostProcessing ?? _opt.NfePostProcessingDefault;

            // ct = cliente desistiu; o CTS também estoura no orçamento. Nos dois casos o que já terminou é mantido.
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(budget.EffectiveSeconds));

                var tasks = req.Candidates
                    .Select((c, i) => RunCandidateAsync(i, c, req, nfe, corr, cts.Token))
                    .ToArray();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                var resp = new BatchResponse
                {
                    Results = results.ToList(),
                    Waves = budget.Ondas,
                    BudgetSeconds = budget.EffectiveSeconds,
                    Completed = results.Count(r => r.Status == "ok" || r.Status == "failed")
                };
                resp.Partial = results.Any(r => r.Status == "timeout" || r.Status == "skipped");
                return resp;
            }
        }

        private async Task<CandidateResult> RunCandidateAsync(
            int index, CandidateRef c, BatchRequest req, bool nfe, string corr, CancellationToken ct)
        {
            var id = Blank(c.MapperId);
            var name = Blank(c.MapperName);
            var res = new CandidateResult { Index = index, MapperId = id, MapperName = name };
            try
            {
                var o = await _exec.ExecuteAsync(req.Document, req.FileName, id, name, nfe, corr, false, ct).ConfigureAwait(false);
                res.DurationMs = o.DurationMs;

                if (o.Cancelled && !o.Started)
                {
                    res.Status = "skipped";
                }
                else if (o.TimedOut || o.Cancelled)
                {
                    res.Status = "timeout";
                    res.Error = "Tempo limite excedido.";
                }
                else if (o.ExitCode == RunnerExitCodes.Ok)
                {
                    res.Status = "ok";
                    res.Output = o.Output;
                    res.ExitCode = 0;
                }
                else
                {
                    res.Status = "failed";
                    res.ExitCode = o.ExitCode;
                    res.Error = LowCodeErrorSanitizer.ForWire(ExitMap.Message(o.ExitCode));
                }
            }
            catch (Exception ex)
            {
                // Falha por candidato nunca derruba o batch.
                _log.Error(corr, "candidato " + index + " falhou: " + ex.GetType().Name);
                res.Status = "failed";
                res.ExitCode = RunnerExitCodes.Fatal;
                res.Error = "Erro interno do runner.";
            }
            return res;
        }

        private static string Blank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
    }
}
