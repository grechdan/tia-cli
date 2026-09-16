using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>How a command is allowed to get hold of a TIA Portal.</summary>
    public sealed class AcquireOptions
    {
        /// <summary>Attach to this process id specifically. Set by --attach.</summary>
        public int? AttachToProcessId { get; set; }

        /// <summary>May we start a portal of our own when none is running?</summary>
        public bool AllowCreate { get; set; }

        /// <summary>Start a portal of our own even when one is already running. Set by --new.</summary>
        public bool ForceCreate { get; set; }

        /// <summary>Only meaningful when creating: start it with the normal TIA Portal window.</summary>
        public bool WithUserInterface { get; set; } = true;
    }

    /// <summary>
    /// One TIA Portal session plus the thread that serialises access to it. Both the one-shot
    /// executor and the daemon are thin wrappers around this: they differ only in how long they
    /// keep it alive.
    ///
    /// Acquisition is lazy. Nothing touches TIA Portal until a command actually needs it, so
    /// 'tia portals' and 'tia session status' stay instant.
    /// </summary>
    internal sealed class SessionHost : IDisposable
    {
        private readonly OpennessThread _thread = new OpennessThread();
        private readonly TiaSession _session = new TiaSession();
        private readonly AcquireOptions _options;

        /// <summary>Methods that must not trigger acquisition - they are how you inspect it.</summary>
        private static readonly HashSet<string> NoSessionNeeded = new HashSet<string>(StringComparer.Ordinal)
        {
            "portal.list",
            "session.status",
        };

        public SessionHost(AcquireOptions options)
        {
            _options = options ?? new AcquireOptions();
        }

        /// <summary>
        /// The last known session state, updated on the Openness thread after every request. The
        /// daemon reports this for 'session status' instead of making a live call, so asking what is
        /// going on does not queue behind the long operation you are asking about.
        /// </summary>
        public SessionStateDto Snapshot => _snapshot;

        private volatile SessionStateDto _snapshot;

        /// <summary>Runs one request on the Openness thread, acquiring a session first if needed.</summary>
        public object Invoke(WireRequest request)
        {
            return _thread.Run(() =>
            {
                if (!NoSessionNeeded.Contains(request.Method)) Ensure();

                var result = Dispatch(request);
                if (result is SessionStateDto state) _snapshot = state;
                if (request.Method == "session.disconnect") _snapshot = null;
                return result;
            }).GetAwaiter().GetResult();
        }

        private SessionStateDto Ensure()
        {
            var state = Acquire();
            _snapshot = state;
            return state;
        }

        /// <summary>
        /// The rule that makes this tool usable both ways: join whatever is already running, and only
        /// start a portal when there is nothing to join and the caller asked for that.
        /// </summary>
        private SessionStateDto Acquire()
        {
            if (_session.IsConnected) return _session.Status();

            if (_options.ForceCreate)
                return _session.Create(_options.WithUserInterface);

            if (_options.AttachToProcessId.HasValue)
                return _session.Attach(_options.AttachToProcessId.Value);

            var running = _session.ListPortals();

            if (running.Count == 1)
                return _session.Attach(running[0].ProcessId);

            if (running.Count > 1)
            {
                throw new SessionException(WireErrorCodes.Ambiguous,
                    "Several TIA Portal instances are running: " +
                    string.Join(", ", running.Select(p => p.ProcessId + " (" + Describe(p) + ")")) + ".",
                    "Pick one with --attach <pid>.");
            }

            if (_options.AllowCreate)
                return _session.Create(_options.WithUserInterface);

            throw new SessionException(WireErrorCodes.NotConnected,
                "No TIA Portal is running to attach to.",
                "Start one yourself, or run 'tia session start' to have this tool launch and keep " +
                "one for you. Add --start to make a single command launch its own throwaway portal.");
        }

        private static string Describe(PortalProcessDto portal)
        {
            return string.IsNullOrEmpty(portal.ProjectPath)
                ? "no project"
                : System.IO.Path.GetFileNameWithoutExtension(portal.ProjectPath);
        }

        private object Dispatch(WireRequest request)
        {
            var p = new Params(request.Params);

            switch (request.Method)
            {
                case "portal.list": return _session.ListPortals();
                case "device.attributes":
                    return _session.ListAttributes(new Params(request.Params).RequiredString("device"));
                case "device.setAttribute":
                {
                    var attribute = new Params(request.Params);
                    return _session.SetAttribute(attribute.RequiredString("device"),
                        attribute.RequiredString("name"), attribute.RequiredString("value"));
                }
                case "sim.create":
                {
                    var sim = new Params(request.Params);
                    return _session.CreateSimulation(sim.RequiredString("device"), sim.String("cpu"),
                        sim.String("address"), sim.String("mask"), sim.Int("timeout", 60000));
                }
                case "device.protection":
                {
                    var protection = new Params(request.Params);
                    return _session.SetProtection(protection.RequiredString("device"),
                        protection.String("level"), protection.String("password"), protection.String("secret"));
                }
                case "session.ensure": return Ensure();
                case "session.status": return _session.IsConnected
                    ? _session.Status()
                    : new SessionStateDto { Connected = false, OpennessVersion = OpennessResolver.ResolvedVersion };
                case "session.disconnect": _session.Disconnect(); return Ok();

                case "project.create": return _session.CreateProject(
                    p.RequiredString("directory"), p.RequiredString("name"));
                case "project.open": return _session.OpenProject(
                    p.RequiredString("path"), p.Bool("upgrade", false));
                case "project.info": return _session.ProjectInfo();
                case "project.save": _session.SaveProject(); return Ok();
                case "project.close": _session.CloseProject(p.Bool("save", false)); return Ok();

                case "device.list": return _session.ListDevices();
                case "catalog.search": return _session.SearchCatalog(
                    p.RequiredString("filter"), p.Int("limit", 50));
                case "device.add": return _session.AddDevice(
                    p.RequiredString("typeIdentifier"), p.RequiredString("name"), p.String("deviceName"));
                case "device.setIp": return _session.SetIpAddress(
                    p.RequiredString("device"), p.String("address"), p.String("subnet"),
                    p.String("mask"), p.String("router"), p.NullableBool("useRouter"));

                case "source.list": return _session.ListSources(p.RequiredString("device"));
                case "source.importScl": return _session.ImportScl(
                    p.RequiredString("device"), p.RequiredString("name"), p.String("code"),
                    p.String("filePath"), p.Bool("generate", true), p.String("folder"));
                case "source.generate": return _session.GenerateFromSource(
                    p.RequiredString("device"), p.RequiredString("name"), p.String("folder"));
                case "source.delete": return _session.DeleteSource(
                    p.RequiredString("device"), p.RequiredString("name"));

                case "block.list": return _session.ListBlocks(
                    p.RequiredString("device"), p.String("filter"), p.String("type"),
                    p.Bool("includeSystemGroups", false));
                case "block.tree": return _session.BlockTree(
                    p.RequiredString("device"), p.String("filter"), p.String("type"),
                    p.Bool("includeSystemGroups", false));
                case "block.show": return _session.DescribeBlock(
                    p.RequiredString("device"), p.RequiredString("block"));
                case "block.export": return _session.ExportBlock(
                    p.RequiredString("device"), p.RequiredString("block"), p.RequiredString("targetPath"),
                    p.Bool("inline", false), p.Int("maxInlineChars", 200000));
                case "block.exportSource": return _session.ExportBlockSource(
                    p.RequiredString("device"), p.RequiredString("block"), p.String("targetPath"),
                    p.Bool("withDependencies", false), p.Bool("inline", false),
                    p.Int("maxInlineChars", 200000));
                case "block.import": return _session.ImportBlocks(
                    p.RequiredString("device"), p.RequiredString("filePath"), p.String("folder"),
                    p.Bool("overwrite", false));
                case "block.rename": return _session.RenameBlock(
                    p.RequiredString("device"), p.RequiredString("block"), p.RequiredString("newName"));
                case "block.delete": return _session.DeleteBlock(
                    p.RequiredString("device"), p.RequiredString("block"));
                case "block.createFolder": return _session.CreateBlockFolder(
                    p.RequiredString("device"), p.RequiredString("folder"));
                case "block.deleteFolder": return _session.DeleteBlockFolder(
                    p.RequiredString("device"), p.RequiredString("folder"));

                case "tag.listTables": return _session.ListTagTables(p.RequiredString("device"));
                case "tag.list": return _session.ListTags(p.RequiredString("device"), p.String("table"));
                case "tag.createTable": return _session.CreateTagTable(
                    p.RequiredString("device"), p.RequiredString("name"));
                case "tag.create": return _session.CreateTag(
                    p.RequiredString("device"), p.String("table"), p.RequiredString("name"),
                    p.String("dataType"), p.String("address"));

                case "ui.showHardware": return _session.ShowHardware(p.String("view"));
                case "ui.showDevice": return _session.ShowDevice(
                    p.RequiredString("device"), p.String("view"));
                case "ui.showBlock": return _session.ShowBlock(
                    p.RequiredString("device"), p.RequiredString("block"));
                case "ui.showTagTable": return _session.ShowTagTable(
                    p.RequiredString("device"), p.RequiredString("table"));

                case "plc.compile": return _session.Compile(p.RequiredString("device"));

                case "plc.download": return _session.Download(
                    p.RequiredString("device"), ReadPlan(p));
                case "plc.upload": return _session.UploadStation(ReadPlan(p));
                case "sim.start": return _session.StartSimulation(
                    p.RequiredString("device"), ReadPlan(p));

                default:
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        $"Unknown method '{request.Method}'.");
            }
        }

        private static TiaSession.TransferPlan ReadPlan(Params p)
        {
            var plan = new TiaSession.TransferPlan();
            if (p.String("mode") != null) plan.Mode = p.String("mode");
            plan.PcInterface = p.String("pcInterface");
            plan.InterfaceNumber = p.Int("slot", 1);
            plan.Address = p.String("address");
            plan.TargetInterface = p.String("target");
            plan.MasterSecret = p.String("secret");
            plan.PlcPassword = p.String("plcPassword");
            plan.IncludeHardware = p.Bool("hardware", false);
            plan.OnlyChanges = p.Bool("onlyChanges", false);
            plan.Force = p.Bool("force", false);
            plan.NoStart = p.Bool("noStart", false);
            plan.SimulationAdvancedTarget = p.Bool("advanced", false);
            return plan;
        }

        private static Dictionary<string, object> Ok() =>
            new Dictionary<string, object> { { "ok", true } };

        public void Dispose()
        {
            // Order matters: the session must be torn down on the thread that built it, or Openness
            // throws on the way out and a portal we created is left orphaned.
            try { _thread.Run(() => _session.Dispose()).GetAwaiter().GetResult(); }
            catch { /* nothing useful to do while shutting down */ }
            _thread.Dispose();
        }
    }
}
