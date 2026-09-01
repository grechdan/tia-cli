using TiaCli.Cli;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    public class CommandLineTests
    {
        [Fact]
        public void SingleWordVerb()
        {
            var line = CommandLine.Parse(new[] { "devices" });
            Assert.Equal("devices", line.Verb);
            Assert.Empty(line.Positionals);
        }

        [Fact]
        public void GroupVerbTakesTwoWords()
        {
            var line = CommandLine.Parse(new[] { "project", "open", @"C:\p\Line.ap20" });
            Assert.Equal("project open", line.Verb);
            Assert.Equal(new[] { @"C:\p\Line.ap20" }, line.Positionals);
        }

        [Fact]
        public void VerbIsLowercasedButPositionalsAreNot()
        {
            var line = CommandLine.Parse(new[] { "Project", "OPEN", @"C:\P\Line.ap20" });
            Assert.Equal("project open", line.Verb);
            Assert.Equal(@"C:\P\Line.ap20", line.Positionals[0]);
        }

        [Fact]
        public void BareGroupNameBecomesItsOwnVerb()
        {
            // "tia sim" with no subcommand; Commands.Execute then reports it unknown.
            var line = CommandLine.Parse(new[] { "sim" });
            Assert.Equal("sim", line.Verb);
        }

        [Fact]
        public void ValueFlagConsumesNextToken()
        {
            var line = CommandLine.Parse(new[] { "blocks", "PLC_1", "--filter", "Motor" });
            Assert.Equal("Motor", line.Value("filter"));
            Assert.Equal(new[] { "PLC_1" }, line.Positionals);
        }

        [Fact]
        public void EqualsFormWorksForAnyFlag()
        {
            // '=' assigns a value even to flags outside the value-flag list.
            var line = CommandLine.Parse(new[] { "session", "start", "--idle=30", "--unlisted=x" });
            Assert.Equal(30, line.Int("idle", 0));
            Assert.Equal("x", line.Value("unlisted"));
        }

        [Fact]
        public void ValueFlagFollowedByAnotherFlagBecomesASwitch()
        {
            var line = CommandLine.Parse(new[] { "blocks", "PLC_1", "--filter", "--json" });
            Assert.True(line.Has("filter"));
            Assert.Null(line.Value("filter"));
            Assert.True(line.Has("json"));
        }

        [Fact]
        public void SwitchesAreCaseInsensitive()
        {
            var line = CommandLine.Parse(new[] { "devices", "--JSON" });
            Assert.True(line.Has("json"));
        }

        [Fact]
        public void BundledShortFlagsSplit()
        {
            var line = CommandLine.Parse(new[] { "devices", "-ab" });
            Assert.True(line.Has("a"));
            Assert.True(line.Has("b"));
        }

        [Fact]
        public void BareDashIsAPositional()
        {
            var line = CommandLine.Parse(new[] { "scl", "import", "PLC_1", "Motor", "-" });
            Assert.Equal(new[] { "PLC_1", "Motor", "-" }, line.Positionals);
        }

        [Fact]
        public void IntRejectsNonNumbersWithInvalidRequest()
        {
            var line = CommandLine.Parse(new[] { "session", "start", "--idle", "soon" });
            var ex = Assert.Throws<WireException>(() => line.Int("idle", 0));
            Assert.Equal(WireErrorCodes.InvalidRequest, ex.Code);
        }

        [Fact]
        public void NullableIntDistinguishesAbsentFromInvalid()
        {
            var absent = CommandLine.Parse(new[] { "portals" });
            Assert.Null(absent.NullableInt("attach"));

            var given = CommandLine.Parse(new[] { "portals", "--attach", "1234" });
            Assert.Equal(1234, given.NullableInt("attach"));
        }

        [Fact]
        public void RequiredThrowsInvalidRequestWhenMissing()
        {
            var line = CommandLine.Parse(new[] { "device", "ip", "PLC_1" });
            var ex = Assert.Throws<WireException>(() => line.Required("address"));
            Assert.Equal(WireErrorCodes.InvalidRequest, ex.Code);
        }

        [Fact]
        public void MissingPositionalNamesTheArgument()
        {
            var line = CommandLine.Parse(new[] { "blocks" });
            var ex = Assert.Throws<WireException>(() => line.Positional(0, "device"));
            Assert.Equal(WireErrorCodes.InvalidRequest, ex.Code);
            Assert.Contains("<device>", ex.Message);
        }

        [Fact]
        public void PositionalOrNullDoesNotThrow()
        {
            var line = CommandLine.Parse(new[] { "blocks" });
            Assert.Null(line.PositionalOrNull(0));
        }
    }

    public class TokenizeTests
    {
        [Fact]
        public void SplitsOnWhitespace()
        {
            Assert.Equal(new[] { "blocks", "PLC_1", "--json" },
                CommandLine.Tokenize("blocks  PLC_1\t--json"));
        }

        [Fact]
        public void QuotesKeepSpacesTogether()
        {
            Assert.Equal(new[] { "project", "open", @"C:\My Projects\Line.ap20" },
                CommandLine.Tokenize(@"project open ""C:\My Projects\Line.ap20"""));
        }

        [Fact]
        public void QuotesCanStartMidToken()
        {
            Assert.Equal(new[] { "--filter=a b" }, CommandLine.Tokenize(@"--filter=""a b"""));
        }

        [Fact]
        public void EmptyLineYieldsNoTokens()
        {
            Assert.Empty(CommandLine.Tokenize("   "));
        }
    }
}
