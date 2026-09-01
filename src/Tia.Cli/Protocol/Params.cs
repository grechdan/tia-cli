using System.Text.Json;

namespace TiaCli.Protocol
{
    /// <summary>Typed access to a request's params object.</summary>
    public readonly struct Params
    {
        private readonly JsonElement? _element;

        public Params(JsonElement? element) { _element = element; }

        private bool TryGet(string name, out JsonElement value)
        {
            value = default;
            return _element.HasValue
                   && _element.Value.ValueKind == JsonValueKind.Object
                   && _element.Value.TryGetProperty(name, out value)
                   && value.ValueKind != JsonValueKind.Null;
        }

        public string String(string name) =>
            TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public string RequiredString(string name)
        {
            var value = String(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new WireException(WireErrorCodes.InvalidRequest, $"Parameter '{name}' is required.");
            return value;
        }

        public bool Bool(string name, bool fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        public int Int(string name, int fallback) =>
            TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i : fallback;

        /// <summary>Null when the caller said nothing, which is different from saying false.</summary>
        public bool? NullableBool(string name)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        public int? NullableInt(string name) =>
            TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i : (int?)null;
    }

    /// <summary>
    /// A failure that already knows its wire code. Thrown by layers that must not reference the
    /// Openness assembly (argument parsing, the daemon client), where SessionException is off limits.
    /// </summary>
    public sealed class WireException : System.Exception
    {
        public string Code { get; }
        public string Hint { get; }
        public string Detail { get; }

        public WireException(string code, string message, string hint = null, string detail = null)
            : base(message)
        {
            Code = code;
            Hint = hint;
            Detail = detail;
        }

        /// <summary>Re-throws an error that came back over the wire, keeping every part of it.</summary>
        public static WireException FromError(WireError error)
        {
            if (error == null)
                return new WireException(WireErrorCodes.Internal, "The request failed without an error.");
            return new WireException(error.Code, error.Message, error.Hint, error.Detail);
        }
    }
}
