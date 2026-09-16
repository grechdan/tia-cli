using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TiaCli.Protocol;

namespace TiaCli.Cli
{
    /// <summary>
    /// Maps verbs onto wire methods and renders what comes back. Every command here works the same
    /// whether a daemon or this process is answering.
    /// </summary>
    internal static class Commands
    {
        public static int Execute(CommandLine cmd, IExecutor executor, Output output)
        {
            switch (cmd.Verb)
            {
                case "portals": return Portals(executor, output);

                case "project open": return ProjectOpen(cmd, executor, output);
                case "project new": return ProjectNew(cmd, executor, output);
                case "project info": return ProjectInfo(executor, output);
                case "project save": return ProjectSave(executor, output);
                case "project close": return ProjectClose(cmd, executor, output);

                case "devices": return Devices(executor, output);
                case "catalog": return Catalog(cmd, executor, output);
                case "device add": return DeviceAdd(cmd, executor, output);
                case "device ip": return DeviceIp(cmd, executor, output);

                case "blocks": return Blocks(cmd, executor, output);
                case "block show": return BlockShow(cmd, executor, output);
                case "block source": return BlockExport(cmd, executor, output, asSource: true);
                case "block export": return BlockExport(cmd, executor, output, asSource: false);
                case "block import": return BlockImport(cmd, executor, output);
                case "block rename": return BlockRename(cmd, executor, output);
                case "block delete": return BlockDelete(cmd, executor, output);
                case "folder add": return FolderAdd(cmd, executor, output);
                case "folder delete": return FolderDelete(cmd, executor, output);

                case "sources": return Sources(cmd, executor, output);
                case "source add": return SourceAdd(cmd, executor, output);
                case "source generate": return SourceGenerate(cmd, executor, output);
                case "source delete": return SourceDelete(cmd, executor, output);
                // The spelling this tool shipped with, kept working.
                case "scl import": return SourceAdd(cmd, executor, output);

                case "tables": return Tables(cmd, executor, output);
                case "tags": return Tags(cmd, executor, output);
                case "table add": return TableAdd(cmd, executor, output);
                case "tag add": return TagAdd(cmd, executor, output);

                case "show hw": return Show(cmd, executor, output, "ui.showHardware", new
                {
                    view = cmd.Value("view"),
                });
                case "show device": return Show(cmd, executor, output, "ui.showDevice", new
                {
                    device = cmd.Positional(0, "device"),
                    view = cmd.Value("view"),
                });
                case "show block": return Show(cmd, executor, output, "ui.showBlock", new
                {
                    device = cmd.Positional(0, "device"),
                    block = cmd.Positional(1, "block"),
                });
                case "show table": return Show(cmd, executor, output, "ui.showTagTable", new
                {
                    device = cmd.Positional(0, "device"),
                    table = cmd.Positional(1, "table"),
                });

                case "device attrs": return DeviceAttributes(cmd, executor, output);
                case "device set": return DeviceSetAttribute(cmd, executor, output);
                case "device protection": return DeviceProtection(cmd, executor, output);
                case "sim create": return SimCreate(cmd, executor, output);

                case "compile": return Compile(cmd, executor, output);

                case "download": return Transfer(cmd, executor, output, "plc.download", new
                {
                    device = cmd.Positional(0, "device"),
                    mode = cmd.Value("via"),
                    pcInterface = cmd.Value("interface"),
                    slot = cmd.Int("slot", 1),
                    address = cmd.Value("address"),
                    hardware = cmd.Has("hardware"),
                    onlyChanges = cmd.Has("changes"),
                    force = cmd.Has("force"),
                    noStart = cmd.Has("stopped"),
                    target = cmd.Value("target"),
                    secret = cmd.Value("secret"),
                    plcPassword = cmd.Value("plc-password"),
                });
                case "upload": return Transfer(cmd, executor, output, "plc.upload", new
                {
                    address = cmd.Positional(0, "address"),
                    mode = cmd.Value("via"),
                    pcInterface = cmd.Value("interface"),
                    slot = cmd.Int("slot", 1),
                });
                case "sim start": return Transfer(cmd, executor, output, "sim.start", new
                {
                    device = cmd.Positional(0, "device"),
                    address = cmd.Value("address"),
                    pcInterface = cmd.Value("interface"),
                    slot = cmd.Int("slot", 1),
                    hardware = cmd.Has("hardware"),
                    noStart = cmd.Has("stopped"),
                    advanced = cmd.Has("advanced"),
                    target = cmd.Value("target"),
                    secret = cmd.Value("secret"),
                    plcPassword = cmd.Value("plc-password"),
                });

                default:
                    throw new WireException(WireErrorCodes.InvalidRequest,
                        $"Unknown command '{cmd.Verb}'.", "Run 'tia help' for the full list.");
            }
        }

        /// <summary>Sends a request and turns a failure response back into an exception to report.</summary>
        public static JsonElement Call(IExecutor executor, string method, object parameters = null)
        {
            var response = executor.Send(method, parameters);
            if (!response.Ok) throw WireException.FromError(response.Error);
            return response.Result ?? default(JsonElement);
        }

        // ---------------------------------------------------------------- session-adjacent

        private static int Portals(IExecutor executor, Output output)
        {
            var result = Call(executor, "portal.list");
            if (output.AsJson) { output.Json(result); return 0; }

            var portals = JsonUtil.To<List<PortalProcessDto>>(result);
            output.Table(
                new[] { "pid", "mode", "project", "running since" },
                portals.Select(p => new[]
                {
                    p.ProcessId.ToString(),
                    p.Mode == "WithUserInterface" ? "ui" : "headless",
                    string.IsNullOrEmpty(p.ProjectPath) ? "-" : Path.GetFileNameWithoutExtension(p.ProjectPath),
                    p.AcquisitionTime ?? "-",
                }).ToList());

            return 0;
        }

        // ---------------------------------------------------------------- editors

        /// <summary>
        /// The show verbs all do the same thing from the caller's side: ask TIA Portal to put
        /// something on screen, and report what it was. The window belongs to TIA Portal, not to us,
        /// so there is nothing to render beyond confirmation.
        /// </summary>
        private static int Show(CommandLine cmd, IExecutor executor, Output output,
            string method, object parameters)
        {
            var result = Call(executor, method, parameters);
            if (output.AsJson) { output.Json(result); return 0; }

            var shown = JsonUtil.To<ShownDto>(result);
            output.Line(string.IsNullOrEmpty(shown.View)
                ? $"Opened {shown.What} '{shown.Name}' in TIA Portal."
                : $"Opened {shown.What} '{shown.Name}' in the {shown.View.ToLowerInvariant()} view.");
            return 0;
        }

        // ---------------------------------------------------------------- online transfers

        private static int Transfer(CommandLine cmd, IExecutor executor, Output output,
            string method, object parameters)
        {
            var result = Call(executor, method, parameters);
            var transfer = JsonUtil.To<TransferResultDto>(result);

            if (output.AsJson)
            {
                output.Json(result);
                return transfer.ErrorCount > 0 ? 1 : 0;
            }

            output.Line($"{transfer.Operation}: {transfer.State} - " +
                        $"{transfer.ErrorCount} error(s), {transfer.WarningCount} warning(s)");
            output.Pairs(new[]
            {
                Output.KV("target", transfer.TargetAddress),
                Output.KV("via", transfer.Mode + " / " + transfer.PcInterface),
            });

            if (!string.IsNullOrEmpty(transfer.UploadedStation))
                output.Line("Uploaded as new station '" + transfer.UploadedStation + "'.");

            // Saying what was answered matters: these were TIA's dialog questions, and a script
            // that never saw the dialog should still leave a record of the choices made.
            foreach (var decision in transfer.Decisions ?? new List<string>())
                output.Detail("answered: " + decision);

            var interesting = (transfer.Messages ?? new List<string>())
                .Where(m => !m.TrimStart().StartsWith("Success", StringComparison.OrdinalIgnoreCase) &&
                            !m.TrimStart().StartsWith("Information", StringComparison.OrdinalIgnoreCase))
                .ToList();

            const int cap = 40;
            foreach (var line in interesting.Take(cap)) output.Line(line);
            if (interesting.Count > cap)
                output.Detail($"...and {interesting.Count - cap} more. Use --json for all of them.");

            return transfer.ErrorCount > 0 ? 1 : 0;
        }

        // ---------------------------------------------------------------- project

        private static int ProjectOpen(CommandLine cmd, IExecutor executor, Output output)
        {
            var path = Path.GetFullPath(cmd.Positional(0, "path"));
            output.Detail("Opening " + path + " - this takes minutes, not seconds.");

            var result = Call(executor, "project.open", new
            {
                path,
                upgrade = cmd.Has("upgrade"),
            });

            if (output.AsJson) { output.Json(result); return 0; }
            RenderProject(JsonUtil.To<ProjectDto>(result), output);
            return 0;
        }

        private static int ProjectNew(CommandLine cmd, IExecutor executor, Output output)
        {
            var directory = Path.GetFullPath(cmd.Positional(0, "directory"));
            var name = cmd.Positional(1, "name");

            var result = Call(executor, "project.create", new { directory, name });
            if (output.AsJson) { output.Json(result); return 0; }

            RenderProject(JsonUtil.To<ProjectDto>(result), output);
            output.Detail("Nothing is on disk until 'tia project save'.");
            return 0;
        }

        private static int ProjectInfo(IExecutor executor, Output output)
        {
            var result = Call(executor, "project.info");
            if (output.AsJson) { output.Json(result); return 0; }

            RenderProject(JsonUtil.To<ProjectDto>(result), output);
            return 0;
        }

        private static int ProjectSave(IExecutor executor, Output output)
        {
            Call(executor, "project.save");
            output.Line("Saved.");
            return 0;
        }

        private static int ProjectClose(CommandLine cmd, IExecutor executor, Output output)
        {
            Call(executor, "project.close", new { save = cmd.Has("save") });
            output.Line(cmd.Has("save") ? "Saved and closed." : "Closed without saving.");
            return 0;
        }

        private static void RenderProject(ProjectDto project, Output output)
        {
            if (project == null) return;

            output.Line(project.Name);
            output.Pairs(new[]
            {
                Output.KV("path", project.Path),
                Output.KV("author", project.Author),
                Output.KV("version", project.Version),
                Output.KV("created", project.CreationTime),
                Output.KV("modified", project.LastModified),
                Output.KV("modified by", project.LastModifiedBy),
                Output.KV("devices", project.DeviceCount.ToString()),
                Output.KV("unsaved changes", project.IsModified ? "yes" : "no"),
            });
        }

        // ---------------------------------------------------------------- devices

        private static int Devices(IExecutor executor, Output output)
        {
            var result = Call(executor, "device.list");
            if (output.AsJson) { output.Json(result); return 0; }

            var devices = JsonUtil.To<List<DeviceDto>>(result);
            output.Table(
                new[] { "station", "cpu item", "software", "address", "type" },
                devices.Select(d => new[]
                {
                    d.Name,
                    d.CpuItemName ?? "-",
                    d.HasPlcSoftware ? "plc" : (d.PlcSoftwareName == null ? "-" : "other"),
                    d.Addresses != null && d.Addresses.Count > 0 ? string.Join(", ", d.Addresses) : "-",
                    Output.Truncate(d.CpuTypeIdentifier ?? d.TypeIdentifier, 40),
                }).ToList());

            output.Detail("Block and tag commands accept either the station or the cpu item name.");
            return 0;
        }

        private static int Catalog(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "catalog.search", new
            {
                filter = cmd.Positional(0, "filter"),
                limit = cmd.Int("limit", 30),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var entries = JsonUtil.To<List<CatalogEntryDto>>(result);
            output.Table(
                new[] { "article", "version", "type identifier", "name" },
                entries.Select(e => new[]
                {
                    e.ArticleNumber ?? "-",
                    e.Version ?? "-",
                    Output.Truncate(e.TypeIdentifier, 60),
                    Output.Truncate(e.TypeName ?? e.Description, 40),
                }).ToList());

            return 0;
        }

        private static int DeviceAdd(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "device.add", new
            {
                typeIdentifier = cmd.Positional(0, "typeIdentifier"),
                name = cmd.Positional(1, "name"),
                deviceName = cmd.Value("device-name"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var device = JsonUtil.To<DeviceDto>(result);
            output.Line("Added " + device.Name);
            output.Pairs(new[]
            {
                Output.KV("cpu item", device.CpuItemName),
                Output.KV("type", device.CpuTypeIdentifier ?? device.TypeIdentifier),
                Output.KV("plc software", device.HasPlcSoftware ? device.PlcSoftwareName : null),
            });
            return 0;
        }

        private static int DeviceIp(CommandLine cmd, IExecutor executor, Output output)
        {
            var router = cmd.Value("router");
            if (!string.IsNullOrEmpty(router) && cmd.Has("no-router"))
                throw new WireException(WireErrorCodes.InvalidRequest,
                    "--router and --no-router contradict each other.",
                    "Give --router <ip> to set a gateway, or --no-router to stop using one.");

            var result = Call(executor, "device.setIp", new
            {
                device = cmd.Positional(0, "device"),
                address = cmd.Value("address"),
                subnet = cmd.Value("subnet"),
                // --subnet-mask is accepted too: --subnet already means the named TIA subnet here,
                // so the short spelling is the ambiguous one to a reader who knows the dialog.
                mask = cmd.Value("mask") ?? cmd.Value("subnet-mask"),
                router,
                // Null leaves the checkbox alone; only an explicit flag touches it.
                useRouter = cmd.Has("no-router") ? (bool?)false : null,
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var node = JsonUtil.To<NetworkNodeDto>(result);
            output.Line(node.Device + " / " + node.InterfaceItem);
            output.Pairs(new[]
            {
                Output.KV("address", node.Address),
                Output.KV("mask", node.SubnetMask),
                Output.KV("subnet", node.SubnetName ?? "(not connected)"),
                Output.KV("router", node.UseRouter
                    ? (string.IsNullOrEmpty(node.RouterAddress) ? "on, no address set" : node.RouterAddress)
                    : "not used"),
            });
            return 0;
        }

        // ---------------------------------------------------------------- blocks

        private static int Blocks(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var filter = cmd.Value("filter");
            var type = cmd.Value("type");
            var system = cmd.Has("system");

            if (cmd.Has("tree"))
            {
                var tree = Call(executor, "block.tree", new
                {
                    device,
                    filter,
                    type,
                    includeSystemGroups = system,
                });

                if (output.AsJson) { output.Json(tree); return 0; }

                var root = JsonUtil.To<BlockTreeNodeDto>(tree);
                output.Line(root.Name);
                RenderTree(root.Children, string.Empty, output);
                return 0;
            }

            var result = Call(executor, "block.list", new
            {
                device,
                filter,
                type,
                includeSystemGroups = system,
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var blocks = JsonUtil.To<List<BlockDto>>(result);
            output.Table(
                new[] { "name", "kind", "no", "language", "folder", "state" },
                blocks.Select(b => new[]
                {
                    b.Name,
                    b.BlockType,
                    b.Number.ToString(),
                    b.Language,
                    b.Path,
                    b.IsKnowHowProtected ? "protected" : (b.IsConsistent ? "ok" : "inconsistent"),
                }).ToList());

            return 0;
        }

        /// <summary>
        /// Draws the folder tree. The spine is ASCII on purpose: the console inherits an OEM code
        /// page unless somebody changes it, and box-drawing characters come out as mojibake there.
        /// </summary>
        private static void RenderTree(List<BlockTreeNodeDto> nodes, string prefix, Output output)
        {
            if (nodes == null) return;

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var last = i == nodes.Count - 1;
                var label = node.Kind == "folder"
                    ? node.Name
                    : node.Name + "  " + Describe(node.Block);

                output.Line(prefix + (last ? "`- " : "+- ") + label);
                RenderTree(node.Children, prefix + (last ? "   " : "|  "), output);
            }
        }

        private static string Describe(BlockDto block)
        {
            if (block == null) return string.Empty;

            var text = block.BlockType + block.Number + "  " + block.Language;
            if (block.IsKnowHowProtected) text += "  protected";
            else if (!block.IsConsistent) text += "  inconsistent";
            return text;
        }

        private static int BlockShow(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "block.show", new
            {
                device = cmd.Positional(0, "device"),
                block = cmd.Positional(1, "block"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var block = JsonUtil.To<BlockDetailDto>(result);
            output.Line(block.Name + "  (" + block.BlockType + block.Number + ")");
            output.Pairs(new[]
            {
                Output.KV("folder", block.Path),
                Output.KV("language", block.Language),
                Output.KV("numbering", block.AutoNumber ? "automatic" : "manual"),
                Output.KV("memory", block.MemoryLayout),
                Output.KV("namespace", block.Namespace),
                Output.KV("event class", block.SecondaryType),
                Output.KV("instance of", block.InstanceOfName),
                Output.KV("state", block.IsKnowHowProtected
                    ? "know-how protected"
                    : (block.IsConsistent ? "ok" : "inconsistent")),
                Output.KV("header", Header(block)),
                Output.KV("modified", block.ModifiedDate),
                Output.KV("compiled", block.CompileDate),
                Output.KV("comment", Output.Truncate(block.Comment, 70)),
            });

            if (block.IsKnowHowProtected)
            {
                output.Line();
                output.Detail("The interface and the code are sealed; only the header is readable.");
                return 0;
            }

            var members = block.Interface ?? new List<InterfaceMemberDto>();
            if (members.Count > 0)
            {
                output.Line();
                output.Line("interface");
                output.Table(
                    new[] { "section", "name", "type", "default", "comment" },
                    members.Select(m => new[]
                    {
                        m.Section ?? "-",
                        // Nested struct members are indented rather than dotted: the nesting is the
                        // point, and a long dotted path buries the name you are looking for.
                        new string(' ', m.Depth * 2) + m.Name,
                        m.DataType ?? "-",
                        m.StartValue ?? "",
                        Output.Truncate(m.Comment, 40),
                    }).ToList());
            }

            var networks = block.Networks ?? new List<NetworkDto>();
            if (networks.Count > 0)
            {
                output.Line();
                output.Line("networks");
                output.Table(
                    new[] { "#", "language", "title" },
                    networks.Select(n => new[]
                    {
                        n.Index.ToString(),
                        n.Language ?? "-",
                        Output.Truncate(n.Title ?? n.Comment, 70),
                    }).ToList());
            }

            output.Line();
            output.Detail("'tia block source' prints the code itself; 'tia block export' its XML.");
            return 0;
        }

        private static string Header(BlockDetailDto block)
        {
            var parts = new[] { block.HeaderName, block.HeaderFamily, block.HeaderVersion, block.HeaderAuthor }
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }

        /// <summary>
        /// The two block exports differ only in what they ask for, so they share the plumbing: where
        /// the file goes, when it is printed instead, and how a truncated print is reported.
        /// </summary>
        private static int BlockExport(CommandLine cmd, IExecutor executor, Output output,
            bool asSource)
        {
            var device = cmd.Positional(0, "device");
            var block = cmd.Positional(1, "block");
            // Source is normally wanted on the terminal, XML normally on disk.
            var print = asSource ? !cmd.Has("out") : cmd.Has("print");

            var target = cmd.Value("out");
            if (string.IsNullOrEmpty(target) && !asSource)
            {
                var leaf = block.Replace('\\', '/').Split('/').Last();
                // With --print the file is a byproduct, so it goes somewhere disposable.
                target = print
                    ? Path.Combine(Path.GetTempPath(), "tia-cli-export", leaf + ".xml")
                    : Path.Combine(Environment.CurrentDirectory, leaf + ".xml");
            }

            var maxChars = cmd.Int("max-chars", 2000000);
            var result = asSource
                ? Call(executor, "block.exportSource", new
                {
                    device,
                    block,
                    // Null lets the session pick the extension from the block's language.
                    targetPath = string.IsNullOrEmpty(target) ? null : Path.GetFullPath(target),
                    withDependencies = cmd.Has("deps"),
                    inline = print,
                    maxInlineChars = maxChars,
                })
                : Call(executor, "block.export", new
                {
                    device,
                    block,
                    targetPath = Path.GetFullPath(target),
                    inline = print,
                    maxInlineChars = maxChars,
                });

            if (output.AsJson) { output.Json(result); return 0; }

            var export = JsonUtil.To<ExportResultDto>(result);
            if (print)
            {
                Console.WriteLine(export.Content);
                if (export.Truncated)
                    output.Warn($"Output truncated at {maxChars} characters; " +
                                "the whole block is in " + export.FilePath);
                return 0;
            }

            output.Line($"{export.Name} -> {export.FilePath} ({export.Bytes:n0} bytes)");
            return 0;
        }

        private static int BlockImport(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "block.import", new
            {
                device = cmd.Positional(0, "device"),
                filePath = Path.GetFullPath(cmd.Positional(1, "file")),
                folder = cmd.Value("folder"),
                overwrite = cmd.Has("overwrite"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var import = JsonUtil.To<BlockImportDto>(result);
            var blocks = import.Blocks ?? new List<BlockDto>();

            output.Line(blocks.Count == 1
                ? $"Imported {blocks[0].Name} into {import.Folder}."
                : $"Imported {blocks.Count} block(s) into {import.Folder}.");

            foreach (var block in blocks)
                output.Detail("  " + block.Name + "  " + block.BlockType + block.Number);

            output.Detail("Imported blocks are not checked until 'tia compile <device>'.");
            return 0;
        }

        private static int BlockRename(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "block.rename", new
            {
                device = cmd.Positional(0, "device"),
                block = cmd.Positional(1, "block"),
                newName = cmd.Positional(2, "new name"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var block = JsonUtil.To<BlockDto>(result);
            output.Line($"Renamed to {block.Name} ({block.Path}).");
            output.Detail("Callers still name the old block; 'tia compile' will say which.");
            return 0;
        }

        private static int BlockDelete(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var name = cmd.Positional(1, "block");

            if (!Confirm(cmd, output, $"Delete block '{name}' from {device}?")) return 2;

            var result = Call(executor, "block.delete", new { device, block = name });
            if (output.AsJson) { output.Json(result); return 0; }

            var block = JsonUtil.To<BlockDto>(result);
            output.Line($"Deleted {block.Name} from {block.Path}.");
            output.Detail("Still only in memory - 'tia project save' makes it permanent.");
            return 0;
        }

        private static int FolderAdd(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "block.createFolder", new
            {
                device = cmd.Positional(0, "device"),
                folder = cmd.Positional(1, "path"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var folder = JsonUtil.To<BlockFolderDto>(result);
            output.Line("Folder " + folder.Path);
            output.Detail("Intermediate folders in the path were created as needed.");
            return 0;
        }

        private static int FolderDelete(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var path = cmd.Positional(1, "path");

            if (!Confirm(cmd, output, $"Delete folder '{path}' and everything in it?")) return 2;

            var result = Call(executor, "block.deleteFolder", new { device, folder = path });
            if (output.AsJson) { output.Json(result); return 0; }

            var folder = JsonUtil.To<BlockFolderDto>(result);
            output.Line($"Deleted {folder.Path} " +
                        $"({folder.BlockCount} block(s), {folder.FolderCount} folder(s)).");
            output.Detail("Still only in memory - 'tia project save' makes it permanent.");
            return 0;
        }

        /// <summary>
        /// Asks before something irreversible. --force skips the question; so does --json, which is
        /// only ever used by a script that cannot answer one. A redirected stdin with neither is the
        /// dangerous case - nobody is there to say no - so it refuses instead of assuming yes.
        /// </summary>
        private static bool Confirm(CommandLine cmd, Output output, string question)
        {
            if (cmd.Has("force") || cmd.Has("yes") || output.AsJson) return true;

            if (Console.IsInputRedirected)
                throw new WireException(WireErrorCodes.InvalidRequest,
                    "This deletes something and there is nobody to ask.",
                    "Add --force when running it from a script.");

            Console.Write(question + " [y/N] ");
            var answer = Console.ReadLine();
            if (string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return true;

            output.Line("Left alone.");
            return false;
        }

        // ---------------------------------------------------------------- sources

        private static int Sources(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "source.list", new { device = cmd.Positional(0, "device") });
            if (output.AsJson) { output.Json(result); return 0; }

            var sources = JsonUtil.To<List<SourceDto>>(result);
            output.Table(
                new[] { "source", "folder" },
                sources.Select(s => new[] { s.Name, s.Path }).ToList());

            output.Detail("'tia source generate <device> <name>' compiles one into blocks.");
            return 0;
        }

        private static int SourceAdd(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var name = cmd.Positional(1, "name");
            var file = cmd.Value("file");
            var code = cmd.Value("code");

            // "--file -" and a bare redirect both mean "the SCL is on stdin".
            if (file == "-" || (string.IsNullOrEmpty(file) && string.IsNullOrEmpty(code) && Console.IsInputRedirected))
            {
                code = Console.In.ReadToEnd();
                file = null;
                if (string.IsNullOrWhiteSpace(code))
                    throw new WireException(WireErrorCodes.InvalidRequest, "No SCL arrived on stdin.");
            }

            var result = Call(executor, "source.importScl", new
            {
                device,
                name,
                code,
                filePath = string.IsNullOrEmpty(file) ? null : Path.GetFullPath(file),
                generate = !cmd.Has("no-generate"),
                folder = cmd.Value("folder"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            RenderSourceImport(JsonUtil.To<SourceImportDto>(result), output);
            return 0;
        }

        private static int SourceGenerate(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "source.generate", new
            {
                device = cmd.Positional(0, "device"),
                name = cmd.Positional(1, "name"),
                folder = cmd.Value("folder"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            RenderSourceImport(JsonUtil.To<SourceImportDto>(result), output);
            return 0;
        }

        private static int SourceDelete(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var name = cmd.Positional(1, "name");

            if (!Confirm(cmd, output, $"Delete source '{name}' from {device}?")) return 2;

            var result = Call(executor, "source.delete", new { device, name });
            if (output.AsJson) { output.Json(result); return 0; }

            var source = JsonUtil.To<SourceDto>(result);
            output.Line($"Deleted {source.Name}.");
            output.Detail("The blocks it generated are still there.");
            return 0;
        }

        private static void RenderSourceImport(SourceImportDto import, Output output)
        {
            output.Line("Source " + import.SourceName);

            if (import.GeneratedBlocks != null && import.GeneratedBlocks.Count > 0)
            {
                output.Line("Generated into " + import.Folder + ": " +
                            string.Join(", ", import.GeneratedBlocks));
                output.Detail("Generated blocks are not checked until 'tia compile <device>'.");
                return;
            }

            output.Detail("Not generated. 'tia source generate <device> " +
                          import.SourceName + "' turns it into blocks.");
        }

        // ---------------------------------------------------------------- tags

        private static int Tables(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "tag.listTables", new { device = cmd.Positional(0, "device") });
            if (output.AsJson) { output.Json(result); return 0; }

            var tables = JsonUtil.To<List<TagTableDto>>(result);
            output.Table(
                new[] { "table", "tags", "default", "group" },
                tables.Select(t => new[]
                {
                    t.Name,
                    t.TagCount.ToString(),
                    t.IsDefault ? "yes" : "",
                    t.Path,
                }).ToList());

            return 0;
        }

        private static int Tags(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "tag.list", new
            {
                device = cmd.Positional(0, "device"),
                table = cmd.Value("table"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var tags = JsonUtil.To<List<TagDto>>(result);
            output.Table(
                new[] { "tag", "type", "address", "table", "comment" },
                tags.Select(t => new[]
                {
                    t.Name,
                    t.DataType ?? "-",
                    t.LogicalAddress ?? "-",
                    t.TableName,
                    Output.Truncate(t.Comment, 40),
                }).ToList());

            return 0;
        }

        private static int TableAdd(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "tag.createTable", new
            {
                device = cmd.Positional(0, "device"),
                name = cmd.Positional(1, "name"),
            });

            if (output.AsJson) { output.Json(result); return 0; }
            output.Line("Created tag table " + JsonUtil.To<TagTableDto>(result).Name);
            return 0;
        }

        private static int TagAdd(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "tag.create", new
            {
                device = cmd.Positional(0, "device"),
                name = cmd.Positional(1, "name"),
                table = cmd.Value("table"),
                dataType = cmd.Value("type"),
                address = cmd.Value("address"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var tag = JsonUtil.To<TagDto>(result);
            output.Line($"{tag.Name}  {tag.DataType}  {tag.LogicalAddress}  (table {tag.TableName})");
            return 0;
        }

        // ---------------------------------------------------------------- attributes

        private static int DeviceAttributes(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "device.attributes", new { device = cmd.Positional(0, "device") });
            if (output.AsJson) { output.Json(result); return 0; }

            var all = JsonUtil.To<List<AttributeDto>>(result);
            var filter = cmd.Value("filter");
            var shown = string.IsNullOrEmpty(filter)
                ? all
                : all.Where(a => (a.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            output.Table(
                new[] { "attribute", "type", "value" },
                shown.Select(a => new[]
                {
                    a.Name,
                    a.Type ?? "-",
                    Output.Truncate(a.Value ?? "-", 60),
                }).ToList());

            if (shown.Count != all.Count)
                output.Detail($"{shown.Count} of {all.Count} attributes. Drop --filter to see them all.");
            return 0;
        }

        private static int DeviceSetAttribute(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "device.setAttribute", new
            {
                device = cmd.Positional(0, "device"),
                name = cmd.Positional(1, "attribute"),
                value = cmd.Positional(2, "value"),
            });
            if (output.AsJson) { output.Json(result); return 0; }

            var attribute = JsonUtil.To<AttributeDto>(result);
            output.Line($"{attribute.Name} = {attribute.Value}");
            output.Detail("Changes are in memory until 'tia project save'.");
            return 0;
        }

        private static int DeviceProtection(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "device.protection", new
            {
                device = cmd.Positional(0, "device"),
                level = cmd.Value("level"),
                password = cmd.Value("password"),
                secret = cmd.Value("secret"),
            });
            if (output.AsJson) { output.Json(result); return 0; }

            foreach (var change in JsonUtil.To<List<AttributeDto>>(result))
                output.Line($"{change.Name}: {change.Value}");

            output.Detail("Changes are in memory until 'tia project save'.");
            return 0;
        }

        // ---------------------------------------------------------------- simulation

        private static int SimCreate(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var result = Call(executor, "sim.create", new
            {
                device,
                cpu = cmd.Value("cpu"),
                address = cmd.Value("address"),
                mask = cmd.Value("mask"),
                timeout = cmd.Int("timeout", 60000),
            });
            if (output.AsJson) { output.Json(result); return 0; }

            var simulation = JsonUtil.To<SimulationInstanceDto>(result);
            output.Line($"Simulation '{simulation.Name}' is running.");
            output.Pairs(new[]
            {
                Output.KV("cpu", simulation.CpuType),
                Output.KV("address", simulation.Address),
                Output.KV("state", simulation.OperatingState ?? "-"),
            });

            output.Detail($"Now put the program on it: tia download {device}");
            return 0;
        }

        // ---------------------------------------------------------------- compile

        private static int Compile(CommandLine cmd, IExecutor executor, Output output)
        {
            var result = Call(executor, "plc.compile", new { device = cmd.Positional(0, "device") });
            var compile = JsonUtil.To<CompileResultDto>(result);

            if (output.AsJson)
            {
                output.Json(result);
                return compile.ErrorCount > 0 ? 1 : 0;
            }

            output.Line($"{compile.State}: {compile.ErrorCount} error(s), {compile.WarningCount} warning(s)");

            // TIA's messages are a tree: each group node repeats the worst state below it with no text of
            // its own, and the last line restates the counts printed above. Only the leaves say anything.
            var interesting = (compile.Messages ?? new List<CompileMessageDto>())
                .Where(m => !string.Equals(m.State, "Success", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(m.State, "Information", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(m.Description) &&
                            !m.Description.TrimStart().StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase))
                .ToList();

            const int cap = 40;
            if (interesting.Count > 0)
            {
                output.Table(
                    new[] { "state", "where", "message" },
                    interesting.Take(cap).Select(m => new[]
                    {
                        m.State,
                        Output.Truncate(m.Path, 40),
                        Output.Truncate(m.Description, 90),
                    }).ToList());

                if (interesting.Count > cap)
                    output.Detail($"...and {interesting.Count - cap} more. Use --json for all of them.");
            }

            // A non-zero exit is what makes 'tia compile' usable in a script or a build step.
            return compile.ErrorCount > 0 ? 1 : 0;
        }
    }
}
