using System.Text.Json.Serialization;

namespace TiaCli.Protocol
{
    /// <summary>A PLCSIM instance as it stands after being created and powered on.</summary>
    public sealed class SimulationInstanceDto
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("cpuType")] public string CpuType { get; set; }
        [JsonPropertyName("address")] public string Address { get; set; }
        [JsonPropertyName("operatingState")] public string OperatingState { get; set; }
        [JsonPropertyName("powerOnResult")] public string PowerOnResult { get; set; }
        [JsonPropertyName("api")] public string Api { get; set; }
    }
}
