using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// Checks that the three layers a command passes through still agree: the verb the parser
    /// produces, the wire method the CLI sends, and the case the session host answers with.
    ///
    /// These read the source rather than the compiled assembly on purpose. The mapping lives in
    /// switch statements, which leave nothing behind for reflection to inspect, and a mismatch there
    /// compiles perfectly - it only shows up as "Unknown method" in front of a real TIA Portal,
    /// which is the most expensive place to find it.
    /// </summary>
    public class WiringTests
    {
        private static string Source(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null &&
                   !Directory.Exists(Path.Combine(directory.FullName, "src", "Tia.Cli")))
                directory = directory.Parent;

            Assert.True(directory != null, "Could not find the repository root from " + AppContext.BaseDirectory);

            var path = Path.Combine(new[] { directory.FullName, "src", "Tia.Cli" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path + " does not exist");
            return File.ReadAllText(path);
        }

        /// <summary>Verbs handled outside the Commands switch, so absent from it by design.</summary>
        private static readonly string[] HandledElsewhere = { "session start", "session stop", "session status", "shell" };

        /// <summary>Kept working for people who learned it before 'source add' existed.</summary>
        private static readonly string[] UndocumentedAliases = { "scl import" };

        [Fact]
        public void EveryWireMethodTheCliSendsHasAHandler()
        {
            var commands = Source("Cli", "Commands.cs");
            var host = Source("Openness", "SessionHost.cs");

            var sent = Regex.Matches(commands,
                    "\"(portal|session|project|device|catalog|source|block|tag|ui|plc|sim)\\.[A-Za-z]+\"")
                .Cast<Match>()
                .Select(m => m.Value.Trim('"'))
                .Distinct()
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            // A regex that quietly stops matching would make this test pass by finding nothing.
            Assert.True(sent.Count > 20, $"Only found {sent.Count} wire methods in Commands.cs");

            var missing = sent.Where(m => !host.Contains("case \"" + m + "\"")).ToList();
            Assert.True(missing.Count == 0,
                "SessionHost.Dispatch has no case for: " + string.Join(", ", missing));
        }

        [Fact]
        public void EveryVerbTheCliAnswersIsInTheHelp()
        {
            var commands = Source("Cli", "Commands.cs");
            var help = Source("Cli", "Help.cs");

            var verbs = Regex.Matches(commands, "case \"([a-z][a-z ]*)\":")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Except(UndocumentedAliases)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();

            Assert.True(verbs.Count > 20, $"Only found {verbs.Count} verbs in Commands.cs");

            var undocumented = verbs.Where(v => !help.Contains(v)).ToList();
            Assert.True(undocumented.Count == 0,
                "Help.cs never mentions: " + string.Join(", ", undocumented));
        }

        [Fact]
        public void EveryVerbTheHelpPromisesIsAnswered()
        {
            // The other direction: help that lists a verb nobody handles sends people to a dead end.
            var commands = Source("Cli", "Commands.cs");
            var help = Source("Cli", "Help.cs");

            // Only the command table, not the prose or the examples around it. A table entry is a verb
            // at the left margin followed by either its aligned description or its arguments - which
            // is what separates "block export <device>" from "tia blocks PLC_1 --tree" in the examples
            // and from a sentence that happens to start with a lowercase word.
            // The "(?!tia\b)" drops the usage line and the examples, which are written the way a user
            // would type them - "tia blocks PLC_1 --tree" - while a table entry never repeats the
            // program name.
            var promised = Regex.Matches(help, @"(?m)^  (?!tia\b)([a-z]+(?: [a-z]+)?)(?=  +\S|$| <)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct()
                .Except(HandledElsewhere)
                .ToList();

            Assert.True(promised.Count > 15, $"Only found {promised.Count} verbs in the help text");

            var unhandled = promised
                .Where(v => !commands.Contains("case \"" + v + "\""))
                .ToList();

            Assert.True(unhandled.Count == 0,
                "Help lists verbs the Commands switch does not handle: " + string.Join(", ", unhandled));
        }
    }
}
