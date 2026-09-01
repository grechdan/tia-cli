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
                case "block export": return BlockExport(cmd, executor, output);

                case "scl import": return SclImport(cmd, executor, output);

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
            var result = Call(executor, "block.list", new
            {
                device = cmd.Positional(0, "device"),
                filter = cmd.Value("filter"),
                includeSystemGroups = cmd.Has("system"),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var blocks = JsonUtil.To<List<BlockDto>>(result);
            output.Table(
                new[] { "name", "kind", "no", "language", "group", "state" },
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

        private static int BlockExport(CommandLine cmd, IExecutor executor, Output output)
        {
            var device = cmd.Positional(0, "device");
            var block = cmd.Positional(1, "block");
            var print = cmd.Has("print");

            var leaf = block.Replace('\\', '/').Split('/').Last();
            var target = cmd.Value("out");
            if (string.IsNullOrEmpty(target))
            {
                // With --print the file is a byproduct, so it goes somewhere disposable.
                target = print
                    ? Path.Combine(Path.GetTempPath(), "tia-cli-export", leaf + ".xml")
                    : Path.Combine(Environment.CurrentDirectory, leaf + ".xml");
            }

            var result = Call(executor, "block.export", new
            {
                device,
                block,
                targetPath = Path.GetFullPath(target),
                inline = print,
                maxInlineChars = cmd.Int("max-chars", 2000000),
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var export = JsonUtil.To<ExportResultDto>(result);
            if (print)
            {
                Console.WriteLine(export.Content);
                if (export.Truncated)
                    output.Warn($"Output truncated at {cmd.Int("max-chars", 2000000)} characters; " +
                                "the whole block is in " + export.FilePath);
                return 0;
            }

            output.Line($"{export.Name} -> {export.FilePath} ({export.Bytes:n0} bytes)");
            return 0;
        }

        // ---------------------------------------------------------------- sources

        private static int SclImport(CommandLine cmd, IExecutor executor, Output output)
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

            var generate = !cmd.Has("no-generate");
            var result = Call(executor, "source.importScl", new
            {
                device,
                name,
                code,
                filePath = string.IsNullOrEmpty(file) ? null : Path.GetFullPath(file),
                generate,
            });

            if (output.AsJson) { output.Json(result); return 0; }

            var import = JsonUtil.To<SourceImportDto>(result);
            output.Line("Imported source " + import.SourceName);
            if (import.GeneratedBlocks != null && import.GeneratedBlocks.Count > 0)
                output.Line("Generated: " + string.Join(", ", import.GeneratedBlocks));
            output.Detail("Generated blocks are not checked until 'tia compile <device>'.");
            return 0;
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

            var interesting = (compile.Messages ?? new List<CompileMessageDto>())
                .Where(m => !string.Equals(m.State, "Success", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(m.State, "Information", StringComparison.OrdinalIgnoreCase))
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
