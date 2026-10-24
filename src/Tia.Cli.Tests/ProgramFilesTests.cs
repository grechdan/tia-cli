using System.IO;
using System.Linq;
using TiaCli.Cli;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// A tag table as CSV. It is written here and read back here, by nobody else, so the one thing
    /// that matters is that what goes out comes back unchanged - whatever is in a comment.
    /// </summary>
    public class TagCsvTests
    {
        private static TagRow Row(string name, string comment = null, string address = "%I0.0") => new TagRow
        {
            Name = name, DataType = "Bool", Address = address, Comment = comment,
        };

        [Fact]
        public void WhatIsWrittenIsReadBack()
        {
            var rows = new[]
            {
                Row("Start", "plain"),
                Row("Stop", "with, a comma"),
                Row("Mode", "says \"auto\""),
                Row("Note", "two\r\nlines"),
                Row("Größe", "Füllstand"),
                Row("Spaced", "  leading spaces kept  "),
                new TagRow { Name = "Speed", DataType = "Int", Address = "%MW10", HmiWriteable = false },
                new TagRow { Name = "Free", DataType = "Real" },
            };

            var back = TagCsv.Read(TagCsv.Write(rows));

            Assert.Equal(rows.Length, back.Count);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Equal(rows[i].Name, back[i].Name);
                Assert.Equal(rows[i].DataType, back[i].DataType);
                Assert.Equal(rows[i].Address, back[i].Address);
                Assert.Equal(rows[i].Comment?.Trim(), back[i].Comment);
                Assert.Equal(rows[i].HmiVisible, back[i].HmiVisible);
                Assert.Equal(rows[i].HmiAccessible, back[i].HmiAccessible);
                Assert.Equal(rows[i].HmiWriteable, back[i].HmiWriteable);
            }
        }

        [Fact]
        public void TheHeaderIsTiasOwnColumnNames()
        {
            var first = TagCsv.Write(new TagRow[0]).Split('\n')[0].TrimEnd('\r');
            Assert.Equal("Name,Data Type,Logical Address,Comment,Hmi Visible,Hmi Accessible,Hmi Writeable", first);
        }

        [Fact]
        public void ColumnsMayComeInAnyOrderAndTheHmiOnesMayBeLeftOut()
        {
            var rows = TagCsv.Read("﻿logical address,NAME,Data Type\nI0.0,Start,Bool\n");
            var start = Assert.Single(rows);
            Assert.Equal("Start", start.Name);
            Assert.Equal("I0.0", start.Address);
            Assert.True(start.HmiVisible && start.HmiAccessible && start.HmiWriteable);
        }

        [Fact]
        public void AListingOfSeveralTablesLeadsWithTheTableAndIsNotImported()
        {
            var listing = TagCsv.Write(new[]
            {
                new TagRow { Table = "Motors", Name = "Start", DataType = "Bool" },
                new TagRow { Table = "Valves", Name = "Open", DataType = "Bool" },
            }, withTable: true);

            Assert.StartsWith("Table,Name,Data Type,", listing);
            Assert.Contains("\r\nMotors,Start,Bool,", listing);
            var refused = Assert.Throws<WireException>(() => TagCsv.Read(listing));
            Assert.Contains("table export", refused.Hint);
        }

        [Fact]
        public void BlankLinesAreIgnored()
        {
            Assert.Single(TagCsv.Read("Name,Data Type\r\n\r\nStart,Bool\r\n\r\n"));
        }

        [Theory]
        [InlineData("Name,Data Type,Colour\nStart,Bool,red\n", "Colour")]
        [InlineData("Name\nStart\n", "Data Type")]
        [InlineData("Name,Data Type\nStart,Bool\nstart,Int\n", "twice")]
        [InlineData("Name,Data Type\n,Bool\n", "no name")]
        [InlineData("Name,Data Type,Hmi Visible\nStart,Bool,maybe\n", "maybe")]
        [InlineData("", "empty")]
        public void AFileThatCannotBeTakenAtItsWordIsRefused(string text, string said)
        {
            var refused = Assert.Throws<WireException>(() => TagCsv.Read(text));
            Assert.Contains(said, refused.Message + refused.Hint);
        }
    }

    public class SyncPathTests
    {
        [Fact]
        public void GitStatusEntriesAreSplitAndARenameGivesBothSides()
        {
            var status = " M source/PLC_1/Program blocks/Twice.scl\0" +
                         "?? source/PLC_1/PLC tags/New table.csv\0" +
                         " D source/PLC_1/Program blocks/Gone.xml\0" +
                         "R  source/PLC_1/Program blocks/Pumps/Motor.scl\0source/PLC_1/Program blocks/Motor.scl\0";

            Assert.Equal(new[]
            {
                "source/PLC_1/Program blocks/Twice.scl",
                "source/PLC_1/PLC tags/New table.csv",
                "source/PLC_1/Program blocks/Gone.xml",
                "source/PLC_1/Program blocks/Pumps/Motor.scl",
                "source/PLC_1/Program blocks/Motor.scl",
            }, ProgramCommands.ParseStatus(status));
        }

        [Fact]
        public void DataBlocksGoInAfterTheCodeTheyDependOn()
        {
            var order = new[] { ".db", ".scl", ".xml", ".awl" }
                .OrderBy(TiaCli.Openness.TiaSession.BlockOrder).ToArray();
            Assert.Equal(".db", order.Last());
        }

        [Fact]
        public void NothingChangedIsNoPaths()
        {
            Assert.Empty(ProgramCommands.ParseStatus(""));
        }

        [Fact]
        public void PathsAreMadeRelativeToTheStationsFolder()
        {
            var station = Path.Combine(Path.GetTempPath(), "ws", "source", "PLC_1");

            Assert.Equal("Program blocks/Pumps/Motor.scl",
                ProgramCommands.Under(station, Path.Combine(station, "Program blocks", "Pumps", "Motor.scl")));
            Assert.Null(ProgramCommands.Under(station, Path.Combine(Path.GetTempPath(), "ws", "source", "PLC_10", "x.scl")));
            Assert.Null(ProgramCommands.Under(station, Path.Combine(Path.GetTempPath(), "elsewhere.scl")));
        }

        [Theory]
        [InlineData("export", "PLC_1")]
        [InlineData("sync", "PLC_1")]
        [InlineData("sync --file \"source/PLC_1/Program blocks/Twice.scl\"", "PLC_1")]
        [InlineData("sync --no-compile", "PLC_1")]
        [InlineData("export PLC_2", "PLC_2")]
        [InlineData("table export FieldTags", "PLC_1 FieldTags")]
        [InlineData("table export --all", "PLC_1")]
        [InlineData("table import FieldTags.csv --folder Motors", "PLC_1 FieldTags.csv")]
        [InlineData("table export FieldTags --xml", "PLC_1 FieldTags")]
        [InlineData("table import FieldTags.xml", "PLC_1 FieldTags.xml")]
        [InlineData("tables export --xml", "PLC_1")]
        [InlineData("types", "PLC_1")]
        [InlineData("type export MotorData", "PLC_1 MotorData")]
        [InlineData("type export --all --out x", "PLC_1")]
        [InlineData("type import MotorData.udt", "PLC_1 MotorData.udt")]
        [InlineData("type export PLC_2 MotorData", "PLC_2 MotorData")]
        [InlineData("blocks export --source", "PLC_1")]
        [InlineData("blocks import --from x", "PLC_1")]
        [InlineData("types export PLC_2", "PLC_2")]
        [InlineData("tables import", "PLC_1")]
        public void PluralsTakeTheWorkspacesStationToo(string line, string positionals)
        {
            ExportAndSyncTakeTheWorkspacesStation(line, positionals);
        }

        [Theory]
        [InlineData("type export PLC_2 MotorData", "PLC_2 MotorData")]
        public void ExportAndSyncTakeTheWorkspacesStation(string line, string positionals)
        {
            var cmd = CommandLine.Parse(CommandLine.Tokenize(line));
            WorkspaceCommands.FillStation(cmd, new Workspace { Name = "Line3", Station = "PLC_1" });
            Assert.Equal(positionals, string.Join(" ", cmd.Positionals));
        }
    }
}
