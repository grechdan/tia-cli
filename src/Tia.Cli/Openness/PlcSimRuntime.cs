using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaCli.Protocol;

namespace TiaCli.Openness
{
    /// <summary>
    /// Creates and powers on S7-PLCSIM instances through PLCSIM's own Simulation Runtime API.
    ///
    /// Openness has no start-simulation call, so this is the only way to bring a simulated PLC into
    /// existence; TIA will happily download to one, but never creates it. The API ships inside the
    /// PLCSIM installation rather than a redistributable, so it is loaded by path and called by
    /// reflection: a compile-time reference would break every build on a machine without PLCSIM.
    ///
    /// Its ECPUType enum covers S7-1500, ET200SP/PRO, the software controllers and SINUMERIK. There is
    /// no S7-1200 in it, and RegisterCustomInstance rejects a 1200 article number, so an S7-1200
    /// simulation still has to be started by hand from the PLCSIM window.
    /// </summary>
    internal static class PlcSimRuntime
    {
        private const string ApiFileName = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";

        /// <summary>Newest PLCSIM installation first; null when none is installed.</summary>
        public static string FindApi()
        {
            var roots = new[]
            {
                @"C:\Program Files\Siemens\Automation",
                @"C:\Program Files (x86)\Siemens\Automation",
            };

            return roots
                .Where(Directory.Exists)
                .SelectMany(root => Directory.GetDirectories(root)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return name.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .SelectMany(dir =>
                {
                    try { return Directory.GetFiles(dir, ApiFileName, SearchOption.AllDirectories); }
                    catch { return new string[0]; }
                })
                .FirstOrDefault();
        }

        public static SimulationInstanceDto Create(string cpuType, string name, string ip, string mask,
            string gateway, uint powerOnTimeoutMs)
        {
            var apiPath = FindApi();
            if (apiPath == null)
                throw new SessionException(WireErrorCodes.NotFound,
                    "No S7-PLCSIM installation with the Simulation Runtime API was found.",
                    "Install S7-PLCSIM, or start the instance by hand and use 'tia download'.");

            Assembly api;
            try { api = Assembly.LoadFrom(apiPath); }
            catch (Exception ex)
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"Could not load the PLCSIM runtime API from {apiPath}: {ex.Message}");
            }

            var manager = api.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", true);
            var cpuEnum = api.GetType("Siemens.Simatic.Simulation.Runtime.ECPUType", true);
            var suiteType = api.GetType("Siemens.Simatic.Simulation.Runtime.SIPSuite4", true);

            object cpu;
            try { cpu = Enum.Parse(cpuEnum, cpuType, ignoreCase: true); }
            catch
            {
                throw new SessionException(WireErrorCodes.InvalidRequest,
                    $"PLCSIM does not know a CPU type called '{cpuType}'.",
                    "Give one with --cpu, e.g. CPU1511 or CPU1500_Unspecified. PLCSIM has no S7-1200 " +
                    "type at all: start a 1200 simulation from the PLCSIM window instead.");
            }

            // Registering the same name twice is refused, and running this command again is the normal
            // way to get there - so an instance that already exists is taken over rather than remade.
            var register = manager.GetMethod("RegisterInstance", new[] { cpuEnum, typeof(string) });
            object instance;
            try
            {
                instance = register.Invoke(null, new[] { cpu, name });
            }
            catch (TargetInvocationException ex) when (
                (ex.InnerException?.Message ?? "").IndexOf("AlreadyExists", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var attach = manager.GetMethod("CreateInterface", new[] { typeof(string) });
                if (attach == null)
                    throw new SessionException(WireErrorCodes.InvalidRequest,
                        $"A PLCSIM instance called '{name}' already exists.",
                        "Remove it in the PLCSIM window, then run this again.");

                instance = Unwrap(() => attach.Invoke(null, new object[] { name }),
                    $"attaching to the existing instance '{name}'");
            }

            // Power comes before the address: an instance that is not running has no interface to
            // configure, and SetIPSuite answers InstanceNotRunning.
            var powerOn = instance.GetType().GetMethod("PowerOn", new[] { typeof(uint) });
            var code = Unwrap(() => powerOn.Invoke(instance, new object[] { powerOnTimeoutMs }),
                "powering on the instance");

            var address = Unwrap(() => Activator.CreateInstance(suiteType, ip, mask, gateway ?? "0.0.0.0"),
                "building the address");

            var setIpSuite = instance.GetType().GetMethod("SetIPSuite",
                new[] { typeof(uint), suiteType, typeof(bool) });
            Unwrap(() => { setIpSuite.Invoke(instance, new[] { (object)0u, address, true }); return null; },
                "setting the instance address");

            return new SimulationInstanceDto
            {
                Name = Read(instance, "Name") ?? name,
                CpuType = Read(instance, "CPUType") ?? cpuType,
                Address = Read(instance, "ControllerIP") ?? ip,
                OperatingState = Read(instance, "OperatingState"),
                PowerOnResult = code?.ToString(),
                Api = apiPath,
            };
        }

        private static string Read(object instance, string property)
        {
            try
            {
                var value = instance.GetType().GetProperty(property)?.GetValue(instance);
                if (value == null) return null;

                // ControllerIP is a string[] - one entry per interface - and ToString() on that says
                // "System.String[]", which is how it reached the user the first time.
                if (!(value is string) && value is System.Collections.IEnumerable list)
                    return string.Join(", ", list.Cast<object>().Where(v => v != null).Select(v => v.ToString()));

                return value.ToString();
            }
            catch { return null; }
        }

        /// <summary>Reflection wraps everything in TargetInvocationException; the inner one is the message.</summary>
        private static object Unwrap(Func<object> call, string what)
        {
            try
            {
                return call();
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new SessionException(WireErrorCodes.OpennessError,
                    $"PLCSIM failed while {what}: {ex.InnerException.Message}",
                    "Check that the PLCSIM runtime manager is running and licensed.");
            }
        }
    }
}
