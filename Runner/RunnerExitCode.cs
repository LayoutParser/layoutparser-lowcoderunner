using System;

namespace LayoutParserLowCodeRunner.Runner
{
    // Exit codes do CLI (0 = sucesso). No modo HTTP cada código vira um status via HttpStatus().
    public enum RunnerExitCode
    {
        Success = 0,
        Unexpected = 2,          // 500
        InvalidArguments = 3,    // 400
        ConfigurationInvalid = 4,// 503 sysmiddleDir/global.config inválidos
        SysmiddleLoadFailed = 5, // 503 DLL/tipos/membros do Sysmiddle não carregam
        PackageNotConfigured = 6,// 503 package vazio na configuração
        PackageNotFound = 7,     // 404
        MapperNotFound = 8,      // 404
        TransformFailed = 9,     // 422
        Timeout = 10             // 504
    }

    public static class RunnerExitCodeExtensions
    {
        public static int HttpStatus(this RunnerExitCode code)
        {
            switch (code)
            {
                case RunnerExitCode.Success: return 200;
                case RunnerExitCode.InvalidArguments: return 400;
                case RunnerExitCode.PackageNotFound:
                case RunnerExitCode.MapperNotFound: return 404;
                case RunnerExitCode.TransformFailed: return 422;
                case RunnerExitCode.ConfigurationInvalid:
                case RunnerExitCode.SysmiddleLoadFailed:
                case RunnerExitCode.PackageNotConfigured: return 503;
                case RunnerExitCode.Timeout: return 504;
                default: return 500;
            }
        }
    }

    public sealed class RunnerException : Exception
    {
        public RunnerExitCode Code { get; }

        public RunnerException(RunnerExitCode code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }
    }
}
