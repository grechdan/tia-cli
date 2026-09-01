using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// Everything the user sees. Two modes: aligned text for a person, raw JSON for a script.
    /// Errors and warnings always go to stderr so --json output stays machine-readable.
    /// </summary>
    internal sealed class Output
    {
        public bool AsJson { get; set; }
        public bool Quiet { get; set; }

        private static bool Colours => !Console.IsOutputRedirected;

        public void Json(JsonElement element)
        {
            Console.WriteLine(JsonSerializer.Serialize(element, WireJson.Pretty));
        }

        public void JsonValue(object value)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, WireJson.Pretty));
        }

        public void Line(string text = "")
        {
            if (!Quiet) Console.WriteLine(text);
        }

        public void Detail(string text)
        {
            if (Quiet) return;
            Write(ConsoleColor.DarkGray, text);
        }

        public void Warn(string text)
        {
            WriteTo(Console.Error, ConsoleColor.Yellow, "warning: " + text);
        }

        public void Error(WireError error)
        {
            if (error == null)
            {
                WriteTo(Console.Error, ConsoleColor.Red, "error: unknown failure");
                return;
            }

            WriteTo(Console.Error, ConsoleColor.Red, "error: " + error.Message);
            if (!string.IsNullOrEmpty(error.Detail))
                WriteTo(Console.Error, ConsoleColor.DarkGray, "  " + error.Detail);
            if (!string.IsNullOrEmpty(error.Hint))
                WriteTo(Console.Error, ConsoleColor.DarkGray, "  " + error.Hint);
        }

        public void Pairs(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var list = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).ToList();
            if (list.Count == 0) return;

            var width = list.Max(p => p.Key.Length);
            foreach (var pair in list)
                Console.WriteLine("  " + pair.Key.PadRight(width) + "  " + pair.Value);
        }

        public static KeyValuePair<string, string> KV(string key, string value) =>
            new KeyValuePair<string, string>(key, value);

        /// <summary>Left-aligned columns sized to their content, with a dimmed header.</summary>
        public void Table(string[] headers, IList<string[]> rows)
        {
            if (rows.Count == 0)
            {
                Detail("(none)");
                return;
            }

            var widths = new int[headers.Length];
            for (var i = 0; i < headers.Length; i++)
            {
                widths[i] = headers[i].Length;
                foreach (var row in rows)
                    widths[i] = Math.Max(widths[i], (row[i] ?? string.Empty).Length);
            }

            var header = new StringBuilder("  ");
            for (var i = 0; i < headers.Length; i++)
            {
                header.Append(headers[i].ToUpperInvariant().PadRight(widths[i]));
                if (i < headers.Length - 1) header.Append("  ");
            }
            Write(ConsoleColor.DarkGray, header.ToString());

            foreach (var row in rows)
            {
                var line = new StringBuilder("  ");
                for (var i = 0; i < headers.Length; i++)
                {
                    var cell = row[i] ?? string.Empty;
                    // Only pad interior columns: trailing spaces make copied output messy.
                    line.Append(i < headers.Length - 1 ? cell.PadRight(widths[i]) + "  " : cell);
                }
                Console.WriteLine(line.ToString());
            }
        }

        public static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max - 1) + "…";
        }

        private static void Write(ConsoleColor colour, string text) => WriteTo(Console.Out, colour, text);

        private static void WriteTo(System.IO.TextWriter writer, ConsoleColor colour, string text)
        {
            if (!Colours)
            {
                writer.WriteLine(text);
                return;
            }

            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = colour;
                writer.WriteLine(text);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }
}
