using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LayoutParserLowCodeRunner.Service.Application;
using LayoutParserLowCodeRunner.Service.Infra;

namespace LayoutParserLowCodeRunner.Service.Http
{
    /// <summary>
    /// Rotas do contrato v1 e mapeamento de erro. Todo erro sai como
    /// <c>{"error","exitCode","code","correlationId"}</c> (code = ErrorCodes, estável) com mensagem SANEADA (sem caminho/stack/documento).
    /// exitCode -1 = falha sem exit code de worker (timeout/cancelamento); 0 = nenhum.
    /// </summary>
    internal sealed class Router
    {
        private static readonly Regex ValidCorrelation = new Regex(@"^[A-Za-z0-9\-_.]{1,64}$", RegexOptions.Compiled);
        private readonly TransformService _transform;
        private readonly MapperCatalogService _catalog;
        private readonly HealthService _health;
        private readonly RollingLogger _log;

        public Router(TransformService transform, MapperCatalogService catalog, HealthService health, RollingLogger log)
        {
            _transform = transform;
            _catalog = catalog;
            _health = health;
            _log = log;
        }

        public async Task<HttpResponseData> HandleAsync(HttpRequestData req, CancellationToken clientGone)
        {
            string corr;
            if (!req.Headers.TryGetValue("X-Correlation-ID", out corr) || !ValidCorrelation.IsMatch(corr ?? ""))
                corr = Guid.NewGuid().ToString("N");

            HttpResponseData resp;
            try
            {
                resp = await Dispatch(req, corr, clientGone).ConfigureAwait(false);
            }
            catch (ServiceException se)
            {
                resp = Error(se.Status, se.ExitCode, se.Code, se.Message, corr);
                if (se.RetryAfterSeconds.HasValue)
                    resp.Headers["Retry-After"] = se.RetryAfterSeconds.Value.ToString();
                _log.Warn(corr, string.Format("{0} {1} -> {2} exit={3}", req.Method, req.Path, se.Status, se.ExitCode));
            }
            catch (OperationCanceledException)
            {
                resp = Error(499, -1, ErrorCodes.ClientClosedRequest, "Requisicao cancelada.", corr);
                _log.Warn(corr, req.Method + " " + req.Path + " cancelada pelo cliente");
            }
            catch (SerializationException)
            {
                resp = Error(400, RunnerExitCodes.InvalidNamedArgument, ErrorCodes.InvalidRequest, "JSON invalido.", corr);
            }
            catch (Exception ex)
            {
                // Só o TIPO no log: a mensagem pode conter caminho/conteúdo.
                _log.Error(corr, "erro interno: " + ex.GetType().Name);
                resp = Error(500, RunnerExitCodes.Fatal, ErrorCodes.RuntimeError, "Erro interno.", corr);
            }

            resp.Headers["X-Correlation-ID"] = corr;
            return resp;
        }

        private async Task<HttpResponseData> Dispatch(HttpRequestData req, string corr, CancellationToken ct)
        {
            var path = (req.Path ?? "").TrimEnd('/').ToLowerInvariant();
            var get = req.Method == "GET";
            var post = req.Method == "POST";

            switch (path)
            {
                case "/v1/health":
                    if (!get) return MethodNotAllowed(corr);
                    var hc = await _health.CheckAsync(corr, Flag(req, "deep"), ct).ConfigureAwait(false);
                    return Ok(hc.Key, hc.Value);

                case "/v1/info":
                    if (!get) return MethodNotAllowed(corr);
                    return Ok(200, _health.Info());

                case "/v1/mappers":
                    if (!get) return MethodNotAllowed(corr);
                    return Ok(200, await _catalog.GetAsync(corr, Flag(req, "refresh"), ct).ConfigureAwait(false));

                case "/v1/transform":
                    if (!post) return MethodNotAllowed(corr);
                    return Ok(200, await _transform.TransformAsync(Json.Deserialize<TransformRequest>(req.Body), corr, ct).ConfigureAwait(false));

                case "/v1/transform/batch":
                    if (!post) return MethodNotAllowed(corr);
                    return Ok(200, await _transform.BatchAsync(Json.Deserialize<BatchRequest>(req.Body), corr, ct).ConfigureAwait(false));

                default:
                    return Error(404, 0, ErrorCodes.RouteNotFound, "Rota nao encontrada.", corr);
            }
        }

        private static bool Flag(HttpRequestData req, string name)
        {
            foreach (var part in (req.Query ?? "").Split('&'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && string.Equals(kv[0], name, StringComparison.OrdinalIgnoreCase)
                    && (kv[1] == "1" || string.Equals(kv[1], "true", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private static HttpResponseData Ok<T>(int status, T body)
        {
            return new HttpResponseData { Status = status, Body = Json.Serialize(body) };
        }

        private static HttpResponseData MethodNotAllowed(string corr)
        {
            return Error(405, 0, ErrorCodes.InvalidRequest, "Metodo nao permitido.", corr);
        }

        private static HttpResponseData Error(int status, int exitCode, string code, string message, string corr)
        {
            return new HttpResponseData
            {
                Status = status,
                Body = Json.Serialize(new ErrorResponse
                {
                    Error = LowCodeErrorSanitizer.ForWire(message),
                    ExitCode = exitCode,
                    Code = code,
                    CorrelationId = corr
                })
            };
        }
    }
}
