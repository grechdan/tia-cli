using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// A small hand-rolled parser. Verbs are one or two words ("devices", "project open"); flags are
    /// either switches or carry a value, given as --flag value or --flag=value.
    /// </summary>
    internal sealed class CommandLine
    {
        /// <summary>Flags that consume the following token when it is not written with '='.</summary>
        private static readonly HashSet<string> ValueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "attach", "idle", "openness-version", "limit", "filter", "table", "type", "address",
            "subnet", "device-name", "out", "file", "code", "project", "max-chars", "view", "router",
            "mask", "subnet-mask", "via", "interface", "slot", "target",
        };

        /// <summary>Verbs whose first word is only a group name.</summary>
        private static readonly HashSet<string> Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session", "project", "device", "block", "scl", "tag", "table", "daemon", "show", "sim",
        };

        public string Verb { get; private set; } = string.Empty;
        public List<string> Positionals { get; } = new List<string>();
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static CommandLine Parse(string[] args)
        {
            var line = new CommandLine();
            var words = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    var name = arg.Substring(2);
                    var eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        line._values[name.Substring(0, eq)] = name.Substring(eq + 1);
                        continue;
                    }

                    if (ValueFlags.Contains(name) && i + 1 < args.Length &&
                        !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        line._values[name] = args[++i];
                        continue;
                    }

                    line._switches.Add(name);
                    continue;
                }

                if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1 && arg != "-")
                {
                    // Only short flags we actually define; a bare "-" is a legitimate stdin marker.
                    foreach (var c in arg.Substring(1)) line._switches.Add(c.ToString());
                    continue;
                }

                words.Add(arg);
            }

            if (words.Count > 0)
            {
                if (Groups.Contains(words[0]) && words.Count > 1)
                {
                    line.Verb = words[0].ToLowerInvariant() + " " + words[1].ToLowerInvariant();
                    line.Positionals.AddRange(words.Skip(2));
                }
                else
                {
                    line.Verb = words[0].ToLowerInvariant();
                    line.Positionals.AddRange(words.Skip(1));
                }
            }

            return line;
        }

        public bool Has(string name) => _switches.Contains(name) || _values.ContainsKey(name);

        public string Value(string name, string fallback = null) =>
            _values.TryGetValue(name, out var value) ? value : fallback;

        public string Required(string name)
        {
            var value = Value(name);
            if (string.IsNullOrEmpty(value))
                throw new WireException(WireErrorCodes.InvalidRequest, $"--{name} is required here.");
            return value;
        }

        public int Int(string name, int fallback)
        {
            var value = Value(name);
            if (value == null) return fallback;
            if (!int.TryParse(value, out var parsed))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"--{name} expects a number, got '{value}'.");
            return parsed;
        }

        public int? NullableInt(string name)
        {
            var value = Value(name);
            if (value == null) return null;
            if (!int.TryParse(value, out var parsed))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    $"--{name} expects a number, got '{value}'.");
            return parsed;
        }

        public string Positional(int index, string name)
        {
            if (index >= Positionals.Count)
                throw new WireException(WireErrorCodes.InvalidRequest, $"Missing argument <{name}>.");
            return Positionals[index];
        }

        public string PositionalOrNull(int index) =>
            index < Positionals.Count ? Positionals[index] : null;

        /// <summary>Splits a shell line into argv, honouring double quotes around paths with spaces.</summary>
        public static string[] Tokenize(string line)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var quoted = false;

            foreach (var c in line)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }

            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens.ToArray();
        }
    }
}
