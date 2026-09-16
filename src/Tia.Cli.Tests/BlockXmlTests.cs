using System.Linq;
using System.Xml.Linq;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    /// <summary>
    /// Exercises the SimaticML reading behind 'tia block show' against documents shaped the way TIA
    /// Portal writes them. This locks in the reading, not the format: only a real export on a
    /// licensed machine can confirm the format itself, which is what the field test is for.
    /// </summary>
    public class BlockXmlTests
    {
        /// <summary>
        /// An SCL function block: two interface sections, a nested struct, a member comment and a
        /// start value, a block comment and one network. Namespaces are on the interface sections
        /// exactly as TIA puts them there, so a reader bound to no namespace would fail this.
        /// </summary>
        private const string FunctionBlock = @"<?xml version='1.0' encoding='utf-8'?>
<Document>
  <Engineering version='V20' />
  <SW.Blocks.FB ID='0'>
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <Interface>
        <Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'>
          <Section Name='Input'>
            <Member Name='start' Datatype='Bool'>
              <Comment><MultiLanguageText Lang='en-US'>Start button</MultiLanguageText></Comment>
            </Member>
            <Member Name='setpoint' Datatype='Int'>
              <StartValue>10</StartValue>
            </Member>
          </Section>
          <Section Name='Static'>
            <Member Name='cfg' Datatype='Struct'>
              <Member Name='limit' Datatype='Int'><StartValue>99</StartValue></Member>
              <Member Name='inner' Datatype='Struct'>
                <Member Name='deep' Datatype='Bool' />
              </Member>
            </Member>
          </Section>
        </Sections>
      </Interface>
      <Name>Motor</Name>
      <Number>1</Number>
      <ProgrammingLanguage>SCL</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID='1' CompositionName='Comment'>
        <ObjectList>
          <MultilingualTextItem ID='2' CompositionName='Items'>
            <AttributeList><Culture>en-US</Culture><Text>Motor control</Text></AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
      <SW.Blocks.CompileUnit ID='3' CompositionName='CompileUnits'>
        <AttributeList>
          <NetworkSource />
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID='4' CompositionName='Comment'>
            <ObjectList>
              <MultilingualTextItem ID='5' CompositionName='Items'>
                <AttributeList><Culture>en-US</Culture><Text>What it does</Text></AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID='6' CompositionName='Title'>
            <ObjectList>
              <MultilingualTextItem ID='7' CompositionName='Items'>
                <AttributeList><Culture>en-US</Culture><Text>Run logic</Text></AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        private static BlockDetailDto Read(string xml)
        {
            var detail = new BlockDetailDto();
            BlockXml.ReadInto(XDocument.Parse(xml), detail);
            return detail;
        }

        [Fact]
        public void ReadsEverySectionInDeclarationOrder()
        {
            var detail = Read(FunctionBlock);

            // Nested members are flattened into the list in the order they are declared, directly
            // after the member that holds them - which is what makes an indented render possible.
            Assert.Equal(
                new[] { "start", "setpoint", "cfg", "limit", "inner", "deep" },
                detail.Interface.Select(m => m.Name));
            Assert.Equal(
                new[] { "Input", "Input", "Static", "Static", "Static", "Static" },
                detail.Interface.Select(m => m.Section));
        }

        [Fact]
        public void NestedStructMembersCarryTheirDepthAndTheirSection()
        {
            var detail = Read(FunctionBlock);

            var deep = detail.Interface.Single(m => m.Name == "deep");
            Assert.Equal(2, deep.Depth);
            // The section is the one the outermost member was declared in, not a nesting of its own.
            Assert.Equal("Static", deep.Section);
            Assert.Equal("Bool", deep.DataType);

            Assert.Equal(0, detail.Interface.Single(m => m.Name == "cfg").Depth);
            Assert.Equal(1, detail.Interface.Single(m => m.Name == "limit").Depth);
        }

        [Fact]
        public void MemberCommentsAndStartValuesAreRead()
        {
            var detail = Read(FunctionBlock);

            Assert.Equal("Start button", detail.Interface.Single(m => m.Name == "start").Comment);
            Assert.Null(detail.Interface.Single(m => m.Name == "start").StartValue);
            Assert.Equal("10", detail.Interface.Single(m => m.Name == "setpoint").StartValue);
            Assert.Equal("99", detail.Interface.Single(m => m.Name == "limit").StartValue);
        }

        [Fact]
        public void BlockCommentComesFromTheBlockNotFromItsFirstNetwork()
        {
            // Both are MultilingualText with CompositionName="Comment"; the network's is nested one
            // level deeper, and a descendant search would find whichever came first in the document.
            var detail = Read(FunctionBlock);

            Assert.Equal("Motor control", detail.Comment);
            Assert.Equal("What it does", detail.Networks.Single().Comment);
        }

        [Fact]
        public void NetworksAreNumberedFromOneAndKeepTheirTitle()
        {
            var detail = Read(FunctionBlock);

            var network = Assert.Single(detail.Networks);
            Assert.Equal(1, network.Index);
            Assert.Equal("Run logic", network.Title);
            Assert.Equal("SCL", network.Language);
        }

        [Fact]
        public void LadderBlocksKeepEveryNetworkInOrder()
        {
            var detail = Read(@"<?xml version='1.0' encoding='utf-8'?>
<Document>
  <SW.Blocks.OB ID='0'>
    <AttributeList>
      <Name>Main</Name>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID='1' CompositionName='CompileUnits'>
        <AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
        <ObjectList>
          <MultilingualText ID='2' CompositionName='Title'>
            <ObjectList><MultilingualTextItem ID='3' CompositionName='Items'>
              <AttributeList><Text>First</Text></AttributeList>
            </MultilingualTextItem></ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <SW.Blocks.CompileUnit ID='4' CompositionName='CompileUnits'>
        <AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
        <ObjectList>
          <MultilingualText ID='5' CompositionName='Title'>
            <ObjectList><MultilingualTextItem ID='6' CompositionName='Items'>
              <AttributeList><Text>Second</Text></AttributeList>
            </MultilingualTextItem></ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.OB>
</Document>");

            Assert.Equal(new[] { 1, 2 }, detail.Networks.Select(n => n.Index));
            Assert.Equal(new[] { "First", "Second" }, detail.Networks.Select(n => n.Title));
            Assert.Empty(detail.Interface);
        }

        [Fact]
        public void AnEmptyFirstTranslationFallsThroughToOneWithText()
        {
            var detail = Read(@"<?xml version='1.0' encoding='utf-8'?>
<Document>
  <SW.Blocks.FC ID='0'>
    <AttributeList><Name>Empty</Name></AttributeList>
    <ObjectList>
      <MultilingualText ID='1' CompositionName='Comment'>
        <ObjectList>
          <MultilingualTextItem ID='2' CompositionName='Items'>
            <AttributeList><Culture>de-DE</Culture><Text />
            </AttributeList>
          </MultilingualTextItem>
          <MultilingualTextItem ID='3' CompositionName='Items'>
            <AttributeList><Culture>en-US</Culture><Text>Only this one is filled in</Text></AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
    </ObjectList>
  </SW.Blocks.FC>
</Document>");

            Assert.Equal("Only this one is filled in", detail.Comment);
        }

        [Fact]
        public void AnUnrecognisedDocumentLeavesTheHeaderAlone()
        {
            // The header attributes come from Openness, not from here. A document this code cannot
            // make sense of must not cost the caller the part that was already known.
            var detail = new BlockDetailDto { Name = "Motor", BlockType = "FB", Number = 1 };
            BlockXml.ReadInto(XDocument.Parse("<Document><Something /></Document>"), detail);

            Assert.Equal("Motor", detail.Name);
            Assert.Empty(detail.Interface);
            Assert.Empty(detail.Networks);
        }

        [Fact]
        public void ADataBlockHasAnInterfaceAndNoNetworks()
        {
            var detail = Read(@"<?xml version='1.0' encoding='utf-8'?>
<Document>
  <SW.Blocks.GlobalDB ID='0'>
    <AttributeList>
      <Interface>
        <Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'>
          <Section Name='Static'>
            <Member Name='count' Datatype='DInt'><StartValue>0</StartValue></Member>
          </Section>
        </Sections>
      </Interface>
      <Name>Data</Name>
    </AttributeList>
  </SW.Blocks.GlobalDB>
</Document>");

            var member = Assert.Single(detail.Interface);
            Assert.Equal("count", member.Name);
            Assert.Equal("DInt", member.DataType);
            Assert.Equal("Static", member.Section);
            Assert.Empty(detail.Networks);
        }
    }
}
