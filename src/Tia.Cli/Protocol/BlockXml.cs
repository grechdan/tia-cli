using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TiaCli.Protocol
{
    /// <summary>
    /// Reads a block's interface and networks out of an Openness export.
    ///
    /// This is the only place the SimaticML layout is known, and it is deliberately kept clear of
    /// the Siemens assembly: the export is just a file by the time it gets here, so the parsing can
    /// be unit-tested on a machine with no TIA Portal on it at all.
    ///
    /// Everything is matched by *local* element name. The SimaticML namespaces carry a schema
    /// version that moves with each TIA release - the interface section alone has been through
    /// several - and binding to one of them would break this on the next release for no gain.
    /// </summary>
    public static class BlockXml
    {
        /// <summary>
        /// Fills in the parts of <paramref name="detail"/> that only the export knows. Anything the
        /// document does not contain is left as it was, so a partial or unfamiliar export degrades
        /// to the header attributes the caller already had rather than failing the command.
        /// </summary>
        public static void ReadInto(XDocument document, BlockDetailDto detail)
        {
            if (document == null || detail == null) return;

            detail.Interface = detail.Interface ?? new List<InterfaceMemberDto>();
            detail.Networks = detail.Networks ?? new List<NetworkDto>();

            // <Document> holds one SW.Blocks.<kind> element - FB, FC, OB, GlobalDB and so on.
            var block = document.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            if (block == null) return;

            var sections = Child(Child(Child(block, "AttributeList"), "Interface"), "Sections");
            if (sections != null)
            {
                foreach (var section in Children(sections, "Section"))
                {
                    var name = (string)section.Attribute("Name");
                    foreach (var member in Children(section, "Member"))
                        ReadMember(member, name, 0, detail.Interface);
                }
            }

            detail.Comment = FirstText(block, "Comment");

            var index = 1;
            foreach (var unit in block.Descendants()
                         .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
            {
                detail.Networks.Add(new NetworkDto
                {
                    Index = index++,
                    Title = FirstText(unit, "Title"),
                    Comment = FirstText(unit, "Comment"),
                    Language = Child(Child(unit, "AttributeList"), "ProgrammingLanguage")?.Value,
                });
            }
        }

        private static void ReadMember(XElement member, string section, int depth,
            List<InterfaceMemberDto> sink)
        {
            sink.Add(new InterfaceMemberDto
            {
                Section = section,
                Name = (string)member.Attribute("Name"),
                DataType = (string)member.Attribute("Datatype"),
                StartValue = Child(member, "StartValue")?.Value,
                // The comment is one <MultiLanguageText> per language; the first with anything in it
                // will do, since the CLI has no language to prefer.
                Comment = Child(member, "Comment")?.Elements()
                    .Select(e => e.Value)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
                Depth = depth,
            });

            // A Struct or a UDT instance carries its own members; they are part of the interface too.
            foreach (var child in Children(member, "Member"))
                ReadMember(child, section, depth + 1, sink);
        }

        /// <summary>The first non-empty translation of a named MultilingualText composition.</summary>
        private static string FirstText(XElement owner, string composition)
        {
            // Direct children only: a block's ObjectList holds its own comment *and* every compile
            // unit, each with a comment of its own, and a descendant search would find theirs first.
            var text = Child(owner, "ObjectList")?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "MultilingualText" &&
                                     (string)e.Attribute("CompositionName") == composition);
            if (text == null) return null;

            return text.Descendants()
                .Where(e => e.Name.LocalName == "MultilingualTextItem")
                .Select(item => Child(Child(item, "AttributeList"), "Text")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static XElement Child(XElement parent, string localName) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        private static IEnumerable<XElement> Children(XElement parent, string localName) =>
            parent.Elements().Where(e => e.Name.LocalName == localName);
    }
}
