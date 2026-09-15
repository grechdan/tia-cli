using System.Collections.Generic;
using TiaCli.Openness;
using Xunit;

namespace TiaCli.Tests
{
    public class OpennessVersionTests
    {
        private static readonly string[] Installed = { "21.0", "20.0", "19.0" };

        [Fact]
        public void NewestUsableVersionWinsWhenNoneIsRequested()
        {
            // The case seen in the field: V21 registered, its assemblies missing.
            Assert.Equal("20.0", OpennessResolver.PickVersion(Installed, null, v => v != "21.0"));
        }

        [Fact]
        public void NewestVersionWinsWhenAllAreUsable()
        {
            Assert.Equal("21.0", OpennessResolver.PickVersion(Installed, null, v => true));
        }

        [Fact]
        public void RequestedVersionIsTakenOverNewerOnes()
        {
            Assert.Equal("19.0", OpennessResolver.PickVersion(Installed, "19.0", v => true));
        }

        [Fact]
        public void RequestedButBrokenVersionIsRefusedRatherThanSwapped()
        {
            // Asking for a version and silently getting another would be worse than failing.
            var ex = Assert.Throws<OpennessSetupException>(
                () => OpennessResolver.PickVersion(Installed, "21.0", v => v != "21.0"));
            Assert.Contains("21.0", ex.Message);
        }

        [Fact]
        public void RequestedButMissingVersionListsWhatIsInstalled()
        {
            var ex = Assert.Throws<OpennessSetupException>(
                () => OpennessResolver.PickVersion(Installed, "18.0", v => true));
            Assert.Contains("not installed", ex.Message);
            Assert.Contains("20.0", ex.Message);
        }

        [Fact]
        public void NoUsableVersionAtAllIsAnError()
        {
            var ex = Assert.Throws<OpennessSetupException>(
                () => OpennessResolver.PickVersion(Installed, null, v => false));
            Assert.Contains("No installed Openness version", ex.Message);
        }

        [Fact]
        public void OnlyTheRequestedVersionIsProbed()
        {
            var probed = new List<string>();
            OpennessResolver.PickVersion(Installed, "19.0", v => { probed.Add(v); return true; });
            Assert.Equal(new[] { "19.0" }, probed);
        }

        [Fact]
        public void ProbingStopsAtTheFirstUsableVersion()
        {
            var probed = new List<string>();
            OpennessResolver.PickVersion(Installed, null, v => { probed.Add(v); return v == "20.0"; });
            Assert.Equal(new[] { "21.0", "20.0" }, probed);
        }
    }
}
