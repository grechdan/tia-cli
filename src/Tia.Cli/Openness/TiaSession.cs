using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.HardwareCatalog;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.Upload;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>
    /// Owns the TiaPortal connection and the open project. Every method here runs on the
    /// <see cref="OpennessThread"/>; none of it is safe to call from anywhere else.
    /// </summary>
    internal sealed class TiaSession : IDisposable
    {
        private TiaPortal _portal;
        private Project _project;
        private string _origin;
        private bool _ownsPortal;

        // Openness handles that must outlive the call that produced them. TiaPortalProcess has a
        // finaliser, and the one GetCurrentProcess() returns shares this portal's lifetime: let it
        // become garbage and the next GC disposes the portal underneath us - which closes the
        // process outright when it is one we started. Held until Disconnect, never disposed here.
        private TiaPortalProcess _currentProcess;

        // The handle we attached through. What disposal means for it after Attach() is not
        // documented, so it is kept rather than guessed at; holding a reference costs nothing.
        private TiaPortalProcess _attachHandle;

        public bool IsConnected => _portal != null;

        // ---------------------------------------------------------------- session

        // Handles GetProcesses() returns for the portal this session is connected to. Disposing one
        // disposes the connection - observed: a single 'tia portals' during a session made every later
        // call fail on a disposed Project - and dropping one hands the same job to its finaliser. So,
        // like _currentProcess, they are kept for the life of the connection.
        private readonly List<TiaPortalProcess> _ownPortalHandles = new List<TiaPortalProcess>();

        public List<PortalProcessDto> ListPortals()
        {
            int? ownId = null;
            if (_portal != null)
            {
                try { ownId = CurrentProcess().Id; }
                catch (SessionException) { /* a portal that is already gone has nothing to protect */ }
            }

            return TiaPortal.GetProcesses()
                .Select(p =>
                {
                    var own = false;
                    try
                    {
                        own = ownId.HasValue && p.Id == ownId.Value;
                        return new PortalProcessDto
                        {
                            ProcessId = p.Id,
                            Mode = p.Mode.ToString(),
                            ExePath = p.Path?.FullName,
                            ProjectPath = p.ProjectPath?.FullName,
                            AcquisitionTime = Iso(p.AcquisitionTime),
                        };
                    }
                    finally
                    {
                        if (own) _ownPortalHandles.Add(p);
                        else p.Dispose();
                    }
                })
                .ToList();
        }

        /// <summary>
        /// Joins a TIA Portal that is already running - the one the engineer has open in front of
        /// them, or one a previous run of this tool started. The portal is left alone on disconnect.
        /// </summary>
        public SessionStateDto Attach(int processId)
        {
            RequireDisconnected();

            // The 30s budget covers a portal that is still booting; GetProcess returns null rather
            // than throwing when the id simply is not a TIA Portal.
            var process = TiaPortal.GetProcess(processId, TimeSpan.FromSeconds(30));
            if (process == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    $"No attachable TIA Portal process with id {processId}.",
                    "Run 'tia portals' to see which instances are running.");

            _portal = process.Attach();
            _attachHandle = process;
            _origin = "attached";
            _ownsPortal = false;

            // The engineer may already have a project open; adopting it is the whole point of
            // attaching, and it saves an Open call that would take minutes.
            AdoptOpenProject();
            return Status();
        }

        /// <summary>
        /// Starts a TIA Portal of our own. It belongs to this process: when we dispose, it closes.
        /// That is why anything long-lived runs inside the daemon rather than a one-shot command.
        /// </summary>
        public SessionStateDto Create(bool withUserInterface)
        {
            RequireDisconnected();

            _portal = new TiaPortal(withUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);
            _origin = "created";
            _ownsPortal = true;

            return Status();
        }

        public SessionStateDto Status()
        {
            if (_portal == null)
                return new SessionStateDto { Connected = false, OpennessVersion = OpennessResolver.ResolvedVersion };

            var process = CurrentProcess();

            // Null here is the one reliable signal that the instance died underneath us.
            if (process == null)
                throw new SessionException(WireErrorCodes.PortalUnrecoverable,
                    "The TIA Portal instance is no longer running.",
                    "Run 'tia session stop', then start a new session.");

            // The handle outlives the process it describes, so a portal that has since been closed
            // shows up here rather than at the call that fetched it.
            bool withUserInterface;
            int processId;
            try
            {
                withUserInterface = process.Mode == TiaPortalMode.WithUserInterface;
                processId = process.Id;
            }
            catch (Exception ex)
            {
                throw new SessionException(WireErrorCodes.PortalUnrecoverable,
                    "The TIA Portal instance is no longer usable: " + ex.Message,
                    "Run 'tia session stop', then start a new session.");
            }

            // A project opened in the UI after we attached would otherwise stay invisible.
            if (_project == null) AdoptOpenProject();

            return new SessionStateDto
            {
                Connected = true,
                WithUserInterface = withUserInterface,
                ProcessId = processId,
                OpennessVersion = OpennessResolver.ResolvedVersion,
                Origin = _origin,
                OwnsPortal = _ownsPortal,
                Project = _project == null ? null : Describe(_project),
            };
        }

        /// <summary>
        /// This portal's own process handle, fetched once and then kept for the life of the session.
        /// It must be neither disposed nor dropped: see <see cref="_currentProcess"/>.
        /// </summary>
        private TiaPortalProcess CurrentProcess()
        {
            if (_currentProcess != null) return _currentProcess;

            try
            {
                _currentProcess = _portal.GetCurrentProcess();
            }
            catch (Exception ex)
            {
                throw new SessionException(WireErrorCodes.PortalUnrecoverable,
                    "The TIA Portal instance is no longer usable: " + ex.Message,
                    "Run 'tia session stop', then start a new session.");
            }

            return _currentProcess;
        }

        public void Disconnect()
        {
            _project = null;
            if (_portal != null)
            {
                _portal.Dispose();
                _portal = null;
            }
            // Released only after the portal has gone, and only by dropping the reference: disposing
            // any of these handles disposes the portal, which is the whole reason they are held.
            _currentProcess = null;
            _attachHandle = null;
            _ownPortalHandles.Clear();
            _origin = null;
            _ownsPortal = false;
        }

        // ---------------------------------------------------------------- project

        public ProjectDto OpenProject(string path, bool upgrade)
        {
            var portal = RequirePortal();
            if (_project != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"Project '{_project.Name}' is already open in this session. Close it first.");

            var file = new FileInfo(path);
            if (!file.Exists)
                throw new SessionException(WireErrorCodes.NotFound, $"Project file not found: {path}");

            _project = upgrade
                ? portal.Projects.OpenWithUpgrade(file)
                : portal.Projects.Open(file);

            return Describe(_project);
        }

        public ProjectDto CreateProject(string directory, string name)
        {
            var portal = RequirePortal();
            if (_project != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"Project '{_project.Name}' is already open in this session. Close it first.");

            var target = new DirectoryInfo(directory);
            if (!target.Exists) target.Create();

            // Create() makes <directory>\<name>\<name>.apNN and leaves it open; it throws a bare
            // Openness error if that folder already exists, so the clearer message is worth checking.
            if (Directory.Exists(Path.Combine(target.FullName, name)))
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"A folder named '{name}' already exists in {target.FullName}.");

            _project = portal.Projects.Create(target, name);
            return Describe(_project);
        }

        public ProjectDto ProjectInfo() => Describe(RequireProject());

        public void SaveProject() => RequireProject().Save();

        public void CloseProject(bool save)
        {
            var project = RequireProject();
            if (save) project.Save();
            project.Close();
            _project = null;
        }

        // ---------------------------------------------------------------- devices

        public List<DeviceDto> ListDevices()
        {
            var project = RequireProject();
            var result = new List<DeviceDto>();

            foreach (var device in EnumerateDevices(project))
                result.Add(Describe(device));

            return result;
        }

        /// <summary>
        /// Searches the installed hardware catalog. This is the only way to learn a valid
        /// typeIdentifier for 'device add' without reading it off the TIA Portal UI, and it reflects
        /// what is actually installed rather than what the catalog documentation lists.
        /// </summary>
        public List<CatalogEntryDto> SearchCatalog(string filter, int limit)
        {
            // The catalog hangs off the portal, not the project, so this works with no project open.
            var catalog = RequirePortal().HardwareCatalog;
            if (catalog == null)
                throw new SessionException(WireErrorCodes.OpennessError,
                    "The hardware catalog is not available on this portal instance.");

            var hits = catalog.Find(filter);
            var result = new List<CatalogEntryDto>();
            if (hits == null) return result;

            foreach (CatalogEntry entry in hits)
            {
                if (result.Count >= limit) break;
                result.Add(new CatalogEntryDto
                {
                    TypeIdentifier = entry.TypeIdentifier,
                    ArticleNumber = entry.ArticleNumber,
                    TypeName = entry.TypeName,
                    Version = entry.Version,
                    Description = entry.Description,
                    CatalogPath = entry.CatalogPath,
                });
            }

            return result;
        }

        public DeviceDto AddDevice(string typeIdentifier, string name, string deviceName)
        {
            var project = RequireProject();

            if (project.Devices.Find(name) != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"A device named '{name}' already exists.");

            // CreateWithItem plugs the CPU into a new station in one step. Create() alone makes an
            // empty rack, which has no PLC software and is almost never what the caller wanted.
            var device = project.Devices.CreateWithItem(typeIdentifier, name,
                string.IsNullOrEmpty(deviceName) ? name : deviceName);

            return Describe(device);
        }

        /// <summary>
        /// Sets the IP of a device's first Ethernet node and, when a subnet is named, makes sure the
        /// node is attached to it. Both halves matter: an address without a subnet compiles but leaves
        /// the station unreachable in the network view.
        /// </summary>
        public NetworkNodeDto SetIpAddress(string deviceName, string address, string subnetName,
            string subnetMask, string routerAddress, bool? useRouter)
        {
            var project = RequireProject();
            var device = FindDevice(project, deviceName);

            var found = FindEthernetNode(device);
            if (found == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    $"Device '{deviceName}' has no Ethernet interface to address.");

            var item = found.Item1;
            var node = found.Item2;

            if (!string.IsNullOrEmpty(address))
                node.SetAttribute("Address", address);

            if (!string.IsNullOrEmpty(subnetMask))
                SetNodeAttribute(node, SubnetMaskAttribute, subnetMask);

            // Order matters: the router address is rejected while the checkbox is off, so turn it on
            // first. Giving an address implies turning it on - nobody sets a gateway to leave it
            // unused - but --no-router still wins when both are given.
            if (!string.IsNullOrEmpty(routerAddress) && useRouter != false)
                SetNodeAttribute(node, RouterEnabledAttribute, true);

            if (!string.IsNullOrEmpty(routerAddress))
                SetNodeAttribute(node, RouterAddressAttribute, routerAddress);

            if (useRouter.HasValue)
                SetNodeAttribute(node, RouterEnabledAttribute, useRouter.Value);

            if (!string.IsNullOrEmpty(subnetName))
            {
                if (node.ConnectedSubnet == null)
                {
                    var existing = project.Subnets.Find(subnetName);
                    if (existing != null) node.ConnectToSubnet(existing);
                    else node.CreateAndConnectToSubnet(subnetName);
                }
                else if (!string.Equals(node.ConnectedSubnet.Name, subnetName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        $"Node is already connected to subnet '{node.ConnectedSubnet.Name}'. " +
                        "Disconnect it before attaching a different one.");
                }
            }

            return new NetworkNodeDto
            {
                Device = device.Name,
                InterfaceItem = item.Name,
                NodeName = node.Name,
                Address = node.GetAttribute("Address") as string,
                SubnetName = node.ConnectedSubnet == null ? null : node.ConnectedSubnet.Name,
                SubnetMask = ReadNodeAttribute(node, SubnetMaskAttribute) as string,
                UseRouter = ReadNodeAttribute(node, RouterEnabledAttribute) as bool? ?? false,
                RouterAddress = ReadNodeAttribute(node, RouterAddressAttribute) as string,
            };
        }

        // Openness exposes these as loose strings rather than typed members, and the set is decided by
        // the interface's own model. They are named here so the two that matter are stated once.
        private const string SubnetMaskAttribute = "SubnetMask";
        private const string RouterEnabledAttribute = "UseRouter";
        private const string RouterAddressAttribute = "RouterAddress";

        /// <summary>
        /// Sets an attribute, and when the interface does not have one by that name says which names
        /// it does have. Attribute names are not part of the API surface - they come from the device's
        /// model and vary by interface - so a wrong guess is otherwise a bare Openness error.
        /// </summary>
        private static void SetNodeAttribute(Node node, string name, object value)
        {
            try
            {
                node.SetAttribute(name, value);
            }
            catch (Exception ex)
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"Could not set '{name}' on this interface: {ex.Message}",
                    "Attributes this interface accepts: " + DescribeAttributes(node));
            }
        }

        /// <summary>Reads an attribute the interface may not have at all; absent reads as null.</summary>
        private static object ReadNodeAttribute(Node node, string name)
        {
            try { return node.GetAttribute(name); }
            catch { return null; }
        }

        private static string DescribeAttributes(Node node)
        {
            try
            {
                return string.Join(", ", node.GetAttributeInfos()
                    .Select(i => i.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                return "(the interface would not list them)";
            }
        }

        // ---------------------------------------------------------------- editors

        /// <summary>
        /// Opens the hardware editor. This is the one part of Openness that drives the UI rather than
        /// the data, and it is how a project gets onto the screen without anybody clicking: TIA Portal
        /// leaves an Openness-opened project in the portal view until an editor is asked for.
        /// </summary>
        public ShownDto ShowHardware(string view)
        {
            var project = RequireProject();
            RequireUserInterface();

            project.ShowHwEditor(ParseView(view));
            return new ShownDto { What = "hardware", Name = project.Name, View = ParseView(view).ToString() };
        }

        public ShownDto ShowDevice(string deviceName, string view)
        {
            var project = RequireProject();
            RequireUserInterface();

            var device = FindDevice(project, deviceName);
            device.ShowInEditor(ParseView(view));
            return new ShownDto { What = "device", Name = device.Name, View = ParseView(view).ToString() };
        }

        public ShownDto ShowBlock(string deviceName, string blockPath)
        {
            var software = RequirePlcSoftware(deviceName);
            RequireUserInterface();

            var block = FindBlock(software, blockPath);

            // A protected block opens as an empty editor rather than failing, which looks like the
            // command did nothing at all.
            if (block.IsKnowHowProtected)
                throw new SessionException(WireErrorCodes.AccessDenied,
                    $"Block '{block.Name}' is know-how protected; its editor would open empty.");

            block.ShowInEditor();
            return new ShownDto { What = "block", Name = block.Name };
        }

        public ShownDto ShowTagTable(string deviceName, string tableName)
        {
            var software = RequirePlcSoftware(deviceName);
            RequireUserInterface();

            foreach (var table in EnumerateTagTables(software.TagTableGroup))
            {
                if (!string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase)) continue;
                table.ShowInEditor();
                return new ShownDto { What = "tag table", Name = table.Name };
            }

            throw new SessionException(WireErrorCodes.NotFound, $"Tag table '{tableName}' not found.");
        }

        private static View ParseView(string view)
        {
            if (string.IsNullOrEmpty(view)) return View.Device;

            View parsed;
            if (Enum.TryParse(view, true, out parsed)) return parsed;

            throw new SessionException(WireErrorCodes.InvalidRequest,
                $"Unknown view '{view}'.", "Use device, network or topology.");
        }

        /// <summary>
        /// A headless portal has no editors to show anything in. Openness fails obscurely there, and
        /// the useful answer is which portal the session is holding.
        /// </summary>
        private void RequireUserInterface()
        {
            var process = CurrentProcess();
            if (process != null && process.Mode == TiaPortalMode.WithUserInterface) return;

            throw new SessionException(WireErrorCodes.InvalidRequest,
                "This session's TIA Portal is running headless, so it has no editor to show.",
                "Start a session without --headless: 'tia session stop' then 'tia session start', " +
                "or attach to a TIA Portal you have open on screen.");
        }

        // ---------------------------------------------------------------- online transfers

        /// <summary>Everything that describes one transfer to or from a PLC.</summary>
        internal sealed class TransferPlan
        {
            public string Mode = "PN/IE";
            public string PcInterface;
            public int InterfaceNumber = 1;
            public string Address;
            /// <summary>The device interface to download to, as TIA names it (e.g. "1 X1").</summary>
            public string TargetInterface;
            public bool IncludeHardware;
            public bool OnlyChanges;
            /// <summary>Allow the prompts that change or erase things beyond the download itself.</summary>
            public bool Force;
            /// <summary>Leave the CPU stopped when the download is done.</summary>
            public bool NoStart;
            /// <summary>Answer the software-target prompt with PLCSIM Advanced instead of a CPU.</summary>
            public bool SimulationAdvancedTarget;
        }

        /// <summary>
        /// Downloads the configuration to the device. TIA's download dialog becomes a series of
        /// callbacks here; every prompt is answered the way the dialog's defaults would, the risky
        /// ones only under Force, and anything unrecognised aborts by name rather than guessing.
        /// </summary>
        public TransferResultDto Download(string deviceName, TransferPlan plan)
        {
            var cpu = RequireCpuItem(deviceName);

            var provider = cpu.GetService<DownloadProvider>();
            if (provider == null)
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"'{deviceName}' has no download service - it cannot be a download target.");

            var target = ResolveTarget(provider.Configuration, plan, DeviceAddressFallback(deviceName), forDownload: true);

            var options = plan.OnlyChanges ? DownloadOptions.SoftwareOnlyChanges : DownloadOptions.Software;
            if (plan.IncludeHardware) options |= DownloadOptions.Hardware;

            var decisions = new List<string>();
            var refusals = new List<SessionException>();
            // Nothing may escape this delegate: TIA runs it inside Download, and an exception there -
            // even one raised by an assignment TIA itself rejects - takes the portal down unexplained.
            // Every prompt is logged, so a crash leaves a trail of how far the download got.
            DownloadConfigurationDelegate answer = c =>
            {
                var name = c?.GetType().Name ?? "(null)";
                try
                {
                    AnswerDownloadPrompt(c, plan, decisions, refusals);
                    TiaCli.Daemon.DaemonPaths.Log($"download prompt: {name} -> answered");
                }
                catch (Exception ex)
                {
                    TiaCli.Daemon.DaemonPaths.Log($"download prompt: {name} -> could not answer: {ex}");
                    refusals.Add(new SessionException(WireErrorCodes.OpennessError,
                        $"Answering the download prompt {name} failed: {ex.Message}",
                        "Report the prompt name and this message."));
                }
            };

            // A prompt this tool will not answer is declined inside the callback and reported once TIA
            // hands control back. Throwing from inside the callback instead is fatal to TIA: it comes
            // back as a NonRecoverableException, the portal is gone, and the prompt's name with it.
            try
            {
                TiaCli.Daemon.DaemonPaths.Log($"download start: {deviceName} via {target.Mode} / " +
                    $"{target.PcInterface} to {target.Display}, options {options}");
                var result = provider.Download(target.Configuration, answer, answer, options);
                TiaCli.Daemon.DaemonPaths.Log($"download returned: {result.State}, " +
                    $"{result.ErrorCount} error(s), {result.WarningCount} warning(s)");
                if (refusals.Count > 0) throw refusals[0];

                var dto = DescribeTransfer("download", deviceName, plan, target, decisions);
                dto.State = result.State.ToString();
                dto.ErrorCount = result.ErrorCount;
                dto.WarningCount = result.WarningCount;
                FlattenTransferMessages(result.Messages, 0, dto.Messages);
                return dto;
            }
            catch (Exception ex) when (refusals.Count > 0 && !(ex is SessionException))
            {
                throw refusals[0];
            }
            catch (EngineeringTargetInvocationException ex) when (refusals.Count == 0)
            {
                // TIA reports a refused download with one line ("Connect to module failed", "error
                // during download"), while the reason - usually the configuration - only appears in its
                // compile output. The portal survives this exception, so compile and attach the errors.
                string reasons = null;
                try
                {
                    var check = Compile(deviceName);
                    var found = check.Messages
                        .Where(m => string.Equals(m.State, "Error", StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(m.Description) &&
                                    !m.Description.TrimStart().StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase))
                        .Select(m => "  - " + m.Description.Trim())
                        .Distinct()
                        .Take(10)
                        .ToList();
                    if (found.Count > 0)
                        reasons = $"The project does not compile ({check.ErrorCount} error(s)):" +
                                  Environment.NewLine + string.Join(Environment.NewLine, found);
                }
                catch (Exception compileFailure)
                {
                    TiaCli.Daemon.DaemonPaths.Log("compile after failed download also failed: " + compileFailure.Message);
                }

                throw new SessionException(WireErrorCodes.OpennessError, ex.Message,
                    reasons != null
                        ? reasons + Environment.NewLine + "Fix these, then run 'tia compile " + deviceName + "' and download again."
                        : "The project compiles, so check that the target is reachable: is the PLC (or the PLCSIM instance) running at that address, on a subnet of its own?");
            }
        }

        /// <summary>
        /// Uploads whatever answers at the address into this project as a new station. The provider
        /// hangs off the project because the upload creates the device rather than reading into one.
        /// </summary>
        public TransferResultDto UploadStation(TransferPlan plan)
        {
            var project = RequireProject();

            var provider = ((IEngineeringServiceProvider)project).GetService<StationUploadProvider>();
            if (provider == null)
                throw new SessionException(WireErrorCodes.OpennessError,
                    "This Openness version does not offer station upload.",
                    "Uploading needs TIA Portal V17 or later.");

            if (string.IsNullOrEmpty(plan.Address))
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "Uploading needs the PLC's address.", "Give it as 'tia upload <ip>'.");

            var target = ResolveTarget(provider.Configuration, plan, null, forDownload: false);

            var decisions = new List<string>();
            var refusals = new List<SessionException>();

            // Same rule as Download: never throw from inside TIA's callback.
            try
            {
                var result = provider.StationUpload(target.Address, c =>
                {
                    var name = c?.GetType().Name ?? "(null)";
                    try
                    {
                        AnswerUploadPrompt(c, decisions, refusals);
                        TiaCli.Daemon.DaemonPaths.Log($"upload prompt: {name} -> answered");
                    }
                    catch (Exception ex)
                    {
                        TiaCli.Daemon.DaemonPaths.Log($"upload prompt: {name} -> could not answer: {ex}");
                        refusals.Add(new SessionException(WireErrorCodes.OpennessError,
                            $"Answering the upload prompt {name} failed: {ex.Message}",
                            "Report the prompt name and this message."));
                    }
                });
                if (refusals.Count > 0) throw refusals[0];

                var dto = DescribeTransfer("upload", null, plan, target, decisions);
                dto.State = result.State.ToString();
                dto.ErrorCount = result.ErrorCount;
                dto.WarningCount = result.WarningCount;
                FlattenTransferMessages(result.Messages, 0, dto.Messages);
                try { dto.UploadedStation = result.UploadedStation?.Name; } catch { }
                return dto;
            }
            catch (Exception ex) when (refusals.Count > 0 && !(ex is SessionException))
            {
                throw refusals[0];
            }
        }

        /// <summary>
        /// Starts a simulation of the device. There is no start-simulation call in Openness; what
        /// exists is downloading to a simulator.
        ///
        /// Up to V17 that meant the PLCSIM connection mode, which also boots the simulator. From V18
        /// the mode is gone: a simulated PLC is an ordinary PN/IE target behind the Siemens PLCSIM
        /// Virtual Ethernet Adapter, reached at its own IP address - and the instance has to be running
        /// already, because TIA will not start one for a download.
        /// </summary>
        public TransferResultDto StartSimulation(string deviceName, TransferPlan plan)
        {
            // A simulated CPU has no retained state worth protecting, and the first download to a
            // fresh simulator always raises the prompts a real first download would.
            plan.Force = true;

            var modes = RequireCpuItem(deviceName).GetService<DownloadProvider>()?.Configuration.Modes;
            if (modes != null && modes.Find("PLCSIM") != null)
            {
                plan.Mode = "PLCSIM";
                plan.PcInterface = plan.PcInterface ?? "PLCSIM";
            }
            else if (modes != null)
            {
                var adapter = modes
                    .SelectMany(m => m.PcInterfaces.Select(i => new { Mode = m, Interface = i }))
                    .FirstOrDefault(x => x.Interface.Name.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0);

                if (adapter == null)
                {
                    var known = string.Join(", ", modes.SelectMany(m => m.PcInterfaces.Select(i => m.Name + " / " + i.Name)));
                    throw new SessionException(WireErrorCodes.NotFound,
                        $"No PLCSIM connection mode and no PLCSIM adapter on this machine. Available: {known}.",
                        "Install S7-PLCSIM for this TIA Portal version.");
                }

                plan.Mode = adapter.Mode.Name;
                plan.PcInterface = plan.PcInterface ?? adapter.Interface.Name;
            }

            try
            {
                var dto = Download(deviceName, plan);
                dto.Operation = "simulation";
                return dto;
            }
            catch (SessionException ex) when (ex.Message.IndexOf("Connect to module", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                              (ex.Hint ?? "").StartsWith("The project compiles", StringComparison.Ordinal))
            {
                // Confirmed on V20: the same download fails with "Connect to module" while the simulation
                // is off, and succeeds once it has been started - TIA will not start it for a download.
                throw new SessionException(ex.Code, ex.Message,
                    "The project compiles, so the simulation is most likely not running. Start it first: " +
                    "open S7-PLCSIM, power on an instance of the same CPU family at the device's IP address " +
                    "(or use 'Start simulation' in TIA Portal), then run 'tia sim start' again.");
            }
        }

        private sealed class ResolvedTarget
        {
            public string Mode;
            public string PcInterface;
            /// <summary>What the transfer is aimed at: a target interface for downloads, an address otherwise.</summary>
            public IConfiguration Configuration;
            /// <summary>Set on the address route only; station upload takes an address.</summary>
            public ConfigurationAddress Address;
            public string Display;
        }

        /// <summary>
        /// Walks mode -> PC interface -> address. Every miss reports what is actually there, because
        /// the names are installation-specific and this is the only way to discover them from a CLI.
        /// </summary>
        private static ResolvedTarget ResolveTarget(ConnectionConfiguration configuration,
            TransferPlan plan, string fallbackAddress, bool forDownload)
        {
            var mode = configuration.Modes.Find(plan.Mode);
            if (mode == null)
            {
                var modes = string.Join(", ", configuration.Modes.Select(m => m.Name));
                throw new SessionException(WireErrorCodes.NotFound,
                    $"Connection mode '{plan.Mode}' is not available. This machine has: {modes}.",
                    plan.Mode.Equals("PLCSIM", StringComparison.OrdinalIgnoreCase)
                        ? "No PLCSIM mode means S7-PLCSIM is not installed."
                        : "Pick one with --via <mode>.");
            }

            ConfigurationPcInterface pcInterface;
            if (!string.IsNullOrEmpty(plan.PcInterface))
            {
                pcInterface = mode.PcInterfaces.Find(plan.PcInterface, plan.InterfaceNumber);
                if (pcInterface == null)
                {
                    var known = string.Join(", ", mode.PcInterfaces.Select(i => $"{i.Name} ({i.Number})"));
                    throw new SessionException(WireErrorCodes.NotFound,
                        $"PC interface '{plan.PcInterface}' ({plan.InterfaceNumber}) not found in mode " +
                        $"'{mode.Name}'. Available: {known}.",
                        "Pick one with --interface <name> and, if needed, --slot <number>.");
                }
            }
            else if (mode.PcInterfaces.Count == 1)
            {
                pcInterface = mode.PcInterfaces[0];
            }
            else
            {
                var known = string.Join(", ", mode.PcInterfaces.Select(i => $"{i.Name} ({i.Number})"));
                throw new SessionException(WireErrorCodes.Ambiguous,
                    $"Mode '{mode.Name}' has several PC interfaces: {known}.",
                    "Pick one with --interface <name>.");
            }

            var address = plan.Address ?? fallbackAddress;

            // A download is aimed at a target interface - the device's port as TIA names it, such as
            // "1 X1" - which is the route Siemens documents. An address made directly on the PC adapter
            // belongs to no target interface, and handing that to Download was seen to take TIA
            // down before it asked a single question. The address route stays for station upload, and
            // for an adapter that offers no target interfaces at all.
            if (forDownload && pcInterface.TargetInterfaces.Count > 0)
            {
                var names = string.Join(", ", pcInterface.TargetInterfaces.Select(t => t.Name));
                TiaCli.Daemon.DaemonPaths.Log($"target interfaces on {pcInterface.Name}: {names}");

                ConfigurationTargetInterface targetInterface;
                if (!string.IsNullOrEmpty(plan.TargetInterface))
                {
                    targetInterface = pcInterface.TargetInterfaces.Find(plan.TargetInterface);
                    if (targetInterface == null)
                        throw new SessionException(WireErrorCodes.NotFound,
                            $"Target interface '{plan.TargetInterface}' not found on '{pcInterface.Name}'. Available: {names}.",
                            "Pick one with --target <name>.");
                }
                else if (pcInterface.TargetInterfaces.Count == 1)
                {
                    targetInterface = pcInterface.TargetInterfaces[0];
                }
                else
                {
                    throw new SessionException(WireErrorCodes.Ambiguous,
                        $"'{pcInterface.Name}' has several target interfaces: {names}.",
                        "Pick one with --target <name>.");
                }

                return new ResolvedTarget
                {
                    Mode = mode.Name,
                    PcInterface = pcInterface.Name,
                    Configuration = targetInterface,
                    Display = string.IsNullOrEmpty(address) ? targetInterface.Name : $"{targetInterface.Name} ({address})",
                };
            }

            if (string.IsNullOrEmpty(address))
            {
                if (pcInterface.Addresses.Count == 0)
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        "No target address. Give one with --address <ip>.");
                var first = pcInterface.Addresses[0];
                return new ResolvedTarget
                {
                    Mode = mode.Name,
                    PcInterface = pcInterface.Name,
                    Configuration = first,
                    Address = first,
                    Display = first.Address,
                };
            }

            var found = pcInterface.Addresses.Find(address) ?? pcInterface.Addresses.Create(address);
            return new ResolvedTarget
            {
                Mode = mode.Name,
                PcInterface = pcInterface.Name,
                Configuration = found,
                Address = found,
                Display = found.Address,
            };
        }

        /// <summary>The device's own configured IP, for when the caller did not name a target.</summary>
        private string DeviceAddressFallback(string deviceName)
        {
            try
            {
                var node = FindEthernetNode(FindDevice(RequireProject(), deviceName));
                return node?.Item2.GetAttribute("Address") as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One prompt of TIA's download dialog. The safe answers mirror what the dialog preselects
        /// for a routine consistent download; everything that stops being routine - erasing memory,
        /// changing protection, firmware up/downgrades - waits for Force. Passwords are refused
        /// outright: a CLI has no business holding PLC passwords in argv.
        /// </summary>
        private static void AnswerDownloadPrompt(DownloadConfiguration prompt, TransferPlan plan,
            List<string> decisions, List<SessionException> refusals)
        {
            switch (prompt)
            {
                // -------- routine: what the dialog would preselect anyway
                case StopModules stop:
                    stop.CurrentSelection = StopModulesSelections.StopAll;
                    decisions.Add("stop modules: stop all"); return;
                case StartModules start:
                    start.CurrentSelection = plan.NoStart
                        ? StartModulesSelections.NoAction
                        : StartModulesSelections.StartModule;
                    decisions.Add(plan.NoStart ? "start after download: left stopped" : "start after download: start");
                    return;
                case AllBlocksDownload all:
                    all.CurrentSelection = AllBlocksDownloadSelections.DownloadAllBlocks;
                    decisions.Add("blocks: download all"); return;
                case ConsistentBlocksDownload consistent:
                    consistent.CurrentSelection = ConsistentBlocksDownloadSelections.ConsistentDownload;
                    decisions.Add("blocks: consistent download"); return;
                case AlarmTextLibrariesDownload alarms:
                    alarms.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload;
                    decisions.Add("alarm text libraries: consistent download"); return;
                case OverwriteSystemData overwrite:
                    overwrite.CurrentSelection = OverwriteSystemDataSelections.Overwrite;
                    decisions.Add("system data: overwrite"); return;
                case DifferentTargetConfiguration different:
                    different.CurrentSelection = DifferentTargetConfigurationSelections.AcceptAll;
                    decisions.Add("target configuration differs: accept"); return;
                case ExpandDownload expand:
                    expand.CurrentSelection = ExpandDownloadSelections.Download;
                    decisions.Add("expanded download: download"); return;
                case WaitOnReboot wait:
                    wait.CurrentSelection = WaitOnRebootSelections.Wait;
                    decisions.Add("reboot: wait"); return;
                case LoadIdentificationData ident:
                    ident.CurrentSelection = LoadIdentificationDataSelections.LoadNothing;
                    decisions.Add("identification data: skip"); return;
                case TargetForSoftware targetFor:
                    targetFor.CurrentSelection = plan.SimulationAdvancedTarget
                        ? TargetForSoftwareSelections.PlcSimulationAdvanced
                        : TargetForSoftwareSelections.CPU;
                    decisions.Add("software target: " +
                        (plan.SimulationAdvancedTarget ? "PLCSIM Advanced" : "CPU"));
                    return;
                case Siemens.Engineering.Safety.Download.Configurations.SafetyProgram safety:
                    safety.CurrentSelection = Siemens.Engineering.Safety.Download.Configurations
                        .SafetyProgramSelections.ConsistentDownload;
                    decisions.Add("safety program: consistent download"); return;
                case ActiveTestCanBeAborted testAbort:
                    testAbort.CurrentSelection = ActiveTestCanBeAbortedSelections.AcceptAll;
                    decisions.Add("active test: abort"); return;
                case ActiveTestCanPreventDownload testPrevent:
                    testPrevent.CurrentSelection = ActiveTestCanPreventDownloadSelections.AcceptAll;
                    decisions.Add("active test: abort"); return;
                case UserManagementDownload users:
                    // Keeping what is on the PLC is the choice that cannot lock anybody out.
                    users.CurrentSelection = UserManagementPreDownloadSelections.KeepOnlineUserManagementData;
                    decisions.Add("user management: keep what is on the PLC"); return;

                // -------- destructive or surprising: only under --force. Otherwise declined - with the
                // prompt's harmless answer where it has one - and reported once the download returns.
                case ResetModule reset:
                    reset.CurrentSelection = Allowed(plan, decisions, refusals, "reset module (erases it)")
                        ? ResetModuleSelections.DeleteAll
                        : ResetModuleSelections.NoAction;
                    return;
                case DataBlockReinitialization reinit:
                    reinit.CurrentSelection = Allowed(plan, decisions, refusals, "reinitialize data blocks (loses current values)")
                        ? DataBlockReinitializationSelections.StopPlcAndReinitialize
                        : DataBlockReinitializationSelections.NoAction;
                    return;
                case InitializeMemory memory:
                    memory.CurrentSelection = Allowed(plan, decisions, refusals, "initialize memory")
                        ? InitializeMemorySelections.AcceptAll
                        : InitializeMemorySelections.NoAction;
                    return;
                case OverwriteOnMemoryCard card:
                    card.CurrentSelection = Allowed(plan, decisions, refusals, "overwrite the memory card")
                        ? OverwriteOnMemoryCardSelections.Load
                        : OverwriteOnMemoryCardSelections.NoAction;
                    return;
                case ProtectionLevelChanged protection:
                    protection.CurrentSelection = Allowed(plan, decisions, refusals, "continue although the protection level changed")
                        ? ProtectionLevelChangedSelections.ContinueDownloading
                        : ProtectionLevelChangedSelections.NoChange;
                    return;
                case SelectiveDeleteDownload delete:
                    // No harmless choice exists for this one; declining means leaving it unanswered.
                    if (Allowed(plan, decisions, refusals, "delete data on the target"))
                        delete.CurrentSelection = SelectiveDeleteDataSelections.AcceptAll;
                    return;
                case UpgradeTargetDevice upgrade:
                    upgrade.Checked = Allowed(plan, decisions, refusals, "upgrade the target device");
                    return;
                case DowngradeTargetDevice downgrade:
                    downgrade.Checked = Allowed(plan, decisions, refusals, "downgrade the target device");
                    return;

                // -------- credentials: never
                case DownloadPasswordConfiguration _:
                    refusals.Add(new SessionException(WireErrorCodes.AccessDenied,
                        "The target asks for a password, and this tool does not handle PLC passwords.",
                        "Download once from the TIA Portal UI, or remove the protection for commissioning."));
                    return;
            }

            // The catch-all comes last so the specific cases above win. It covers the plain
            // pre-download check and the small confirmations (overwrite HMI data, fit components,
            // certificates) - the dialog's checked-by-default boxes.
            if (prompt is DownloadCheckConfiguration check)
            {
                check.Checked = true;
                decisions.Add("confirmed: " + prompt.GetType().Name);
                return;
            }

            refusals.Add(new SessionException(WireErrorCodes.OpennessError,
                $"The download asked something this tool does not know how to answer: " +
                $"{prompt.GetType().Name}.",
                "Do this download once from the TIA Portal UI, and report the prompt name."));
        }

        /// <summary>
        /// True when the plan allows a destructive answer. Otherwise the refusal is recorded rather than
        /// thrown - this runs inside TIA's callback, where an exception takes the portal down - and the
        /// caller declines the prompt.
        /// </summary>
        private static bool Allowed(TransferPlan plan, List<string> decisions,
            List<SessionException> refusals, string what)
        {
            if (plan.Force)
            {
                decisions.Add("forced: " + what);
                return true;
            }

            refusals.Add(new SessionException(WireErrorCodes.InvalidRequest,
                $"The download wants to {what}, which needs an explicit go-ahead.",
                "Re-run with --force to allow it."));
            return false;
        }

        private static void AnswerUploadPrompt(
            Siemens.Engineering.Upload.Configurations.UploadConfiguration prompt, List<string> decisions,
            List<SessionException> refusals)
        {
            switch (prompt)
            {
                case Siemens.Engineering.Upload.Configurations.UploadMissingProducts missing:
                    missing.CurrentSelection = Siemens.Engineering.Upload.Configurations
                        .UploadMissingProductsSelections.TryUpload;
                    decisions.Add("modules without installed products: try anyway"); return;
                case Siemens.Engineering.Upload.Configurations.UploadPasswordConfiguration _:
                    refusals.Add(new SessionException(WireErrorCodes.AccessDenied,
                        "The PLC asks for a password, and this tool does not handle PLC passwords.",
                        "Upload once from the TIA Portal UI instead."));
                    return;
            }

            refusals.Add(new SessionException(WireErrorCodes.OpennessError,
                $"The upload asked something this tool does not know how to answer: " +
                $"{prompt.GetType().Name}.",
                "Do this upload once from the TIA Portal UI, and report the prompt name."));
        }

        private static TransferResultDto DescribeTransfer(string operation, string device,
            TransferPlan plan, ResolvedTarget target, List<string> decisions)
        {
            return new TransferResultDto
            {
                Operation = operation,
                Device = device,
                Mode = target.Mode,
                PcInterface = target.PcInterface,
                TargetAddress = target.Display,
                Decisions = decisions,
                Messages = new List<string>(),
            };
        }

        private static void FlattenTransferMessages(DownloadResultMessageComposition messages,
            int depth, List<string> into)
        {
            if (messages == null) return;
            foreach (DownloadResultMessage message in messages)
            {
                into.Add(new string(' ', depth * 2) + message.State + ": " + message.Message);
                FlattenTransferMessages(message.Messages, depth + 1, into);
            }
        }

        private static void FlattenTransferMessages(UploadResultMessageComposition messages,
            int depth, List<string> into)
        {
            if (messages == null) return;
            foreach (UploadResultMessage message in messages)
            {
                into.Add(new string(' ', depth * 2) + message.State + ": " + message.Message);
                FlattenTransferMessages(message.Messages, depth + 1, into);
            }
        }

        /// <summary>The CPU device item, which is what carries the online services.</summary>
        private DeviceItem RequireCpuItem(string deviceName)
        {
            var device = FindDevice(RequireProject(), deviceName);
            var carrier = FindSoftwareCarrier(device);
            if (carrier == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    $"Device '{device.Name}' carries no PLC software (it may be an HMI or a GSD device).");
            return carrier.Item1;
        }

        // ---------------------------------------------------------------- sources

        /// <summary>
        /// Writes SCL to a file, registers it as an external source and asks TIA to compile it into
        /// blocks. This is the only route from SCL text to a real block: block XML import needs the
        /// full Openness schema, and CreateFB makes an empty block with no way to set its body.
        /// </summary>
        public SourceImportDto ImportScl(string deviceName, string sourceName, string code,
            string filePath, bool generate)
        {
            var software = RequirePlcSoftware(deviceName);

            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(filePath))
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "Provide either the SCL text or a path to an .scl file.");

            var name = sourceName;
            if (!name.EndsWith(".scl", StringComparison.OrdinalIgnoreCase)) name += ".scl";

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

            if (generate)
            {
                // KeepOnError leaves whatever compiled behind instead of rolling everything back, so
                // a partially valid source still shows the caller how far it got.
                var blocks = source.GenerateBlocksFromSource(GenerateBlockOption.KeepOnError);
                if (blocks != null)
                {
                    foreach (PlcBlock block in blocks)
                        result.GeneratedBlocks.Add(block.Name);
                }

                if (result.GeneratedBlocks.Count == 0)
                    throw new SessionException(WireErrorCodes.OpennessError,
                        $"Source '{name}' produced no blocks. The SCL almost certainly has a syntax " +
                        "error; open the source in TIA Portal to see the compiler message.");
            }

            return result;
        }

        // ---------------------------------------------------------------- blocks

        public List<BlockDto> ListBlocks(string deviceName, string nameFilter, bool includeSystemGroups)
        {
            var software = RequirePlcSoftware(deviceName);
            var blocks = new List<BlockDto>();
            CollectBlocks(software.BlockGroup, software.BlockGroup.Name, blocks, includeSystemGroups);

            if (!string.IsNullOrEmpty(nameFilter))
            {
                blocks = blocks
                    .Where(b => b.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            return blocks;
        }

        public ExportResultDto ExportBlock(string deviceName, string blockPath, string targetPath,
            bool inline, int maxInlineChars)
        {
            var software = RequirePlcSoftware(deviceName);
            var block = FindBlock(software, blockPath);

            if (block.IsKnowHowProtected)
                throw new SessionException(WireErrorCodes.AccessDenied,
                    $"Block '{block.Name}' is know-how protected and cannot be exported.");

            var file = new FileInfo(targetPath);
            if (file.Directory != null && !file.Directory.Exists) file.Directory.Create();
            // Openness refuses to overwrite; clearing first makes repeated exports idempotent.
            if (file.Exists) file.Delete();

            block.Export(file, ExportOptions.None);
            file.Refresh();

            var dto = new ExportResultDto
            {
                Name = block.Name,
                FilePath = file.FullName,
                Bytes = file.Length,
            };

            if (inline)
            {
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
            }

            return dto;
        }

        // ---------------------------------------------------------------- tags

        public List<TagTableDto> ListTagTables(string deviceName)
        {
            var software = RequirePlcSoftware(deviceName);
            var tables = new List<TagTableDto>();
            CollectTagTables(software.TagTableGroup, software.TagTableGroup.Name, tables);
            return tables;
        }

        public List<TagDto> ListTags(string deviceName, string tableName)
        {
            var software = RequirePlcSoftware(deviceName);
            var result = new List<TagDto>();
            var matchedTable = false;

            foreach (var table in EnumerateTagTables(software.TagTableGroup))
            {
                if (!string.IsNullOrEmpty(tableName) &&
                    !string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                matchedTable = true;
                foreach (PlcTag tag in table.Tags)
                {
                    result.Add(new TagDto
                    {
                        Name = tag.Name,
                        TableName = table.Name,
                        DataType = tag.DataTypeName,
                        LogicalAddress = tag.LogicalAddress,
                        Comment = FirstText(tag.Comment),
                    });
                }
            }

            // Distinguishing "no such table" from "table is empty" matters: an empty result would
            // otherwise read as success for a name that does not exist.
            if (!string.IsNullOrEmpty(tableName) && !matchedTable)
                throw new SessionException(WireErrorCodes.NotFound, $"Tag table '{tableName}' not found.");

            return result;
        }

        public TagTableDto CreateTagTable(string deviceName, string name)
        {
            var software = RequirePlcSoftware(deviceName);

            var existing = software.TagTableGroup.TagTables.Find(name);
            if (existing != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"Tag table '{name}' already exists.");

            var table = software.TagTableGroup.TagTables.Create(name);
            return new TagTableDto
            {
                Name = table.Name,
                Path = software.TagTableGroup.Name,
                IsDefault = table.IsDefault,
                TagCount = table.Tags.Count,
            };
        }

        public TagDto CreateTag(string deviceName, string tableName, string name, string dataType,
            string address)
        {
            var software = RequirePlcSoftware(deviceName);

            PlcTagTable table;
            if (string.IsNullOrEmpty(tableName))
            {
                // The default table always exists; falling back to it means a caller can add a tag
                // without first having to learn the table layout.
                table = EnumerateTagTables(software.TagTableGroup).FirstOrDefault(t => t.IsDefault)
                        ?? EnumerateTagTables(software.TagTableGroup).FirstOrDefault();
                if (table == null)
                    throw new SessionException(WireErrorCodes.NotFound,
                        $"Device '{deviceName}' has no tag table to add to.");
            }
            else
            {
                table = EnumerateTagTables(software.TagTableGroup).FirstOrDefault(t =>
                    string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
                if (table == null)
                    throw new SessionException(WireErrorCodes.NotFound,
                        $"Tag table '{tableName}' not found.");
            }

            if (table.Tags.Find(name) != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"Tag '{name}' already exists in table '{table.Name}'.");

            var tag = string.IsNullOrEmpty(address)
                ? table.Tags.Create(name)
                : table.Tags.Create(name, dataType, address);

            if (string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(dataType))
                tag.DataTypeName = dataType;

            return new TagDto
            {
                Name = tag.Name,
                TableName = table.Name,
                DataType = tag.DataTypeName,
                LogicalAddress = tag.LogicalAddress,
                Comment = FirstText(tag.Comment),
            };
        }

        // ---------------------------------------------------------------- compile

        /// <summary>
        /// Compiles the device's hardware configuration, then its PLC software - what TIA's "Compile >
        /// Hardware and software" does. Software alone reported success on projects whose hardware
        /// could never be downloaded: protection and certificate settings, for one, are hardware
        /// configuration, and a download refuses them while a software compile never looks.
        /// </summary>
        public CompileResultDto Compile(string deviceName)
        {
            var messages = new List<CompileMessageDto>();
            var errors = 0;
            var warnings = 0;

            var device = FindDevice(RequireProject(), deviceName);
            var hardware = ((IEngineeringServiceProvider)device).GetService<ICompilable>();
            if (hardware != null)
            {
                var result = hardware.Compile();
                FlattenMessages(result.Messages, messages);
                errors += result.ErrorCount;
                warnings += result.WarningCount;
            }

            var software = RequirePlcSoftware(deviceName).GetService<ICompilable>();
            if (software == null && hardware == null)
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"'{deviceName}' does not support compilation.");

            if (software != null)
            {
                var result = software.Compile();
                FlattenMessages(result.Messages, messages);
                errors += result.ErrorCount;
                warnings += result.WarningCount;
            }

            return new CompileResultDto
            {
                State = errors > 0 ? "Error" : warnings > 0 ? "Warning" : "Success",
                ErrorCount = errors,
                WarningCount = warnings,
                Messages = messages,
            };
        }

        // ---------------------------------------------------------------- helpers

        private void RequireDisconnected()
        {
            if (_portal != null)
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    "This session is already connected to TIA Portal.");
        }

        private TiaPortal RequirePortal()
        {
            if (_portal == null)
                throw new SessionException(WireErrorCodes.NotConnected,
                    "Not connected to TIA Portal.");
            return _portal;
        }

        private Project RequireProject()
        {
            RequirePortal();
            // Re-probe before failing: when we are attached to someone's portal, they may have opened
            // a project in the UI since the last command ran.
            if (_project == null) AdoptOpenProject();

            if (_project == null)
                throw new SessionException(WireErrorCodes.NoProjectOpen,
                    "No project is open.",
                    "Open one with 'tia project open <path>', or open it in TIA Portal and re-run.");
            return _project;
        }

        private void AdoptOpenProject()
        {
            try
            {
                if (_portal != null && _portal.Projects.Count > 0) _project = _portal.Projects[0];
            }
            catch
            {
                // A portal mid-teardown throws here; the caller's own error is the useful one.
            }
        }

        private PlcSoftware RequirePlcSoftware(string deviceName)
        {
            var project = RequireProject();

            var candidates = EnumerateDevices(project).ToList();
            var device = candidates.FirstOrDefault(d =>
                string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                // The caller may have passed the CPU item name instead of the station name; both are
                // shown by 'tia devices' and confusing them is the most common mistake.
                device = candidates.FirstOrDefault(d =>
                {
                    var carrier = FindSoftwareCarrier(d);
                    return carrier != null &&
                           string.Equals(carrier.Item1.Name, deviceName, StringComparison.OrdinalIgnoreCase);
                });
            }

            if (device == null)
                throw candidates.Count == 0
                    ? new SessionException(WireErrorCodes.NotFound,
                        $"Device '{deviceName}' not found: the project has no devices at all.",
                        "Add a PLC with 'tia device add <typeIdentifier> <name>' first.")
                    : new SessionException(WireErrorCodes.NotFound,
                        $"Device '{deviceName}' not found. Known devices: " +
                        string.Join(", ", candidates.Select(d => d.Name)));

            var found = FindSoftwareCarrier(device);
            var software = found?.Item2 as PlcSoftware;
            if (software == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    $"Device '{device.Name}' carries no PLC software (it may be an HMI or a GSD device).");

            return software;
        }

        /// <summary>Resolves a station name or a CPU item name to its Device, like RequirePlcSoftware.</summary>
        private static Device FindDevice(Project project, string deviceName)
        {
            var candidates = EnumerateDevices(project).ToList();

            var device = candidates.FirstOrDefault(d =>
                string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
            if (device != null) return device;

            device = candidates.FirstOrDefault(d =>
            {
                var carrier = FindSoftwareCarrier(d);
                return carrier != null &&
                       string.Equals(carrier.Item1.Name, deviceName, StringComparison.OrdinalIgnoreCase);
            });
            if (device != null) return device;

            throw candidates.Count == 0
                ? new SessionException(WireErrorCodes.NotFound,
                    $"Device '{deviceName}' not found: the project has no devices at all.",
                    "Add a PLC with 'tia device add <typeIdentifier> <name>' first.")
                : new SessionException(WireErrorCodes.NotFound,
                    $"Device '{deviceName}' not found. Known devices: " +
                    string.Join(", ", candidates.Select(d => d.Name)));
        }

        /// <summary>
        /// Finds the first Ethernet node under a device. The interface lives on a nested DeviceItem,
        /// not the station, so this recurses the same way FindSoftwareCarrier does.
        /// </summary>
        private static Tuple<DeviceItem, Node> FindEthernetNode(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                var found = FindEthernetNode(item);
                if (found != null) return found;
            }
            return null;
        }

        private static Tuple<DeviceItem, Node> FindEthernetNode(DeviceItem item)
        {
            var network = item.GetService<NetworkInterface>();
            if (network != null)
            {
                foreach (Node node in network.Nodes)
                    return Tuple.Create(item, node);
            }

            foreach (DeviceItem child in item.DeviceItems)
            {
                var found = FindEthernetNode(child);
                if (found != null) return found;
            }
            return null;
        }

        private static IEnumerable<Device> EnumerateDevices(Project project)
        {
            foreach (Device device in project.Devices) yield return device;

            foreach (var group in project.DeviceGroups)
                foreach (var device in EnumerateDevices(group))
                    yield return device;
        }

        private static IEnumerable<Device> EnumerateDevices(DeviceUserGroup group)
        {
            foreach (Device device in group.Devices) yield return device;

            foreach (var child in group.Groups)
                foreach (var device in EnumerateDevices(child))
                    yield return device;
        }

        /// <summary>
        /// Walks the DeviceItem tree looking for the item that exposes a SoftwareContainer. The
        /// container is rarely on the top-level item, so this has to recurse.
        /// </summary>
        private static Tuple<DeviceItem, Software> FindSoftwareCarrier(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                var found = FindSoftwareCarrier(item);
                if (found != null) return found;
            }
            return null;
        }

        private static Tuple<DeviceItem, Software> FindSoftwareCarrier(DeviceItem item)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software != null)
                return Tuple.Create(item, container.Software);

            foreach (DeviceItem child in item.DeviceItems)
            {
                var found = FindSoftwareCarrier(child);
                if (found != null) return found;
            }
            return null;
        }

        private static IEnumerable<string> CollectAddresses(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
                foreach (var address in CollectAddresses(item))
                    yield return address;
        }

        private static IEnumerable<string> CollectAddresses(DeviceItem item)
        {
            var network = item.GetService<NetworkInterface>();
            if (network != null)
            {
                foreach (Node node in network.Nodes)
                {
                    var address = node.GetAttribute("Address") as string;
                    if (!string.IsNullOrEmpty(address))
                        yield return address;
                }
            }

            foreach (DeviceItem child in item.DeviceItems)
                foreach (var address in CollectAddresses(child))
                    yield return address;
        }

        private static void CollectBlocks(PlcBlockGroup group, string path, List<BlockDto> sink,
            bool includeSystemGroups)
        {
            foreach (PlcBlock block in group.Blocks)
                sink.Add(Describe(block, path));

            foreach (PlcBlockUserGroup child in group.Groups)
                CollectBlocks(child, path + "/" + child.Name, sink, includeSystemGroups);

            if (includeSystemGroups && group is PlcBlockSystemGroup systemGroup)
            {
                foreach (PlcSystemBlockGroup child in systemGroup.SystemBlockGroups)
                    CollectSystemBlocks(child, path + "/" + child.Name, sink);
            }
        }

        // PlcSystemBlockGroup is not a PlcBlockGroup - it is a parallel type with its own Groups
        // composition, so the traversal cannot be shared with CollectBlocks.
        private static void CollectSystemBlocks(PlcSystemBlockGroup group, string path, List<BlockDto> sink)
        {
            foreach (PlcBlock block in group.Blocks)
                sink.Add(Describe(block, path));

            foreach (PlcSystemBlockGroup child in group.Groups)
                CollectSystemBlocks(child, path + "/" + child.Name, sink);
        }

        private static PlcBlock FindBlock(PlcSoftware software, string blockPath)
        {
            var all = new List<BlockDto>();
            CollectBlocks(software.BlockGroup, software.BlockGroup.Name, all, true);

            // Accept either a bare name ("MyFB") or a full group path ("Program blocks/Grp/MyFB").
            var wanted = blockPath.Replace('\\', '/').Trim('/');
            var leaf = wanted.Contains("/") ? wanted.Substring(wanted.LastIndexOf('/') + 1) : wanted;

            var group = ResolveGroupFor(software, wanted);
            var block = group.Blocks.Find(leaf);
            if (block != null) return block;

            var matches = all.Where(b =>
                string.Equals(b.Name, leaf, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 1)
            {
                var only = matches[0];
                var owner = ResolveGroupFor(software, only.Path + "/" + only.Name);
                var resolved = owner.Blocks.Find(only.Name);
                if (resolved != null) return resolved;
            }

            if (matches.Count > 1)
                throw new SessionException(WireErrorCodes.Ambiguous,
                    $"Block '{leaf}' is ambiguous. Qualify it with a group path: " +
                    string.Join(", ", matches.Select(m => m.Path + "/" + m.Name)));

            throw new SessionException(WireErrorCodes.NotFound, $"Block '{blockPath}' not found.");
        }

        /// <summary>Resolves the group that should contain the leaf named by <paramref name="fullPath"/>.</summary>
        private static PlcBlockGroup ResolveGroupFor(PlcSoftware software, string fullPath)
        {
            PlcBlockGroup group = software.BlockGroup;
            var segments = fullPath.Replace('\\', '/').Trim('/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            // Drop the leaf, and the root group name when the caller included it.
            var start = segments.Length > 0 &&
                        string.Equals(segments[0], software.BlockGroup.Name, StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;

            for (var i = start; i < segments.Length - 1; i++)
            {
                var next = group.Groups.FirstOrDefault(g =>
                    string.Equals(g.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (next == null) return group;
                group = next;
            }

            return group;
        }

        private static IEnumerable<PlcTagTable> EnumerateTagTables(PlcTagTableGroup group)
        {
            foreach (PlcTagTable table in group.TagTables) yield return table;

            foreach (PlcTagTableUserGroup child in group.Groups)
                foreach (var table in EnumerateTagTables(child))
                    yield return table;
        }

        private static void CollectTagTables(PlcTagTableGroup group, string path, List<TagTableDto> sink)
        {
            foreach (PlcTagTable table in group.TagTables)
            {
                sink.Add(new TagTableDto
                {
                    Name = table.Name,
                    Path = path,
                    IsDefault = table.IsDefault,
                    TagCount = table.Tags.Count,
                });
            }

            foreach (PlcTagTableUserGroup child in group.Groups)
                CollectTagTables(child, path + "/" + child.Name, sink);
        }

        private static void FlattenMessages(CompilerResultMessageComposition messages,
            List<CompileMessageDto> sink)
        {
            if (messages == null) return;

            foreach (CompilerResultMessage message in messages)
            {
                sink.Add(new CompileMessageDto
                {
                    State = message.State.ToString(),
                    Path = message.Path,
                    Description = message.Description,
                });
                // Nested messages carry the actual errors; a flat read reports success wrongly.
                FlattenMessages(message.Messages, sink);
            }
        }

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

        private static DeviceDto Describe(Device device)
        {
            var dto = new DeviceDto
            {
                Name = device.Name,
                TypeIdentifier = device.TypeIdentifier,
                IsGsd = device.IsGsd,
                Addresses = new List<string>(),
            };

            var carrier = FindSoftwareCarrier(device);
            if (carrier != null)
            {
                dto.CpuItemName = carrier.Item1.Name;
                dto.CpuTypeIdentifier = carrier.Item1.TypeIdentifier;
                dto.HasPlcSoftware = carrier.Item2 is PlcSoftware;
                dto.PlcSoftwareName = carrier.Item2.Name;
            }

            foreach (var address in CollectAddresses(device))
                dto.Addresses.Add(address);

            return dto;
        }

        private static ProjectDto Describe(Project project)
        {
            return new ProjectDto
            {
                Name = project.Name,
                Path = project.Path?.FullName,
                Author = project.Author,
                Version = project.Version,
                CreationTime = Iso(project.CreationTime),
                LastModified = Iso(project.LastModified),
                LastModifiedBy = project.LastModifiedBy,
                IsModified = project.IsModified,
                DeviceCount = EnumerateDevices(project).Count(),
            };
        }

        private static string FirstText(MultilingualText text)
        {
            if (text == null) return null;
            foreach (MultilingualTextItem item in text.Items)
            {
                if (!string.IsNullOrEmpty(item.Text)) return item.Text;
            }
            return null;
        }

        private static string Iso(DateTime value)
        {
            return value == default(DateTime)
                ? null
                : value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public void Dispose() => Disconnect();
    }

    /// <summary>An error that maps to a specific wire error code rather than a generic failure.</summary>
    internal sealed class SessionException : Exception
    {
        public string Code { get; }

        /// <summary>Advice printed under the message. Set it when the fix is not obvious.</summary>
        public string Hint { get; }

        public SessionException(string code, string message, string hint = null) : base(message)
        {
            Code = code;
            Hint = hint;
        }
    }
}
