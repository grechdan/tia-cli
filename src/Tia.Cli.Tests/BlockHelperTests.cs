using TiaCli.Openness;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// The block filter. It decides what 'tia blocks --filter/--type' answers with, and it is the one
    /// place where "DB" has to mean both kinds of data block - which no other layer would catch.
    /// </summary>
    public class BlockFilterTests
    {
        private static BlockDto Block(string name, string type) =>
            new BlockDto { Name = name, BlockType = type };

        [Fact]
        public void WithoutFiltersEverythingMatches()
        {
            Assert.True(TiaSession.Matches(Block("Motor", "FB"), null, null));
            Assert.True(TiaSession.Matches(Block("Motor", "FB"), "", ""));
        }

        [Fact]
        public void TheNameFilterIsACaseInsensitiveSubstring()
        {
            Assert.True(TiaSession.Matches(Block("PumpMotor", "FB"), "motor", null));
            Assert.True(TiaSession.Matches(Block("PumpMotor", "FB"), "Pump", null));
            Assert.False(TiaSession.Matches(Block("PumpMotor", "FB"), "Valve", null));
        }

        [Theory]
        [InlineData("DB", "GlobalDB", true)]
        [InlineData("DB", "InstanceDB", true)]
        [InlineData("db", "GlobalDB", true)]
        [InlineData("DB", "FB", false)]
        [InlineData("GlobalDB", "InstanceDB", false)]
        public void AskingForDbCoversBothKindsOfDataBlock(string filter, string blockType, bool expected)
        {
            // "DB" is what people type; Openness calls them GlobalDB and InstanceDB.
            Assert.Equal(expected, TiaSession.Matches(Block("Data", blockType), null, filter));
        }

        [Fact]
        public void ATypeListMatchesAnyOfItsEntries()
        {
            Assert.True(TiaSession.Matches(Block("Motor", "FB"), null, "FB,FC"));
            Assert.True(TiaSession.Matches(Block("Calc", "FC"), null, "FB,FC"));
            Assert.False(TiaSession.Matches(Block("Main", "OB"), null, "FB,FC"));
        }

        [Fact]
        public void PaddingAndEmptyEntriesInTheTypeListAreIgnored()
        {
            Assert.True(TiaSession.Matches(Block("Calc", "FC"), null, " FB , , FC "));
            Assert.False(TiaSession.Matches(Block("Main", "OB"), null, " FB , , FC "));
        }

        [Fact]
        public void TypeMatchingIsWholeWordNotASuffix()
        {
            // "B" must not sweep up every FB and OB; only the DB shorthand is special.
            Assert.False(TiaSession.Matches(Block("Motor", "FB"), null, "B"));
        }

        [Fact]
        public void NameAndTypeBothHaveToMatch()
        {
            Assert.True(TiaSession.Matches(Block("Motor", "FB"), "Mot", "FB"));
            Assert.False(TiaSession.Matches(Block("Motor", "FB"), "Mot", "FC"));
            Assert.False(TiaSession.Matches(Block("Valve", "FB"), "Mot", "FB"));
        }
    }

    /// <summary>
    /// Block paths as the CLI accepts them: "Pumps/Small/FC1", with or without the root folder in
    /// front, and with whichever slash the shell left behind.
    /// </summary>
    public class BlockPathTests
    {
        private const string Root = "Program blocks";

        [Fact]
        public void ALeadingRootFolderNameIsDropped()
        {
            Assert.Equal(new[] { "Pumps", "FC1" }, TiaSession.Segments("Program blocks/Pumps/FC1", Root));
            Assert.Equal(new[] { "Pumps", "FC1" }, TiaSession.Segments("program BLOCKS/Pumps/FC1", Root));
        }

        [Fact]
        public void TheRootNameIsOnlyDroppedAtTheFront()
        {
            // A folder a user called "Program blocks" further down is theirs, not the root.
            Assert.Equal(new[] { "Pumps", "Program blocks" },
                TiaSession.Segments("Program blocks/Pumps/Program blocks", Root));
        }

        [Fact]
        public void BackslashesAreAcceptedToo()
        {
            Assert.Equal(new[] { "Pumps", "FC1" }, TiaSession.Segments(@"Pumps\FC1", Root));
        }

        [Fact]
        public void EmptyAndPaddedSegmentsAreDropped()
        {
            Assert.Equal(new[] { "Pumps", "FC1" }, TiaSession.Segments("/Pumps//  FC1  /", Root));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Program blocks")]
        public void NothingBelowTheRootIsAnEmptyPath(string path)
        {
            Assert.Empty(TiaSession.Segments(path, Root));
        }

        [Fact]
        public void NormalizeJoinsTheSegmentsWithForwardSlashes()
        {
            Assert.Equal("Pumps/FC1", TiaSession.Normalize(@"Program blocks\Pumps\FC1", Root));
            Assert.Equal("", TiaSession.Normalize("Program blocks", Root));
        }
    }

    /// <summary>Pruning is what keeps a filtered tree from being mostly empty folders.</summary>
    public class BlockTreePruneTests
    {
        private static BlockTreeNodeDto Folder(params BlockTreeNodeDto[] children) =>
            new BlockTreeNodeDto
            {
                Kind = "folder",
                Name = "Pumps",
                Children = new System.Collections.Generic.List<BlockTreeNodeDto>(children),
            };

        [Fact]
        public void AnEmptyFolderSurvivesWhenNothingIsBeingFiltered()
        {
            var folder = Folder();
            Assert.Same(folder, TiaSession.Prune(folder, null, null));
            Assert.Same(folder, TiaSession.Prune(folder, "", ""));
        }

        [Fact]
        public void AnEmptyFolderDisappearsUnderAFilter()
        {
            Assert.Null(TiaSession.Prune(Folder(), "Motor", null));
            Assert.Null(TiaSession.Prune(Folder(), null, "FB"));
        }

        [Fact]
        public void AFolderThatSomethingMatchedInIsKept()
        {
            var folder = Folder(new BlockTreeNodeDto { Kind = "block", Name = "Motor" });
            Assert.Same(folder, TiaSession.Prune(folder, "Motor", null));
        }
    }

    public class SourceNameTests
    {
        [Theory]
        [InlineData("Motor", "Motor.scl")]
        [InlineData("Motor.scl", "Motor.scl")]
        [InlineData("Motor.awl", "Motor.awl")]
        [InlineData("Motor.db", "Motor.db")]
        public void SourcesGetAnSclExtensionOnlyWhenTheyHaveNone(string given, string expected)
        {
            Assert.Equal(expected, TiaSession.WithSourceExtension(given));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankTextBecomesNullSoItIsLeftOutOfJson(string value)
        {
            Assert.Null(TiaSession.NullIfEmpty(value));
        }

        [Fact]
        public void RealTextIsKept()
        {
            Assert.Equal("Pumps", TiaSession.NullIfEmpty("Pumps"));
        }
    }
}
