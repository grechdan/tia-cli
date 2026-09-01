using System;
using System.Diagnostics;
using System.IO;
using TiaCli.Daemon;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// Points DaemonPaths at a temp directory for the lifetime of the test class, so these tests
    /// can never disturb a real daemon's advertisement in %LOCALAPPDATA%\tia-cli.
    /// </summary>
    public sealed class DaemonStateTests : IDisposable
    {
        private readonly string _home;

        public DaemonStateTests()
        {
            _home = Path.Combine(Path.GetTempPath(), "tia-cli-tests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TIA_CLI_HOME", _home);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TIA_CLI_HOME", null);
            try { Directory.Delete(_home, recursive: true); } catch { }
        }

        [Fact]
        public void HomeOverrideIsHonoured()
        {
            Assert.StartsWith(_home, DaemonPaths.StateFile);
        }

        [Fact]
        public void WriteThenReadRoundTrips()
        {
            DaemonState.Write(new DaemonState
            {
                Pipe = "tia-cli-test",
                ProcessId = 4242,
                StartedAt = "2026-08-31T12:00:00",
            });

            var read = DaemonState.Read();
            Assert.Equal("tia-cli-test", read.Pipe);
            Assert.Equal(4242, read.ProcessId);
            Assert.Equal("2026-08-31T12:00:00", read.StartedAt);
        }

        [Fact]
        public void ReadReturnsNullWhenNoFileExists()
        {
            DaemonState.Delete();
            Assert.Null(DaemonState.Read());
        }

        [Fact]
        public void ReadToleratesACorruptFile()
        {
            // A killed daemon can leave anything behind; Read must shrug, not throw.
            File.WriteAllText(DaemonPaths.StateFile, "{ not json");
            Assert.Null(DaemonState.Read());
        }

        [Fact]
        public void DeleteIsIdempotent()
        {
            DaemonState.Write(new DaemonState { Pipe = "p", ProcessId = 1 });
            DaemonState.Delete();
            DaemonState.Delete();
            Assert.False(File.Exists(DaemonPaths.StateFile));
        }

        [Fact]
        public void IsProcessAliveIsTrueForThisProcess()
        {
            var state = new DaemonState { ProcessId = Process.GetCurrentProcess().Id };
            Assert.True(state.IsProcessAlive());
        }

        [Fact]
        public void IsProcessAliveIsFalseForAnImpossiblePid()
        {
            // Pids are multiples of 4 on Windows; an odd one can never name a live process.
            var state = new DaemonState { ProcessId = 999999999 };
            Assert.False(state.IsProcessAlive());
        }

        [Fact]
        public void PipeNameIsPerUserAndSanitised()
        {
            Assert.StartsWith("tia-cli-", DaemonPaths.PipeName);
            Assert.DoesNotContain(" ", DaemonPaths.PipeName);
            Assert.Equal(DaemonPaths.PipeName, DaemonPaths.PipeName.ToLowerInvariant());
        }
    }
}
