using System.Collections.Generic;

namespace TiaCli.Protocol
{
    public sealed class PortalProcessDto
    {
        public int ProcessId { get; set; }
        public string Mode { get; set; }
        public string ExePath { get; set; }
        public string ProjectPath { get; set; }
        public string AcquisitionTime { get; set; }
    }

    /// <summary>What the CLI knows about the session it is talking through.</summary>
    public sealed class SessionStateDto
    {
        public bool Connected { get; set; }
        public bool WithUserInterface { get; set; }
        public int ProcessId { get; set; }
        public string OpennessVersion { get; set; }
        /// <summary>"attached" when the portal was already running, "created" when this tool started it.</summary>
        public string Origin { get; set; }
        /// <summary>True when disposing this session also closes the TIA Portal process.</summary>
        public bool OwnsPortal { get; set; }
        public ProjectDto Project { get; set; }
    }

    public sealed class DaemonStatusDto
    {
        public bool Running { get; set; }
        /// <summary>A stop has been accepted; the daemon is on its way out.</summary>
        public bool Stopping { get; set; }
        public int ProcessId { get; set; }
        public string Pipe { get; set; }
        public string StartedAt { get; set; }
        public string LogPath { get; set; }
        public SessionStateDto Session { get; set; }
        /// <summary>The session's portal process is no longer running: the snapshot describes a corpse.</summary>
        public bool PortalExited { get; set; }
    }

    /// <summary>What was put on screen in TIA Portal.</summary>
    public sealed class ShownDto
    {
        public string What { get; set; }
        public string Name { get; set; }
        /// <summary>Only set for the hardware editor: Device, Network or Topology.</summary>
        public string View { get; set; }
    }

    public sealed class ProjectDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string CreationTime { get; set; }
        public string LastModified { get; set; }
        public string LastModifiedBy { get; set; }
        public bool IsModified { get; set; }
        public int DeviceCount { get; set; }
    }

    public sealed class DeviceDto
    {
        public string Name { get; set; }
        public string TypeIdentifier { get; set; }
        public bool IsGsd { get; set; }
        public string CpuItemName { get; set; }
        public string CpuTypeIdentifier { get; set; }
        public bool HasPlcSoftware { get; set; }
        public string PlcSoftwareName { get; set; }
        public List<string> Addresses { get; set; }
    }

    public sealed class CatalogEntryDto
    {
        public string TypeIdentifier { get; set; }
        public string ArticleNumber { get; set; }
        public string TypeName { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string CatalogPath { get; set; }
    }

    public sealed class NetworkNodeDto
    {
        public string Device { get; set; }
        public string InterfaceItem { get; set; }
        public string NodeName { get; set; }
        public string Address { get; set; }
        public string SubnetName { get; set; }
        /// <summary>The IP subnet mask, which is unrelated to <see cref="SubnetName"/>.</summary>
        public string SubnetMask { get; set; }
        /// <summary>The "use router" checkbox on the interface's Ethernet addresses.</summary>
        public bool UseRouter { get; set; }
        /// <summary>The gateway. TIA keeps the last value even when the checkbox is off.</summary>
        public string RouterAddress { get; set; }
    }

    public sealed class SourceImportDto
    {
        public string SourceName { get; set; }
        public string FilePath { get; set; }
        public bool Generated { get; set; }
        public List<string> GeneratedBlocks { get; set; }
    }

    public sealed class BlockDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string BlockType { get; set; }
        public int Number { get; set; }
        public string Language { get; set; }
        public string Namespace { get; set; }
        public bool IsConsistent { get; set; }
        public bool IsKnowHowProtected { get; set; }
        public string ModifiedDate { get; set; }
        public string InstanceOfName { get; set; }
        public string SecondaryType { get; set; }
    }

    public sealed class ExportResultDto
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public long Bytes { get; set; }
        public string Content { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class TagTableDto
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDefault { get; set; }
        public int TagCount { get; set; }
    }

    public sealed class TagDto
    {
        public string Name { get; set; }
        public string TableName { get; set; }
        public string DataType { get; set; }
        public string LogicalAddress { get; set; }
        public string Comment { get; set; }
    }

    /// <summary>The outcome of a download, upload or simulation start.</summary>
    public sealed class TransferResultDto
    {
        public string Operation { get; set; }
        public string Device { get; set; }
        public string Mode { get; set; }
        public string PcInterface { get; set; }
        public string TargetAddress { get; set; }
        public string State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        /// <summary>The dialog prompts that were answered automatically, in plain words.</summary>
        public List<string> Decisions { get; set; }
        public List<string> Messages { get; set; }
        /// <summary>Upload only: the station the upload created.</summary>
        public string UploadedStation { get; set; }
    }

    public sealed class CompileResultDto
    {
        public string State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<CompileMessageDto> Messages { get; set; }
    }

    public sealed class CompileMessageDto
    {
        public string State { get; set; }
        public string Path { get; set; }
        public string Description { get; set; }
    }
}
