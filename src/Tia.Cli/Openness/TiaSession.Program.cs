using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>
    /// A station's program as files and back: the 'tia export' and 'tia sync' of a workspace. The
    /// files sit in three folders named like the three in TIA's tree, so the station's folder reads
    /// the way the project does:
    ///
    ///   Program blocks/   SCL, STL and DB blocks as text; LAD, FBD and GRAPH as Openness XML
    ///   PLC data types/   one .udt per type, the text TIA generates and compiles back
    ///   PLC tags/         one .csv per tag table (see TagCsv); TIA's own .xml is taken too
    /// </summary>
    internal sealed partial class TiaSession
    {
        private const string TypeExtension = ".udt";
        private const string TagTableExtension = ".csv";
        /// <summary>A tag table as TIA exports it, constants and every language included (see TagTableXml).</summary>
        private const string TagTableXmlExtension = ".xml";

        // ---------------------------------------------------------------- export

        /// <summary>
        /// Writes the whole program under <paramref name="directory"/>, replacing what the three
        /// folders held, so a block deleted in TIA is a deleted file afterwards. Only those three are
        /// cleared: anything else in the station's folder is not this command's to remove. Like
        /// ExportAllBlocks, a file that will not export is reported and the rest carry on.
        /// </summary>
        public BlockExportAllDto ExportProgram(string deviceName, string directory)
        {
            var software = RequirePlcSoftware(deviceName);
            var result = new BlockExportAllDto
            {
                Directory = Path.GetFullPath(directory),
                Exported = new List<ExportResultDto>(),
                Skipped = new List<BlockProblemDto>(),
                Failed = new List<BlockProblemDto>(),
            };

            var blocks = SafeFileName(software.BlockGroup.Name);
            var types = SafeFileName(software.TypeGroup.Name);
            var tags = SafeFileName(software.TagTableGroup.Name);

            // Cleared only once TIA has answered, so a dead connection deletes nothing.
            foreach (var folder in new[] { blocks, types, tags })
            {
                var path = Path.Combine(result.Directory, folder);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }

            WriteBlocks(software, Path.Combine(result.Directory, blocks), true, null, null, blocks + "/", result);
            WriteTypes(software, software.TypeGroup, Path.Combine(result.Directory, types), types, result);
            WriteTagTables(software.TagTableGroup, Path.Combine(result.Directory, tags), tags, TagTableExtension, result);
            return result;
        }

        private static void WriteTypes(PlcSoftware software, PlcTypeGroup group, string directory, string label,
            BlockExportAllDto result)
        {
            foreach (PlcType type in group.Types)
            {
                var reference = label + "/" + type.Name;
                if (type.IsKnowHowProtected)
                {
                    result.Skipped.Add(new BlockProblemDto { Block = reference, Reason = "know-how protected" });
                    continue;
                }

                try
                {
                    var file = Fresh(Path.Combine(directory, SafeFileName(type.Name) + TypeExtension));
                    software.ExternalSourceGroup.GenerateSource(new[] { (IGenerateSource)type }, file,
                        GenerateOptions.None);
                    file.Refresh();
                    if (!file.Exists)
                        throw new SessionException(WireErrorCodes.OpennessError,
                            $"TIA Portal reported no error but wrote no source for '{type.Name}'.");
                    result.Exported.Add(new ExportResultDto { Name = reference, FilePath = file.FullName, Bytes = file.Length });
                }
                catch (Exception error) when (!AlreadyExplained(error))
                {
                    result.Failed.Add(new BlockProblemDto { Block = reference, Reason = error.Message });
                }
            }

            foreach (PlcTypeUserGroup child in group.Groups)
                WriteTypes(software, child, Path.Combine(directory, SafeFileName(child.Name)), label + "/" + child.Name, result);
        }

        private void WriteTagTables(PlcTagTableGroup group, string directory, string label, string extension,
            BlockExportAllDto result)
        {
            foreach (PlcTagTable table in group.TagTables)
                WriteTagTable(table, Path.Combine(directory, SafeFileName(table.Name) + extension),
                    label + "/" + table.Name, result);

            foreach (PlcTagTableUserGroup child in group.Groups)
                WriteTagTables(child, Path.Combine(directory, SafeFileName(child.Name)), label + "/" + child.Name,
                    extension, result);
        }

        /// <summary>One tag table, as CSV or - when <paramref name="path"/> ends in .xml - as TIA's XML.</summary>
        private void WriteTagTable(PlcTagTable table, string path, string reference, BlockExportAllDto result)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), TagTableXmlExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // TIA's own file holds the constants too, so nothing is left out to report. With
                    // defaults, as tag tables kept in repositories are: without, every attribute at
                    // its default - the HMI flags of nearly every tag - is left out of the file.
                    var xml = Fresh(path);
                    table.Export(xml, ExportOptions.WithDefaults);
                    DropCreatedStamp(xml);
                    xml.Refresh();
                    result.Exported.Add(new ExportResultDto { Name = reference, FilePath = xml.FullName, Bytes = xml.Length });
                    return;
                }

                var rows = table.Tags.Cast<PlcTag>().Select(tag => new TagRow
                {
                    Name = tag.Name,
                    DataType = tag.DataTypeName,
                    Address = tag.LogicalAddress,
                    Comment = EditingText(tag.Comment),
                    HmiVisible = tag.ExternalVisible,
                    HmiAccessible = tag.ExternalAccessible,
                    HmiWriteable = tag.ExternalWritable,
                });

                var file = Fresh(path);
                File.WriteAllText(file.FullName, TagCsv.Write(rows), TagCsv.FileEncoding);
                file.Refresh();
                result.Exported.Add(new ExportResultDto { Name = reference, FilePath = file.FullName, Bytes = file.Length });

                // The CSV holds tags. Constants stay in TIA, and sync leaves them alone.
                if (table.UserConstants.Count > 0)
                    result.Skipped.Add(new BlockProblemDto
                    {
                        Block = reference,
                        Reason = $"{table.UserConstants.Count} user constant(s) not in the CSV; they stay as they are in TIA",
                    });
            }
            catch (Exception error) when (!AlreadyExplained(error))
            {
                result.Failed.Add(new BlockProblemDto { Block = reference, Reason = error.Message });
            }
        }

        // ---------------------------------------------------------------- sync

        private enum Part { Blocks, Types, Tags }

        /// <summary>A file of the station's folder, placed: which part, which folders, which object.</summary>
        private sealed class ProgramFile
        {
            public string Relative;
            public string FullPath;
            public Part Part;
            public List<string> Folders;
            public string Name;
            public string Extension;
        }

        /// <summary>
        /// Brings the files that changed back into the project, and takes out what was deleted. The
        /// order is the order things depend on each other: tag tables and data types before the
        /// blocks that use them, and deletions last, blocks before the types they might use. Each
        /// file stands alone - one that fails is reported, and the rest still go in - and the
        /// external sources a text file goes in through are removed again once their blocks exist.
        /// </summary>
        public ProgramSyncDto SyncProgram(string deviceName, string directory, List<string> changed, List<string> deleted)
        {
            var software = RequirePlcSoftware(deviceName);
            var result = new ProgramSyncDto
            {
                Directory = Path.GetFullPath(directory),
                Applied = new List<SyncedFileDto>(),
                Failed = new List<BlockProblemDto>(),
            };

            var changes = Place(software, result, changed ?? new List<string>());
            var deletions = Place(software, result, deleted ?? new List<string>());
            ApplyAll(software, result, changes, deletions);
            return result;
        }

        private void ApplyAll(PlcSoftware software, ProgramSyncDto result, List<ProgramFile> changes,
            List<ProgramFile> deletions)
        {
            var language = EditingLanguage();

            foreach (var file in changes.Where(f => f.Part == Part.Tags))
                Apply(result, file, "imported", () => ApplyTagTable(software, file, language));
            foreach (var file in changes.Where(f => f.Part == Part.Types))
                Apply(result, file, "imported", () => GenerateFrom(software, file));
            foreach (var file in changes.Where(f => f.Part == Part.Blocks).OrderBy(f => BlockOrder(f.Extension)))
                Apply(result, file, "imported", () => ImportBlock(software, file));

            foreach (var file in deletions.Where(f => f.Part == Part.Blocks))
                Apply(result, file, "deleted", () => RequireBlock(software, string.Join("/", file.Folders.Concat(new[] { file.Name }))).Block.Delete());
            foreach (var file in deletions.Where(f => f.Part == Part.Types))
                Apply(result, file, "deleted", () => FindType(software, file).Delete());
            foreach (var file in deletions.Where(f => f.Part == Part.Tags))
                Apply(result, file, "deleted", () => DeleteTagTable(software, file));
        }

        /// <summary>
        /// Code blocks before data blocks: an instance DB is generated against its FB, and a DB
        /// declared over a type needs the type, so the .db files go in last. Calls between code
        /// blocks need no order - the compile resolves those.
        /// </summary>
        internal static int BlockOrder(string extension)
        {
            switch (extension)
            {
                case ".xml": return 0;
                case ".scl": return 1;
                case ".awl": return 2;
                case ".db": return 3;
                default: return 4;
            }
        }

        private static readonly Dictionary<Part, string[]> PartExtensions = new Dictionary<Part, string[]>
        {
            { Part.Blocks, new[] { ".scl", ".awl", ".db", ".xml" } },
            { Part.Types, new[] { TypeExtension } },
            { Part.Tags, new[] { TagTableExtension, TagTableXmlExtension } },
        };

        /// <summary>
        /// Every file of one part under <paramref name="directory"/> into the project, its folders
        /// becoming folders in TIA's tree - what 'blocks import', 'types import' and 'tables import'
        /// do. Nothing is deleted: a folder of files says what should be there, not what should go.
        /// With <paramref name="underRoot"/> the files are looked for one folder down, in the one
        /// named like TIA's own ("Program blocks"), as the matching export writes them.
        /// </summary>
        public ProgramSyncDto ImportFolder(string deviceName, string directory, string partName, bool underRoot)
        {
            var software = RequirePlcSoftware(deviceName);
            Part part;
            string root;
            switch ((partName ?? "").ToLowerInvariant())
            {
                case "blocks": part = Part.Blocks; root = software.BlockGroup.Name; break;
                case "types": part = Part.Types; root = software.TypeGroup.Name; break;
                case "tables": part = Part.Tags; root = software.TagTableGroup.Name; break;
                default:
                    throw new SessionException(WireErrorCodes.InvalidRequest, $"Nothing to import called '{partName}'.");
            }

            var folder = Path.GetFullPath(underRoot ? Path.Combine(directory, SafeFileName(root)) : directory);
            if (!Directory.Exists(folder))
                throw new SessionException(WireErrorCodes.NotFound, $"No folder {folder} to import from.",
                    "The matching export writes it; --from <dir> names another.");

            var result = new ProgramSyncDto
            {
                Directory = folder,
                Applied = new List<SyncedFileDto>(),
                Failed = new List<BlockProblemDto>(),
            };

            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => PartExtensions[part].Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var relative = f.Substring(folder.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                    var segments = relative.Split('/');
                    return new ProgramFile
                    {
                        Relative = relative,
                        FullPath = f,
                        Part = part,
                        Folders = segments.Take(segments.Length - 1).ToList(),
                        Name = Path.GetFileNameWithoutExtension(f),
                        Extension = Path.GetExtension(f).ToLowerInvariant(),
                    };
                })
                .ToList();

            ApplyAll(software, result, files, new List<ProgramFile>());
            return result;
        }

        private static void Apply(ProgramSyncDto result, ProgramFile file, string action, Action work)
        {
            try
            {
                work();
                result.Applied.Add(new SyncedFileDto { File = file.Relative, Action = action });
            }
            catch (Exception error) when (!AlreadyExplained(error))
            {
                var hint = (error as SessionException)?.Hint;
                result.Failed.Add(new BlockProblemDto
                {
                    Block = file.Relative,
                    Reason = error.Message + (string.IsNullOrEmpty(hint) ? "" : " " + hint),
                });
            }
        }

        /// <summary>Sorts paths relative to the station's folder into the three parts.</summary>
        private static List<ProgramFile> Place(PlcSoftware software, ProgramSyncDto result, List<string> paths)
        {
            var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase)
            {
                { SafeFileName(software.BlockGroup.Name), Part.Blocks },
                { SafeFileName(software.TypeGroup.Name), Part.Types },
                { SafeFileName(software.TagTableGroup.Name), Part.Tags },
            };

            var placed = new List<ProgramFile>();
            foreach (var path in paths)
            {
                var segments = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (segments.Count < 2 || !parts.TryGetValue(segments[0], out var part))
                {
                    result.Failed.Add(new BlockProblemDto
                    {
                        Block = path,
                        Reason = "not under " + string.Join(", ", parts.Keys) + " - nothing in TIA to bring it into",
                    });
                    continue;
                }

                var leaf = segments[segments.Count - 1];
                placed.Add(new ProgramFile
                {
                    Relative = string.Join("/", segments),
                    FullPath = Path.Combine(new[] { result.Directory }.Concat(segments).ToArray()),
                    Part = part,
                    Folders = segments.Skip(1).Take(segments.Count - 2).ToList(),
                    Name = Path.GetFileNameWithoutExtension(leaf),
                    Extension = Path.GetExtension(leaf).ToLowerInvariant(),
                });
            }
            return placed;
        }

        private static void ImportBlock(PlcSoftware software, ProgramFile file)
        {
            var folder = ResolveFolder(software, string.Join("/", file.Folders), create: true);
            if (file.Extension == ".xml")
            {
                folder.Blocks.Import(new FileInfo(file.FullPath), ImportOptions.Override);
                return;
            }

            GenerateThroughSource(software, file, source => folder is PlcBlockUserGroup user
                ? source.GenerateBlocksFromSource(user, GenerateBlockOption.KeepOnError)
                : source.GenerateBlocksFromSource(GenerateBlockOption.KeepOnError));
        }

        private static void GenerateFrom(PlcSoftware software, ProgramFile file)
        {
            PlcTypeGroup folder = software.TypeGroup;
            foreach (var segment in file.Folders)
                folder = folder.Groups.Find(segment) ?? folder.Groups.Create(segment);

            GenerateThroughSource(software, file, source => folder is PlcTypeUserGroup user
                ? source.GenerateBlocksFromSource(user, GenerateBlockOption.KeepOnError)
                : source.GenerateBlocksFromSource(GenerateBlockOption.KeepOnError));
        }

        /// <summary>
        /// Text becomes a block or a type only by way of an external source. The source is a means,
        /// not a result, so it goes again afterwards - a sync of fifty files would otherwise leave
        /// fifty entries under "External source files".
        /// </summary>
        private static void GenerateThroughSource(PlcSoftware software, ProgramFile file,
            Func<PlcExternalSource, IList<IEngineeringObject>> generate)
        {
            if (!File.Exists(file.FullPath))
                throw new SessionException(WireErrorCodes.NotFound, $"Not found: {file.FullPath}");

            var sources = software.ExternalSourceGroup.ExternalSources;
            var name = Path.GetFileName(file.FullPath);
            sources.Find(name)?.Delete();
            var source = sources.CreateFromFile(name, file.FullPath);
            try
            {
                var made = generate(source);
                if (made == null || made.Count == 0)
                    throw new SessionException(WireErrorCodes.OpennessError,
                        $"{name} produced nothing. It almost certainly has a syntax error; the compile names it.");
            }
            finally
            {
                try { source.Delete(); } catch (EngineeringException) { /* a leftover source is harmless */ }
            }
        }

        private static PlcType FindType(PlcSoftware software, ProgramFile file)
        {
            PlcTypeGroup folder = software.TypeGroup;
            foreach (var segment in file.Folders)
                folder = folder.Groups.Find(segment) ?? throw new SessionException(WireErrorCodes.NotFound,
                    $"No data type folder '{segment}'.");
            return folder.Types.Find(file.Name) ?? throw new SessionException(WireErrorCodes.NotFound,
                $"No data type '{file.Name}' to delete; it is gone already.");
        }

        private static PlcTagTableGroup TagFolder(PlcSoftware software, ProgramFile file, bool create)
        {
            PlcTagTableGroup folder = software.TagTableGroup;
            foreach (var segment in file.Folders)
            {
                var next = folder.Groups.Find(segment);
                if (next == null && !create)
                    throw new SessionException(WireErrorCodes.NotFound, $"No tag table folder '{segment}'.");
                folder = next ?? folder.Groups.Create(segment);
            }
            return folder;
        }

        /// <summary>
        /// Makes the table hold exactly the tags its CSV lists: missing ones created, the rest
        /// brought into line, and tags the CSV no longer has deleted. User constants are not in the
        /// CSV and are not touched. A tag renamed in the file is a delete and a create here, as it
        /// would be in the file's own diff.
        /// </summary>
        private static void ApplyTagTable(PlcSoftware software, ProgramFile file, Language language)
        {
            if (file.Extension != TagTableExtension && file.Extension != TagTableXmlExtension)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "A tag table is a .csv file, or the .xml TIA Portal exports.");
            if (!File.Exists(file.FullPath))
                throw new SessionException(WireErrorCodes.NotFound, $"Not found: {file.FullPath}");
            if (file.Extension == TagTableXmlExtension)
            {
                ImportTagTableXml(software, file);
                return;
            }

            List<TagRow> rows;
            try
            {
                rows = TagCsv.Read(File.ReadAllText(file.FullPath, TagCsv.FileEncoding));
            }
            catch (WireException error)
            {
                throw new SessionException(error.Code, error.Message, error.Hint);
            }

            var folder = TagFolder(software, file, create: true);
            var table = folder.TagTables.Find(file.Name) ?? folder.TagTables.Create(file.Name);

            var wanted = new HashSet<string>(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in table.Tags.Cast<PlcTag>().Where(t => !wanted.Contains(t.Name)).ToList())
                stale.Delete();

            foreach (var row in rows)
            {
                var tag = table.Tags.Find(row.Name)
                          ?? (row.Address == null
                              ? table.Tags.Create(row.Name)
                              : table.Tags.Create(row.Name, row.DataType, row.Address));

                if (!string.IsNullOrEmpty(row.DataType) &&
                    !string.Equals(tag.DataTypeName, row.DataType, StringComparison.OrdinalIgnoreCase))
                    tag.DataTypeName = row.DataType;
                if (row.Address != null &&
                    !string.Equals(tag.LogicalAddress, row.Address, StringComparison.OrdinalIgnoreCase))
                    tag.LogicalAddress = row.Address;
                if (tag.ExternalVisible != row.HmiVisible) tag.ExternalVisible = row.HmiVisible;
                if (tag.ExternalAccessible != row.HmiAccessible) tag.ExternalAccessible = row.HmiAccessible;
                if (tag.ExternalWritable != row.HmiWriteable) tag.ExternalWritable = row.HmiWriteable;
                SetEditingText(tag.Comment, language, row.Comment ?? "");
            }
        }

        /// <summary>
        /// A tag table from TIA's own XML, replacing the table of the same name: its tags and user
        /// constants become the file's, and so do the comments in every language. The name is the
        /// one in the file. A table TIA already has is replaced in the folder it is in, wherever the
        /// file sits - names are unique across the folders, so one elsewhere would be refused.
        /// </summary>
        private static void ImportTagTableXml(PlcSoftware software, ProgramFile file)
        {
            string name;
            try
            {
                name = TagTableXml.NameOf(XDocument.Load(file.FullPath));
            }
            catch (XmlException error)
            {
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"{Path.GetFileName(file.FullPath)} is not XML: {error.Message}");
            }
            if (name == null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"{Path.GetFileName(file.FullPath)} holds no tag table: no {TagTableXml.TableElement} with a name.",
                    "'tia table export <station> <table> --xml' writes one.");

            var folder = FolderHolding(software.TagTableGroup, name) ?? TagFolder(software, file, create: true);
            folder.TagTables.Import(new FileInfo(file.FullPath), ImportOptions.Override);
        }

        /// <summary>The folder the tag table <paramref name="name"/> is in, or null when there is none.</summary>
        private static PlcTagTableGroup FolderHolding(PlcTagTableGroup group, string name)
        {
            if (group.TagTables.Find(name) != null) return group;
            foreach (PlcTagTableUserGroup child in group.Groups)
            {
                var found = FolderHolding(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void DeleteTagTable(PlcSoftware software, ProgramFile file)
        {
            var table = TagFolder(software, file, create: false).TagTables.Find(file.Name)
                        ?? throw new SessionException(WireErrorCodes.NotFound,
                            $"No tag table '{file.Name}' to delete; it is gone already.");
            if (table.IsDefault)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"'{table.Name}' is the default tag table, which TIA does not let go.");
            table.Delete();
        }

        // ---------------------------------------------------------------- one part on its own

        /// <summary>
        /// Every tag table of the station under <paramref name="directory"/>, in its folders: as CSV,
        /// or with <paramref name="xml"/> as TIA's own XML. Nothing there is deleted first; that is
        /// 'tia export', which owns its folder.
        /// </summary>
        public BlockExportAllDto ExportTagTables(string deviceName, string directory, bool underRoot, bool xml)
        {
            var software = RequirePlcSoftware(deviceName);
            var result = NewExport(underRoot
                ? Path.Combine(directory, SafeFileName(software.TagTableGroup.Name))
                : directory);
            WriteTagTables(software.TagTableGroup, result.Directory, SafeFileName(software.TagTableGroup.Name),
                xml ? TagTableXmlExtension : TagTableExtension, result);
            return result;
        }

        public ExportResultDto ExportTagTable(string deviceName, string tableName, string targetPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var table = EnumerateTagTables(software.TagTableGroup).FirstOrDefault(t =>
                            string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new SessionException(WireErrorCodes.NotFound, $"Tag table '{tableName}' not found.",
                            "'tia tables' lists them.");

            // Written through the same code as the whole set, so a single file is the same file.
            var result = NewExport(Path.GetDirectoryName(Path.GetFullPath(targetPath)));
            WriteTagTable(table, Path.GetFullPath(targetPath), table.Name, result);
            if (result.Failed.Count > 0)
                throw new SessionException(WireErrorCodes.OpennessError, result.Failed[0].Reason);
            return result.Exported[0];
        }

        /// <summary>
        /// A tag table from its CSV: made to hold exactly the tags the file lists, created when it
        /// is not there yet. The table is named after the file unless <paramref name="tableName"/>
        /// says otherwise. From TIA's XML instead, the table the file names is replaced by it.
        /// </summary>
        public SyncedFileDto ImportTagTable(string deviceName, string filePath, string tableName, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var file = Standalone(filePath, tableName, folderPath);
            if (file.Extension == TagTableXmlExtension && !string.IsNullOrWhiteSpace(tableName))
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "An XML tag table names itself; --name does not apply to it.",
                    "Change the <Name> in the file to import it under another name.");
            ApplyTagTable(software, file, EditingLanguage());
            return new SyncedFileDto { File = file.FullPath, Action = "imported" };
        }

        public List<TypeDto> ListTypes(string deviceName)
        {
            var software = RequirePlcSoftware(deviceName);
            var types = new List<TypeDto>();
            CollectTypes(software.TypeGroup, software.TypeGroup.Name, types);
            return types;
        }

        private static void CollectTypes(PlcTypeGroup group, string path, List<TypeDto> sink)
        {
            foreach (PlcType type in group.Types)
                sink.Add(new TypeDto
                {
                    Name = type.Name,
                    Path = path,
                    IsConsistent = type.IsConsistent,
                    IsKnowHowProtected = type.IsKnowHowProtected,
                    ModifiedDate = Iso(type.ModifiedDate),
                });
            foreach (PlcTypeUserGroup child in group.Groups)
                CollectTypes(child, path + "/" + child.Name, sink);
        }

        public BlockExportAllDto ExportTypes(string deviceName, string directory, bool underRoot)
        {
            var software = RequirePlcSoftware(deviceName);
            var result = NewExport(underRoot
                ? Path.Combine(directory, SafeFileName(software.TypeGroup.Name))
                : directory);
            WriteTypes(software, software.TypeGroup, result.Directory, SafeFileName(software.TypeGroup.Name), result);
            return result;
        }

        public ExportResultDto ExportType(string deviceName, string typeName, string targetPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var type = AllTypes(software.TypeGroup).FirstOrDefault(t =>
                           string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new SessionException(WireErrorCodes.NotFound, $"Data type '{typeName}' not found.",
                           "'tia types' lists them.");
            if (type.IsKnowHowProtected)
                throw new SessionException(WireErrorCodes.AccessDenied,
                    $"Data type '{type.Name}' is know-how protected and cannot be exported.");

            var file = Fresh(targetPath);
            software.ExternalSourceGroup.GenerateSource(new[] { (IGenerateSource)type }, file, GenerateOptions.None);
            file.Refresh();
            if (!file.Exists)
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"TIA Portal reported no error but wrote no source for '{type.Name}'.");
            return new ExportResultDto { Name = type.Name, FilePath = file.FullName, Bytes = file.Length };
        }

        /// <summary>A data type from its .udt text, generated into <paramref name="folderPath"/>.</summary>
        public SyncedFileDto ImportType(string deviceName, string filePath, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var file = Standalone(filePath, null, folderPath);
            GenerateFrom(software, file);
            return new SyncedFileDto { File = file.FullPath, Action = "imported" };
        }

        private static IEnumerable<PlcType> AllTypes(PlcTypeGroup group)
        {
            foreach (PlcType type in group.Types) yield return type;
            foreach (PlcTypeUserGroup child in group.Groups)
                foreach (var type in AllTypes(child)) yield return type;
        }

        private static BlockExportAllDto NewExport(string directory) => new BlockExportAllDto
        {
            Directory = Path.GetFullPath(directory),
            Exported = new List<ExportResultDto>(),
            Skipped = new List<BlockProblemDto>(),
            Failed = new List<BlockProblemDto>(),
        };

        /// <summary>A file named on the command line, placed the way a synced one is.</summary>
        private static ProgramFile Standalone(string filePath, string name, string folderPath)
        {
            var full = Path.GetFullPath(filePath);
            return new ProgramFile
            {
                Relative = Path.GetFileName(full),
                FullPath = full,
                Folders = (folderPath ?? "").Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(full) : name,
                Extension = Path.GetExtension(full).ToLowerInvariant(),
            };
        }

        // ---------------------------------------------------------------- comments

        private Language EditingLanguage()
        {
            try { return RequireProject().LanguageSettings.EditingLanguage; }
            catch (EngineeringException) { return null; }
        }

        /// <summary>
        /// The comment in the language the project is edited in, which is the one the CSV carries;
        /// a project that has only ever been written in another falls back to whatever there is.
        /// </summary>
        private string EditingText(MultilingualText text)
        {
            if (text == null) return null;
            var language = EditingLanguage();
            if (language != null)
            {
                foreach (MultilingualTextItem item in text.Items)
                    if (Equals(item.Language, language)) return NullIfEmpty(item.Text);
            }
            return FirstText(text);
        }

        private static void SetEditingText(MultilingualText text, Language language, string value)
        {
            if (text == null) return;
            MultilingualTextItem target = null;
            foreach (MultilingualTextItem item in text.Items)
            {
                if (language != null && Equals(item.Language, language)) { target = item; break; }
                if (target == null) target = item;
            }
            if (target != null && !string.Equals(target.Text ?? "", value, StringComparison.Ordinal))
                target.Text = value;
        }
    }
}
