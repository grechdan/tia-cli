using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>
    /// The program-blocks half of the session: listing and reading blocks, moving them in and out of
    /// the project as XML or SCL, and the external sources that are the only way text becomes code.
    /// </summary>
    internal sealed partial class TiaSession
    {
        // ---------------------------------------------------------------- listing

        public List<BlockDto> ListBlocks(string deviceName, string nameFilter, string typeFilter,
            bool includeSystemGroups)
        {
            var software = RequirePlcSoftware(deviceName);
            return Handles(software, includeSystemGroups)
                .Select(h => Describe(h.Block, h.Folder))
                .Where(b => Matches(b, nameFilter, typeFilter))
                .ToList();
        }

        /// <summary>
        /// The same blocks as <see cref="ListBlocks"/>, but keeping the folders they sit in. Folders
        /// that end up with nothing in them after filtering are dropped, so a filtered tree shows
        /// only the way to what matched.
        /// </summary>
        public BlockTreeNodeDto BlockTree(string deviceName, string nameFilter, string typeFilter,
            bool includeSystemGroups)
        {
            var software = RequirePlcSoftware(deviceName);
            var root = software.BlockGroup;

            var tree = BuildTree(root, root.Name, nameFilter, typeFilter, includeSystemGroups);

            // The root always comes back, even empty: "nothing matched" is an answer, not an absence.
            return tree ?? new BlockTreeNodeDto
            {
                Kind = "folder",
                Name = root.Name,
                Path = root.Name,
                Children = new List<BlockTreeNodeDto>(),
            };
        }

        /// <summary>
        /// Everything about one block. The header attributes come off the Openness object; the
        /// interface and the networks exist nowhere but the export XML, so the block is exported to a
        /// temporary file and read back. That is the whole trick, and it is why this takes a moment.
        /// </summary>
        public BlockDetailDto DescribeBlock(string deviceName, string blockPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var handle = RequireBlock(software, blockPath);
            var block = handle.Block;

            var detail = new BlockDetailDto
            {
                Name = block.Name,
                Path = handle.Folder,
                BlockType = BlockTypeOf(block),
                Number = block.Number,
                AutoNumber = block.AutoNumber,
                Language = block.ProgrammingLanguage.ToString(),
                Namespace = string.IsNullOrEmpty(block.Namespace) ? null : block.Namespace,
                SecondaryType = (block as OB)?.SecondaryType,
                InstanceOfName = (block as InstanceDB)?.InstanceOfName,
                MemoryLayout = block.MemoryLayout.ToString(),
                IsConsistent = block.IsConsistent,
                IsKnowHowProtected = block.IsKnowHowProtected,
                HeaderAuthor = NullIfEmpty(block.HeaderAuthor),
                HeaderFamily = NullIfEmpty(block.HeaderFamily),
                HeaderName = NullIfEmpty(block.HeaderName),
                HeaderVersion = block.HeaderVersion?.ToString(),
                CreationDate = Iso(block.CreationDate),
                ModifiedDate = Iso(block.ModifiedDate),
                CompileDate = Iso(block.CompileDate),
                Interface = new List<InterfaceMemberDto>(),
                Networks = new List<NetworkDto>(),
            };

            // A protected block still has readable attributes; only its body is sealed.
            if (block.IsKnowHowProtected) return detail;

            var scratch = ScratchFile(block.Name + ".xml");
            try
            {
                block.Export(scratch, ExportOptions.None);
                BlockXml.ReadInto(XDocument.Load(scratch.FullName), detail);
            }
            finally
            {
                TryDelete(scratch);
            }

            return detail;
        }

        // ---------------------------------------------------------------- in and out

        public ExportResultDto ExportBlock(string deviceName, string blockPath, string targetPath,
            bool inline, int maxInlineChars)
        {
            var software = RequirePlcSoftware(deviceName);
            var block = RequireBlock(software, blockPath).Block;

            if (block.IsKnowHowProtected)
                throw new SessionException(WireErrorCodes.AccessDenied,
                    $"Block '{block.Name}' is know-how protected and cannot be exported.");

            var file = Fresh(targetPath);
            block.Export(file, ExportOptions.None);
            file.Refresh();

            return Read(block.Name, file, inline, maxInlineChars);
        }

        /// <summary>
        /// Exports a block as text - SCL, STL or a DB declaration, whichever it is written in. This
        /// is the readable counterpart to the XML export, and the form you can edit and import back
        /// with 'source add'. Blocks drawn in LAD, FBD or GRAPH have no textual form; TIA refuses.
        /// </summary>
        public ExportResultDto ExportBlockSource(string deviceName, string blockPath, string targetPath,
            bool withDependencies, bool inline, int maxInlineChars)
        {
            var software = RequirePlcSoftware(deviceName);
            var block = RequireBlock(software, blockPath).Block;

            if (block.IsKnowHowProtected)
                throw new SessionException(WireErrorCodes.AccessDenied,
                    $"Block '{block.Name}' is know-how protected and cannot be exported.");

            var file = Fresh(string.IsNullOrEmpty(targetPath)
                ? Path.Combine(Path.GetTempPath(), "tia-cli-src", block.Name + SourceExtension(block))
                : targetPath);

            try
            {
                software.ExternalSourceGroup.GenerateSource(new[] { (IGenerateSource)block }, file,
                    withDependencies ? GenerateOptions.WithDependencies : GenerateOptions.None);
            }
            catch (EngineeringException error) when (!AlreadyExplained(error))
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"TIA Portal would not write '{block.Name}' out as source: {error.Message}",
                    $"Only textual blocks have a source form. This one is {block.ProgrammingLanguage}; " +
                    "use 'tia block export' for its XML instead.");
            }

            file.Refresh();
            if (!file.Exists)
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"TIA Portal reported no error but wrote no source for '{block.Name}'.");

            return Read(block.Name, file, inline, maxInlineChars);
        }

        /// <summary>
        /// Imports blocks from an Openness XML file. TIA refuses to overwrite silently, so replacing
        /// what is already there is a deliberate flag rather than the default.
        /// </summary>
        public BlockImportDto ImportBlocks(string deviceName, string filePath, string folderPath,
            bool overwrite)
        {
            var software = RequirePlcSoftware(deviceName);

            var file = new FileInfo(Path.GetFullPath(filePath));
            if (!file.Exists)
                throw new SessionException(WireErrorCodes.NotFound, $"Import file not found: {file.FullName}");

            var folder = ResolveFolder(software, folderPath, create: true);

            IList<PlcBlock> imported;
            try
            {
                imported = folder.Blocks.Import(file,
                    overwrite ? ImportOptions.Override : ImportOptions.None);
            }
            catch (EngineeringException error) when (!AlreadyExplained(error))
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    "The import failed: " + error.Message,
                    "A block of the same name already there is the usual cause - pass --overwrite. " +
                    "Otherwise the XML was written for a different TIA version, or names something " +
                    "this PLC has not got.");
            }

            var path = FolderPath(folder);
            return new BlockImportDto
            {
                FilePath = file.FullName,
                Folder = path,
                Overwrote = overwrite,
                Blocks = (imported ?? new List<PlcBlock>()).Select(b => Describe(b, path)).ToList(),
            };
        }

        // ---------------------------------------------------------------- editing

        public BlockDto RenameBlock(string deviceName, string blockPath, string newName)
        {
            var software = RequirePlcSoftware(deviceName);
            var handle = RequireBlock(software, blockPath);

            if (string.Equals(handle.Block.Name, newName, StringComparison.Ordinal))
                return Describe(handle.Block, handle.Folder);

            try
            {
                handle.Block.Name = newName;
            }
            catch (EngineeringException error) when (!AlreadyExplained(error))
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"Could not rename '{handle.Block.Name}' to '{newName}': {error.Message}",
                    "Another block of that name, or a name TIA does not allow, are the two reasons.");
            }

            // Nothing that calls the block is rewritten by this; only a compile will say what broke.
            return Describe(handle.Block, handle.Folder);
        }

        public BlockDto DeleteBlock(string deviceName, string blockPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var handle = RequireBlock(software, blockPath);

            // Described before deleting: afterwards the handle is a corpse and every read throws.
            var gone = Describe(handle.Block, handle.Folder);

            try
            {
                handle.Block.Delete();
            }
            catch (EngineeringException error) when (!AlreadyExplained(error))
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"Could not delete '{gone.Name}': {error.Message}",
                    "System blocks and blocks belonging to a technology object cannot be deleted here.");
            }

            return gone;
        }

        public BlockFolderDto CreateBlockFolder(string deviceName, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new SessionException(WireErrorCodes.InvalidRequest, "Give the folder to create.");

            return Describe(ResolveFolder(software, folderPath, create: true));
        }

        public BlockFolderDto DeleteBlockFolder(string deviceName, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var folder = ResolveFolder(software, folderPath, create: false);

            var user = folder as PlcBlockUserGroup;
            if (user == null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"'{FolderPath(folder)}' is the root folder and cannot be deleted.");

            var gone = Describe(user);
            user.Delete();
            return gone;
        }

        // ---------------------------------------------------------------- external sources

        public List<SourceDto> ListSources(string deviceName)
        {
            var software = RequirePlcSoftware(deviceName);
            var group = software.ExternalSourceGroup;
            var sources = new List<SourceDto>();

            CollectSources(group.ExternalSources.Cast<PlcExternalSource>(),
                group.Groups.Cast<PlcExternalSourceUserGroup>(), group.Name, sources);

            return sources;
        }

        /// <summary>
        /// Writes SCL to a file, registers it as an external source and asks TIA to compile it into
        /// blocks. This is the only route from SCL text to a real block: block XML import needs the
        /// full Openness schema, and CreateFB makes an empty block with no way to set its body.
        /// </summary>
        public SourceImportDto ImportScl(string deviceName, string sourceName, string code,
            string filePath, bool generate, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);

            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(filePath))
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "Provide either the SCL text or a path to an .scl file.");

            var name = WithSourceExtension(sourceName);

            string path;
            if (!string.IsNullOrEmpty(code))
            {
                // The BOM is suppressed: TIA reports a syntax error on the first line when present.
                path = Path.Combine(Path.GetTempPath(), "tia-cli-src", name);
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, code, new UTF8Encoding(false));
            }
            else
            {
                path = Path.GetFullPath(filePath);
                if (!File.Exists(path))
                    throw new SessionException(WireErrorCodes.NotFound, $"Source file not found: {path}");
            }

            var sources = software.ExternalSourceGroup.ExternalSources;

            // Openness refuses to add a second source under the same name; replacing makes repeated
            // edit-and-retry cycles work, which is the normal way code gets written.
            var previous = sources.Find(name);
            if (previous != null) previous.Delete();

            var source = sources.CreateFromFile(name, path);

            var result = new SourceImportDto
            {
                SourceName = source.Name,
                FilePath = path,
                Generated = generate,
                GeneratedBlocks = new List<string>(),
            };

            if (generate) Generate(software, source, folderPath, result);
            return result;
        }

        /// <summary>Compiles a source already in the project into blocks, without re-importing it.</summary>
        public SourceImportDto GenerateFromSource(string deviceName, string sourceName, string folderPath)
        {
            var software = RequirePlcSoftware(deviceName);
            var source = RequireSource(software, sourceName);

            var result = new SourceImportDto
            {
                SourceName = source.Name,
                Generated = true,
                GeneratedBlocks = new List<string>(),
            };

            Generate(software, source, folderPath, result);
            return result;
        }

        public SourceDto DeleteSource(string deviceName, string sourceName)
        {
            var software = RequirePlcSoftware(deviceName);
            var source = RequireSource(software, sourceName);

            var gone = new SourceDto { Name = source.Name, Path = SourceFolderPath(source) };
            source.Delete();
            return gone;
        }

        private static void Generate(PlcSoftware software, PlcExternalSource source, string folderPath,
            SourceImportDto result)
        {
            PlcBlockUserGroup folder = null;
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                folder = ResolveFolder(software, folderPath, create: true) as PlcBlockUserGroup;
                if (folder == null)
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        "--folder named the root; generated blocks go there anyway if you leave it off.");
            }

            // KeepOnError leaves whatever compiled behind instead of rolling everything back, so a
            // partially valid source still shows the caller how far it got.
            var blocks = folder == null
                ? source.GenerateBlocksFromSource(GenerateBlockOption.KeepOnError)
                : source.GenerateBlocksFromSource(folder, GenerateBlockOption.KeepOnError);

            if (blocks != null)
            {
                foreach (var generated in blocks)
                {
                    var block = generated as PlcBlock;
                    if (block != null) result.GeneratedBlocks.Add(block.Name);
                }
            }

            result.Folder = folder == null ? software.BlockGroup.Name : FolderPath(folder);

            if (result.GeneratedBlocks.Count == 0)
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"Source '{source.Name}' produced no blocks. The SCL almost certainly has a syntax " +
                    "error; open the source in TIA Portal to see the compiler message.");
        }

        // ---------------------------------------------------------------- finding things

        /// <summary>A block together with the folder path it was found under.</summary>
        private struct BlockHandle
        {
            public PlcBlock Block;
            public string Folder;
        }

        private static List<BlockHandle> Handles(PlcSoftware software, bool includeSystemGroups)
        {
            var sink = new List<BlockHandle>();
            Walk(software.BlockGroup, software.BlockGroup.Name, sink, includeSystemGroups);
            return sink;
        }

        private static void Walk(PlcBlockGroup group, string path, List<BlockHandle> sink,
            bool includeSystemGroups)
        {
            foreach (PlcBlock block in group.Blocks)
                sink.Add(new BlockHandle { Block = block, Folder = path });

            foreach (PlcBlockUserGroup child in group.Groups)
                Walk(child, path + "/" + child.Name, sink, includeSystemGroups);

            var system = group as PlcBlockSystemGroup;
            if (includeSystemGroups && system != null)
            {
                foreach (PlcSystemBlockGroup child in system.SystemBlockGroups)
                    WalkSystem(child, path + "/" + child.Name, sink);
            }
        }

        // PlcSystemBlockGroup is not a PlcBlockGroup - it is a parallel type with its own Groups
        // composition, so the traversal cannot be shared with Walk.
        private static void WalkSystem(PlcSystemBlockGroup group, string path, List<BlockHandle> sink)
        {
            foreach (PlcBlock block in group.Blocks)
                sink.Add(new BlockHandle { Block = block, Folder = path });

            foreach (PlcSystemBlockGroup child in group.Groups)
                WalkSystem(child, path + "/" + child.Name, sink);
        }

        /// <summary>
        /// Accepts a bare name ("MyFB"), a folder-qualified path ("Pumps/MyFB") or a full one with
        /// the root folder in front of it. A bare name held by two blocks is refused rather than
        /// guessed at: the wrong one is a silent wrong answer.
        /// </summary>
        private static BlockHandle RequireBlock(PlcSoftware software, string blockPath)
        {
            var root = software.BlockGroup.Name;
            var wanted = Normalize(blockPath, root);
            if (wanted.Length == 0)
                throw new SessionException(WireErrorCodes.InvalidRequest, "Give a block name.");

            var all = Handles(software, includeSystemGroups: true);

            var qualified = all.FirstOrDefault(h => string.Equals(
                Normalize(h.Folder + "/" + h.Block.Name, root), wanted,
                StringComparison.OrdinalIgnoreCase));
            if (qualified.Block != null) return qualified;

            var leaf = wanted.Substring(wanted.LastIndexOf('/') + 1);
            var byName = all.Where(h =>
                string.Equals(h.Block.Name, leaf, StringComparison.OrdinalIgnoreCase)).ToList();

            // A folder was named and nothing is there under that name. Falling back to the block of
            // that name somewhere else would act on a block the caller did not ask for - and said so
            // explicitly, by writing a path - so this reports where it actually is instead.
            if (wanted.Contains("/"))
                throw new SessionException(WireErrorCodes.NotFound,
                    $"No block '{leaf}' in '{wanted.Substring(0, wanted.LastIndexOf('/'))}'.",
                    byName.Count == 0
                        ? null
                        : "It is in " + string.Join(", ", byName.Select(h => h.Folder)) + ".");

            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1)
                throw new SessionException(WireErrorCodes.Ambiguous,
                    $"Block '{leaf}' is in {byName.Count} folders. Qualify it: " +
                    string.Join(", ", byName.Select(h => h.Folder + "/" + h.Block.Name)));

            throw new SessionException(WireErrorCodes.NotFound, $"Block '{blockPath}' not found.");
        }

        /// <summary>
        /// Walks to a folder under the program blocks, making the missing part of the path as it goes
        /// when asked to. An empty path means the root folder.
        /// </summary>
        private static PlcBlockGroup ResolveFolder(PlcSoftware software, string folderPath, bool create)
        {
            PlcBlockGroup folder = software.BlockGroup;

            foreach (var segment in Segments(folderPath, software.BlockGroup.Name))
            {
                var next = folder.Groups.Cast<PlcBlockUserGroup>().FirstOrDefault(g =>
                    string.Equals(g.Name, segment, StringComparison.OrdinalIgnoreCase));

                if (next == null)
                {
                    if (!create)
                        throw new SessionException(WireErrorCodes.NotFound,
                            $"No folder '{segment}' under '{FolderPath(folder)}'.");
                    next = folder.Groups.Create(segment);
                }

                folder = next;
            }

            return folder;
        }

        private static PlcExternalSource RequireSource(PlcSoftware software, string sourceName)
        {
            var group = software.ExternalSourceGroup;
            var candidates = new List<PlcExternalSource>();
            CollectSourceHandles(group.ExternalSources.Cast<PlcExternalSource>(),
                group.Groups.Cast<PlcExternalSourceUserGroup>(), candidates);

            var wanted = sourceName.Replace('\\', '/');
            wanted = wanted.Contains("/") ? wanted.Substring(wanted.LastIndexOf('/') + 1) : wanted;

            // The extension is part of the name in the project, but nobody types it.
            var found = candidates.FirstOrDefault(s =>
                            string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault(s => string.Equals(
                            s.Name, WithSourceExtension(wanted), StringComparison.OrdinalIgnoreCase));

            if (found == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    $"External source '{sourceName}' not found.",
                    "'tia sources <device>' lists them.");

            return found;
        }

        private static void CollectSourceHandles(IEnumerable<PlcExternalSource> sources,
            IEnumerable<PlcExternalSourceUserGroup> groups, List<PlcExternalSource> sink)
        {
            sink.AddRange(sources);
            foreach (var group in groups)
                CollectSourceHandles(group.ExternalSources.Cast<PlcExternalSource>(),
                    group.Groups.Cast<PlcExternalSourceUserGroup>(), sink);
        }

        private static void CollectSources(IEnumerable<PlcExternalSource> sources,
            IEnumerable<PlcExternalSourceUserGroup> groups, string path, List<SourceDto> sink)
        {
            foreach (var source in sources)
                sink.Add(new SourceDto { Name = source.Name, Path = path });

            foreach (var group in groups)
                CollectSources(group.ExternalSources.Cast<PlcExternalSource>(),
                    group.Groups.Cast<PlcExternalSourceUserGroup>(), path + "/" + group.Name, sink);
        }

        private static string SourceFolderPath(PlcExternalSource source)
        {
            var segments = new List<string>();
            var current = source.Parent;

            while (current != null)
            {
                var userGroup = current as PlcExternalSourceUserGroup;
                if (userGroup != null)
                {
                    segments.Insert(0, userGroup.Name);
                    current = userGroup.Parent;
                    continue;
                }

                var systemGroup = current as PlcExternalSourceSystemGroup;
                if (systemGroup != null) segments.Insert(0, systemGroup.Name);
                break;
            }

            return string.Join("/", segments);
        }

        // ---------------------------------------------------------------- plumbing

        private static bool Matches(BlockDto block, string nameFilter, string typeFilter)
        {
            if (!string.IsNullOrEmpty(nameFilter) &&
                block.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (string.IsNullOrEmpty(typeFilter)) return true;

            // "DB" is how people ask for data blocks, and it has to cover both kinds of them.
            return typeFilter.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Any(t => string.Equals(block.BlockType, t, StringComparison.OrdinalIgnoreCase) ||
                          (string.Equals(t, "DB", StringComparison.OrdinalIgnoreCase) &&
                           block.BlockType.EndsWith("DB", StringComparison.OrdinalIgnoreCase)));
        }

        private static BlockTreeNodeDto BuildTree(PlcBlockGroup group, string path,
            string nameFilter, string typeFilter, bool includeSystemGroups)
        {
            var node = Folder(group.Name, path);

            foreach (PlcBlockUserGroup child in group.Groups)
                Add(node, BuildTree(child, path + "/" + child.Name, nameFilter, typeFilter,
                    includeSystemGroups));

            var system = group as PlcBlockSystemGroup;
            if (includeSystemGroups && system != null)
            {
                foreach (PlcSystemBlockGroup child in system.SystemBlockGroups)
                    Add(node, BuildSystemTree(child, path + "/" + child.Name, nameFilter, typeFilter));
            }

            AddBlocks(node, group.Blocks.Cast<PlcBlock>(), path, nameFilter, typeFilter);
            return Prune(node, nameFilter, typeFilter);
        }

        // Same shape, different types: PlcSystemBlockGroup shares no base class with PlcBlockGroup,
        // so the recursion has to be written twice rather than parameterised.
        private static BlockTreeNodeDto BuildSystemTree(PlcSystemBlockGroup group, string path,
            string nameFilter, string typeFilter)
        {
            var node = Folder(group.Name, path);

            foreach (PlcSystemBlockGroup child in group.Groups)
                Add(node, BuildSystemTree(child, path + "/" + child.Name, nameFilter, typeFilter));

            AddBlocks(node, group.Blocks.Cast<PlcBlock>(), path, nameFilter, typeFilter);
            return Prune(node, nameFilter, typeFilter);
        }

        private static BlockTreeNodeDto Folder(string name, string path) => new BlockTreeNodeDto
        {
            Kind = "folder",
            Name = name,
            Path = path,
            Children = new List<BlockTreeNodeDto>(),
        };

        private static void Add(BlockTreeNodeDto parent, BlockTreeNodeDto child)
        {
            if (child != null) parent.Children.Add(child);
        }

        private static void AddBlocks(BlockTreeNodeDto node, IEnumerable<PlcBlock> blocks, string path,
            string nameFilter, string typeFilter)
        {
            foreach (var block in blocks)
            {
                var described = Describe(block, path);
                if (!Matches(described, nameFilter, typeFilter)) continue;

                node.Children.Add(new BlockTreeNodeDto
                {
                    Kind = "block",
                    Name = described.Name,
                    Path = path,
                    Block = described,
                });
            }
        }

        /// <summary>A folder that nothing matched in is not part of the answer to a filtered ask.</summary>
        private static BlockTreeNodeDto Prune(BlockTreeNodeDto node, string nameFilter, string typeFilter)
        {
            var filtering = !string.IsNullOrEmpty(nameFilter) || !string.IsNullOrEmpty(typeFilter);
            return filtering && node.Children.Count == 0 ? null : node;
        }

        private static string FolderPath(PlcBlockGroup folder)
        {
            var segments = new List<string>();
            IEngineeringObject current = folder;

            while (current is PlcBlockGroup)
            {
                var group = (PlcBlockGroup)current;
                segments.Insert(0, group.Name);
                current = group.Parent;
            }

            return string.Join("/", segments);
        }

        private static BlockFolderDto Describe(PlcBlockGroup folder) => new BlockFolderDto
        {
            Name = folder.Name,
            Path = FolderPath(folder),
            BlockCount = folder.Blocks.Count,
            FolderCount = folder.Groups.Count,
        };

        private static BlockDto Describe(PlcBlock block, string path)
        {
            return new BlockDto
            {
                Name = block.Name,
                Path = path,
                BlockType = BlockTypeOf(block),
                Number = block.Number,
                Language = block.ProgrammingLanguage.ToString(),
                Namespace = string.IsNullOrEmpty(block.Namespace) ? null : block.Namespace,
                IsConsistent = block.IsConsistent,
                IsKnowHowProtected = block.IsKnowHowProtected,
                ModifiedDate = Iso(block.ModifiedDate),
                InstanceOfName = (block as InstanceDB)?.InstanceOfName,
                SecondaryType = (block as OB)?.SecondaryType,
            };
        }

        private static string BlockTypeOf(PlcBlock block)
        {
            if (block is OB) return "OB";
            if (block is FB) return "FB";
            if (block is FC) return "FC";
            if (block is InstanceDB) return "InstanceDB";
            if (block is GlobalDB) return "GlobalDB";
            return block.GetType().Name;
        }

        /// <summary>Path segments below the root folder, with a leading root name dropped.</summary>
        private static List<string> Segments(string path, string rootName)
        {
            if (string.IsNullOrWhiteSpace(path)) return new List<string>();

            var parts = path.Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (parts.Count > 0 && string.Equals(parts[0], rootName, StringComparison.OrdinalIgnoreCase))
                parts.RemoveAt(0);

            return parts;
        }

        private static string Normalize(string path, string rootName) =>
            string.Join("/", Segments(path, rootName));

        private static string SourceExtension(PlcBlock block)
        {
            if (block is DataBlock) return ".db";

            switch (block.ProgrammingLanguage)
            {
                case ProgrammingLanguage.STL:
                case ProgrammingLanguage.F_STL:
                    return ".awl";
                default:
                    return ".scl";
            }
        }

        private static string WithSourceExtension(string name) =>
            Path.GetExtension(name).Length > 0 ? name : name + ".scl";

        private static string NullIfEmpty(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>
        /// True when ErrorTranslator already has a better account of this failure than the caller's
        /// guess at it. Openness reports a missing licence as a plain EngineeringException with
        /// LicenseNotFoundException down the inner chain, so "TIA would not write this block out as
        /// source - only textual blocks have one" is what an unlicensed machine is told about an SCL
        /// block. Wrong, and it sends the reader off to look at the wrong thing entirely. These are
        /// let through untouched; the hints below only claim to explain what they actually explain.
        /// </summary>
        private static bool AlreadyExplained(Exception error)
        {
            // Matched the same way the translator matches them: the licence down the whole chain,
            // the rest on the outer type.
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                if ((cause.GetType().FullName ?? string.Empty)
                    .EndsWith("LicenseNotFoundException", StringComparison.Ordinal))
                    return true;
            }

            var typeName = error.GetType().FullName ?? string.Empty;
            return typeName.EndsWith("EngineeringSecurityException", StringComparison.Ordinal) ||
                   typeName.EndsWith("NonRecoverableException", StringComparison.Ordinal) ||
                   typeName.EndsWith("EngineeringObjectDisposedException", StringComparison.Ordinal);
        }

        /// <summary>A file Openness is willing to write: parent present, nothing in the way.</summary>
        private static FileInfo Fresh(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (file.Directory != null && !file.Directory.Exists) file.Directory.Create();
            // Openness refuses to overwrite; clearing first makes repeated exports idempotent.
            if (file.Exists) file.Delete();
            return file;
        }

        private static FileInfo ScratchFile(string name) =>
            Fresh(Path.Combine(Path.GetTempPath(), "tia-cli-export",
                Guid.NewGuid().ToString("N"), name));

        private static void TryDelete(FileInfo file)
        {
            try
            {
                file.Refresh();
                if (file.Exists) file.Delete();
                if (file.Directory != null && file.Directory.Exists &&
                    !file.Directory.EnumerateFileSystemInfos().Any())
                    file.Directory.Delete();
            }
            catch (IOException) { /* a leftover temp file is not worth failing the command over */ }
        }

        private static ExportResultDto Read(string name, FileInfo file, bool inline, int maxInlineChars)
        {
            var dto = new ExportResultDto
            {
                Name = name,
                FilePath = file.FullName,
                Bytes = file.Length,
            };

            if (!inline) return dto;

            var text = File.ReadAllText(file.FullName);
            if (text.Length > maxInlineChars)
            {
                dto.Content = text.Substring(0, maxInlineChars);
                dto.Truncated = true;
            }
            else
            {
                dto.Content = text;
            }

            return dto;
        }
    }
}
