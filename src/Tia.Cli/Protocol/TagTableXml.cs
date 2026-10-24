using System.Linq;
using System.Xml.Linq;

namespace TiaCli.Protocol
{
    /// <summary>
    /// A tag table as TIA Portal's own Openness XML: tags, user constants and every language of the
    /// comments, which the CSV does not carry. TIA writes and reads it; all this needs to know is
    /// which table a file is, because the table's name is in the file, not in its file name.
    /// </summary>
    public static class TagTableXml
    {
        public const string TableElement = "SW.Tags.PlcTagTable";

        /// <summary>The name of the table the document holds, or null when it holds none.</summary>
        public static string NameOf(XDocument document)
        {
            var name = Child(Child(Child(document.Root, TableElement), "AttributeList"), "Name")?.Value.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private static XElement Child(XElement parent, string localName) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
    }
}
