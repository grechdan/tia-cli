using System.Text.Json;

namespace TiaCli.Protocol
{
    public static class JsonUtil
    {
        /// <summary>
        /// Snapshots any value as a JsonElement. Cloned, so it outlives the JsonDocument it came
        /// from - which is what lets WireResponse.Result hold an arbitrarily shaped payload.
        /// </summary>
        public static JsonElement ToElement(object value)
        {
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, WireJson.Options)))
                return doc.RootElement.Clone();
        }

        public static T To<T>(JsonElement? element)
        {
            if (!element.HasValue) return default(T);
            return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), WireJson.Options);
        }
    }
}
