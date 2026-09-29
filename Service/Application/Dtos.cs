using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LayoutParserLowCodeRunner.Service.Application
{
    // Contrato HTTP v1 (JSON UTF-8, camelCase via DataMember). DataContractJsonSerializer: sem dependência
    // externa (a Bin do Sysmiddle traz o próprio Newtonsoft.Json e versões conflitariam).

    [DataContract]
    public class TransformRequest
    {
        [DataMember(Name = "document")] public string Document { get; set; }
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        [DataMember(Name = "mapperId")] public string MapperId { get; set; }
        [DataMember(Name = "mapperName")] public string MapperName { get; set; }
        [DataMember(Name = "nfePostProcessing")] public bool? NfePostProcessing { get; set; }
    }

    [DataContract]
    public class TransformResponse
    {
        [DataMember(Name = "output")] public string Output { get; set; }
        [DataMember(Name = "warnings")] public List<string> Warnings { get; set; } = new List<string>();
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
        [DataMember(Name = "mapperId", EmitDefaultValue = false)] public string MapperId { get; set; }
    }

    [DataContract]
    public class CandidateRef
    {
        [DataMember(Name = "mapperId")] public string MapperId { get; set; }
        [DataMember(Name = "mapperName")] public string MapperName { get; set; }
    }

    [DataContract]
    public class BatchRequest
    {
        [DataMember(Name = "document")] public string Document { get; set; }
        [DataMember(Name = "fileName")] public string FileName { get; set; }
        [DataMember(Name = "candidates")] public List<CandidateRef> Candidates { get; set; }
        [DataMember(Name = "nfePostProcessing")] public bool? NfePostProcessing { get; set; }
        [DataMember(Name = "budgetSeconds")] public int? BudgetSeconds { get; set; }
    }

    [DataContract]
    public class CandidateResult
    {
        [DataMember(Name = "index")] public int Index { get; set; }
        [DataMember(Name = "mapperId", EmitDefaultValue = false)] public string MapperId { get; set; }
        [DataMember(Name = "mapperName", EmitDefaultValue = false)] public string MapperName { get; set; }
        /// <summary>ok | failed | timeout | skipped</summary>
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "output", EmitDefaultValue = false)] public string Output { get; set; }
        [DataMember(Name = "error", EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(Name = "exitCode", EmitDefaultValue = false)] public int? ExitCode { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
    }

    [DataContract]
    public class BatchResponse
    {
        [DataMember(Name = "results")] public List<CandidateResult> Results { get; set; } = new List<CandidateResult>();
        [DataMember(Name = "waves")] public int Waves { get; set; }
        [DataMember(Name = "budgetSeconds")] public int BudgetSeconds { get; set; }
        [DataMember(Name = "completed")] public int Completed { get; set; }
        [DataMember(Name = "partial")] public bool Partial { get; set; }
    }

    [DataContract]
    public class MapperItem
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
    }

    [DataContract]
    public class ErrorResponse
    {
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "correlationId")] public string CorrelationId { get; set; }
    }

    [DataContract]
    public class HealthResponse
    {
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "license")] public string License { get; set; }
        [DataMember(Name = "package")] public string Package { get; set; }
        [DataMember(Name = "globalFolder")] public string GlobalFolder { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string Reason { get; set; }
    }

    [DataContract]
    public class InfoResponse
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "build")] public string Build { get; set; }
        [DataMember(Name = "x86")] public bool X86 { get; set; }
        [DataMember(Name = "uptimeSeconds")] public long UptimeSeconds { get; set; }
        [DataMember(Name = "package")] public string Package { get; set; }
        [DataMember(Name = "maxConcurrent")] public int MaxConcurrent { get; set; }
        [DataMember(Name = "running")] public int Running { get; set; }
        [DataMember(Name = "queued")] public int Queued { get; set; }
    }

    internal static class Json
    {
        public static byte[] Serialize<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] body) where T : class
        {
            using (var ms = new MemoryStream(body))
            {
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
            }
        }
    }
}
