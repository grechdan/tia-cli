using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaCli.Protocol
{
    /// <summary>
    /// One request. The same shape crosses the daemon pipe and is used in-process by the direct
    /// executor, so a command never has to know which of the two is answering it.
    /// </summary>
    public sealed class WireRequest
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("method")] public string Method { get; set; }
        [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    }

    public sealed class WireResponse
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("result")] public JsonElement? Result { get; set; }
        [JsonPropertyName("error")] public WireError Error { get; set; }
    }

    public sealed class WireError
    {
        [JsonPropertyName("code")] public string Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("detail")] public string Detail { get; set; }
        [JsonPropertyName("hint")] public string Hint { get; set; }
    }

    /// <summary>Stable error codes. These pick the process exit code, so they are part of the contract.</summary>
    public static class WireErrorCodes
    {
        public const string NotConnected = "not_connected";
        public const string NoProjectOpen = "no_project_open";
        public const string Ambiguous = "ambiguous";
        public const string NotFound = "not_found";
        public const string AccessDenied = "access_denied";
        public const string LicenseMissing = "license_missing";
        public const string PortalUnrecoverable = "portal_unrecoverable";
        public const string InvalidRequest = "invalid_request";
        public const string OpennessError = "openness_error";
        public const string DaemonError = "daemon_error";
        public const string Internal = "internal";
    }

    public static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Newline-delimited framing: a literal newline in the payload would desync the reader.
            WriteIndented = false,
        };

        /// <summary>Indented variant, used only for what --json prints to the user.</summary>
        public static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
    }
}
