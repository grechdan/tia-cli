using System.Collections.Generic;

namespace TiaCli.Protocol
{
    /// <summary>
    /// A block with everything that needed reading out of it: the header attributes Openness gives
    /// directly, plus the interface and networks, which only exist in the export XML.
    /// </summary>
    public sealed class BlockDetailDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string BlockType { get; set; }
        public int Number { get; set; }
        public bool AutoNumber { get; set; }
        public string Language { get; set; }
        public string Namespace { get; set; }
        public string SecondaryType { get; set; }
        public string InstanceOfName { get; set; }
        public string MemoryLayout { get; set; }
        public bool IsConsistent { get; set; }
        public bool IsKnowHowProtected { get; set; }
        public string HeaderAuthor { get; set; }
        public string HeaderFamily { get; set; }
        public string HeaderName { get; set; }
        public string HeaderVersion { get; set; }
        public string Comment { get; set; }
        public string CreationDate { get; set; }
        public string ModifiedDate { get; set; }
        public string CompileDate { get; set; }
        /// <summary>Flattened interface members; nested struct members carry a dotted name.</summary>
        public List<InterfaceMemberDto> Interface { get; set; }
        public List<NetworkDto> Networks { get; set; }
    }

    public sealed class InterfaceMemberDto
    {
        /// <summary>Input, Output, InOut, Static, Temp, Constant, Return.</summary>
        public string Section { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string StartValue { get; set; }
        public string Comment { get; set; }
        /// <summary>0 for a member of the section itself, 1 inside a struct, and so on.</summary>
        public int Depth { get; set; }
    }

    /// <summary>One network of a code block. SCL blocks have exactly one.</summary>
    public sealed class NetworkDto
    {
        public int Index { get; set; }
        public string Title { get; set; }
        public string Comment { get; set; }
        public string Language { get; set; }
    }

    /// <summary>A node of the program-blocks tree: either a folder with children, or a block.</summary>
    public sealed class BlockTreeNodeDto
    {
        /// <summary>"folder" or "block".</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public BlockDto Block { get; set; }
        public List<BlockTreeNodeDto> Children { get; set; }
    }

    /// <summary>A folder in the program-blocks tree.</summary>
    public sealed class BlockFolderDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int BlockCount { get; set; }
        public int FolderCount { get; set; }
    }

    /// <summary>What an XML import produced.</summary>
    public sealed class BlockImportDto
    {
        public string FilePath { get; set; }
        public string Folder { get; set; }
        public bool Overwrote { get; set; }
        public List<BlockDto> Blocks { get; set; }
    }

    /// <summary>An external source as it sits in the project tree.</summary>
    public sealed class SourceDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }
}
