using System.Text.Json.Serialization;

namespace TiaCli.Protocol
{
    /// <summary>One Openness attribute of a hardware item, as text.</summary>
    public sealed class AttributeDto
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("value")] public string Value { get; set; }
    }
}
