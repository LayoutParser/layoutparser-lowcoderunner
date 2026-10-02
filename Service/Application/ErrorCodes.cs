namespace LayoutParserLowCodeRunner.Service.Application
{
    /// <summary>
    /// Códigos de erro ESTÁVEIS do contrato v1 (campo <c>code</c> do corpo de erro e dos resultados do batch).
    /// Único lugar do mapeamento: não duplicar strings/regras em Router, MiniHttpServer, TransformService etc.
    /// A mensagem (<c>error</c>) é texto humano e pode mudar; o <c>code</c> não.
    /// </summary>
    internal static class ErrorCodes
    {
        public const string InvalidRequest = "invalid_request";
        public const string MapperNotFound = "mapper_not_found";
        public const string RouteNotFound = "route_not_found";
        public const string TransformFailed = "transform_failed";
        public const string EmptyResult = "empty_result";
        /// <summary>Documento de entrada vazio, rejeitado pelo SERVIÇO antes de subir o worker (422, exitCode 4).</summary>
        public const string EmptyDocument = "empty_document";
        /// <summary>Exit 4 do WORKER: arquivo de entrada não encontrado.</summary>
        public const string InputNotFound = "input_not_found";
        public const string PackageNotConfigured = "package_not_configured";
        public const string PackageNotFound = "package_not_found";
        public const string Timeout = "timeout";
        public const string QueueFull = "queue_full";
        public const string RunnerUnavailable = "runner_unavailable";
        public const string RuntimeError = "runtime_error";
        public const string ClientClosedRequest = "client_closed_request";
        /// <summary>Só no batch: candidato nem chegou a executar (orçamento/cancelamento).</summary>
        public const string NotExecuted = "not_executed";

        /// <summary>Exit code do worker -> code. Exits 2, 6 e desconhecidos => runtime_error.</summary>
        public static string ForExit(int exitCode)
        {
            switch (exitCode)
            {
                case RunnerExitCodes.Fatal: return TransformFailed;
                case RunnerExitCodes.BootstrapFailed: return RunnerUnavailable;
                case RunnerExitCodes.InputNotFound: return InputNotFound;
                case RunnerExitCodes.EmptyResult: return EmptyResult;
                case RunnerExitCodes.InvalidNamedArgument: return InvalidRequest;
                case RunnerExitCodes.MapperNameUnresolved: return MapperNotFound;
                case RunnerExitCodes.PackageNotConfigured: return PackageNotConfigured;
                case RunnerExitCodes.PackageNotFound: return PackageNotFound;
                default: return RuntimeError;
            }
        }

        /// <summary>Erros de protocolo HTTP (sem exit code de worker) -> code, pelo status.</summary>
        public static string ForProtocolStatus(int status)
        {
            switch (status)
            {
                case 404: return RouteNotFound;
                case 499: return ClientClosedRequest;
                case 504: return Timeout;
                case 400:
                case 405:
                case 411:
                case 413:
                case 431: return InvalidRequest;
                default: return RuntimeError;
            }
        }
    }
}
