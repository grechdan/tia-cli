using System.Xml.Linq;
using TiaCli.Cli;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// A tag table's XML names its table; that name, not the file's, is the table an import
    /// replaces. Only a real export confirms the format, which the field test does.
    /// </summary>
    public class TagTableXmlTests
    {
        /// <summary>Shaped the way TIA Portal exports one: a tag, a user constant, two languages.</summary>
        private const string Table = @"<?xml version='1.0' encoding='utf-8'?>
<Document>
  <Engineering version='V18' />
  <SW.Tags.PlcTagTable ID='0'>
    <AttributeList>
      <Name>Field Tags</Name>
    </AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID='1' CompositionName='Tags'>
        <AttributeList>
          <DataTypeName>Bool</DataTypeName>
          <LogicalAddress>%Q0.0</LogicalAddress>
          <Name>MotorRun</Name>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID='2' CompositionName='Comment'>
            <ObjectList>
              <MultilingualTextItem ID='3' CompositionName='Items'>
                <AttributeList><Culture>de-DE</Culture><Text>Motor läuft</Text></AttributeList>
              </MultilingualTextItem>
              <MultilingualTextItem ID='4' CompositionName='Items'>
                <AttributeList><Culture>en-US</Culture><Text>Motor runs</Text></AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Tags.PlcTag>
      <SW.Tags.PlcUserConstant ID='5' CompositionName='UserConstants'>
        <AttributeList>
          <DataTypeName>Int</DataTypeName>
          <Name>MaxSpeed</Name>
          <Value>1500</Value>
        </AttributeList>
      </SW.Tags.PlcUserConstant>
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>";

        [Fact]
        public void TheTableIsNamedByItsOwnNameNotATagsOrConstants()
        {
            Assert.Equal("Field Tags", TagTableXml.NameOf(XDocument.Parse(Table)));
        }

        [Fact]
        public void ANamespaceOnTheDocumentDoesNotHideTheName()
        {
            var document = XDocument.Parse(Table.Replace("<Document>", "<Document xmlns='urn:x'>"));
            Assert.Equal("Field Tags", TagTableXml.NameOf(document));
        }

        [Theory]
        [InlineData("<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Motor</Name></AttributeList></SW.Blocks.FB></Document>")]
        [InlineData("<Document><SW.Tags.PlcTagTable ID='0'><AttributeList><Name> </Name></AttributeList></SW.Tags.PlcTagTable></Document>")]
        [InlineData("<Document />")]
        public void ADocumentWithoutANamedTagTableHasNoName(string xml)
        {
            Assert.Null(TagTableXml.NameOf(XDocument.Parse(xml)));
        }

        [Theory]
        [InlineData("FieldTags", null, false, "FieldTags.csv")]
        [InlineData("FieldTags", null, true, "FieldTags.xml")]
        [InlineData("FieldTags", "out/t.xml", false, "out/t.xml")]
        [InlineData("FieldTags", "out/t.XML", true, "out/t.XML")]
        [InlineData("FieldTags", "out/t.csv", false, "out/t.csv")]
        public void TableExportWritesWhereOutAndXmlSay(string table, string outPath, bool xml, string expected)
        {
            Assert.Equal(expected, ProgramCommands.TableFile(table, outPath, xml));
        }

        [Fact]
        public void XmlWithACsvOutIsRefused()
        {
            var error = Assert.Throws<WireException>(() => ProgramCommands.TableFile("FieldTags", "t.csv", true));
            Assert.Contains("--xml", error.Message);
        }
    }
}
