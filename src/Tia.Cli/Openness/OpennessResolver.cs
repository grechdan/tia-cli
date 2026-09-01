using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace TiaCli.Openness
{
    /// <summary>
    /// Locates Siemens.Engineering.dll through the registry and serves it to the CLR on demand.
    ///
    /// Openness assemblies live under Program Files and are never copied next to our exe, so without
    /// this the CLI cannot start. Resolving by registry rather than by hard-coded path is also what
    /// lets a single build target V20 today and V21 later: the CLR asks for the compile-time version
    /// (20.0.0.0), and an AssemblyResolve handler is free to answer with a different one.
    /// </summary>
    internal static class OpennessResolver
    {
        private const string RegistryRoot = @"SOFTWARE\Siemens\Automation\Openness";

        private static readonly string[] AssemblyNames =
        {
            "Siemens.Engineering",
            "Siemens.Engineering.Hmi",
            "Siemens.Engineering.AddIn",
        };

        private static Dictionary<string, string> _pathsByAssemblyName;
        private static string _resolvedVersion;

        public static string ResolvedVersion => _resolvedVersion;

        /// <summary>Openness versions installed on this machine, newest first (e.g. "20.0", "21.0").</summary>
        public static IReadOnlyList<string> InstalledVersions()
        {
            using (var root = OpenRoot())
            {
                if (root == null) return new string[0];
                return root.GetSubKeyNames()
                    .Select(n => new { Name = n, Parsed = ParseVersion(n) })
                    .Where(x => x.Parsed != null)
                    .OrderByDescending(x => x.Parsed)
                    .Select(x => x.Name)
                    .ToList();
            }
        }

        /// <summary>
        /// Installs the resolve handler. <paramref name="requestedVersion"/> is a registry version key
        /// such as "20.0"; null selects the newest installed. Must run before any Siemens type is
        /// touched anywhere in the process.
        /// </summary>
        public static void Install(string requestedVersion)
        {
            if (_resolvedVersion != null) return;

            var versions = InstalledVersions();
            if (versions.Count == 0)
            {
                throw new OpennessSetupException(
                    "No TIA Portal Openness installation found under HKLM\\" + RegistryRoot + ". " +
                    "Install TIA Portal with the Openness option enabled.");
            }

            var version = requestedVersion ?? versions[0];
            if (!versions.Contains(version))
            {
                throw new OpennessSetupException(
                    $"Openness {version} is not installed. Available: {string.Join(", ", versions)}.");
            }

            _pathsByAssemblyName = ReadAssemblyPaths(version);
            if (!_pathsByAssemblyName.ContainsKey("Siemens.Engineering"))
            {
                throw new OpennessSetupException(
                    $"Openness {version} is registered but exposes no Siemens.Engineering assembly path.");
            }

            _resolvedVersion = version;
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static RegistryKey OpenRoot()
        {
            // Openness registers under the 64-bit view; this build is x64 so that is already the
            // default, but being explicit makes a stray AnyCPU/x86 build fail loudly instead of
            // silently finding nothing.
            var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return hklm.OpenSubKey(RegistryRoot);
        }

        private static Version ParseVersion(string name)
        {
            Version v;
            return Version.TryParse(name, out v) ? v : null;
        }

        /// <summary>
        /// Reads HKLM\...\Openness\{version}\PublicAPI\{asmVersion} into assembly name -> dll path,
        /// preferring the highest API level the installation offers.
        /// </summary>
        private static Dictionary<string, string> ReadAssemblyPaths(string version)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var root = OpenRoot())
            using (var versionKey = root?.OpenSubKey(version))
            using (var publicApi = versionKey?.OpenSubKey("PublicAPI"))
            {
                if (publicApi == null) return result;

                var apiLevels = publicApi.GetSubKeyNames()
                    .Select(n => new { Name = n, Parsed = ParseVersion(n) })
                    .Where(x => x.Parsed != null)
                    .OrderByDescending(x => x.Parsed)
                    .Select(x => x.Name);

                foreach (var level in apiLevels)
                {
                    using (var levelKey = publicApi.OpenSubKey(level))
                    {
                        if (levelKey == null) continue;
                        foreach (var asm in AssemblyNames)
                        {
                            if (result.ContainsKey(asm)) continue;
                            var path = levelKey.GetValue(asm) as string;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                result[asm] = path;
                        }
                    }
                    if (result.Count == AssemblyNames.Length) break;
                }
            }

            return result;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;

            string path;
            if (_pathsByAssemblyName != null && _pathsByAssemblyName.TryGetValue(simpleName, out path))
                return Assembly.LoadFrom(path);

            // Siblings such as Siemens.Engineering.Contract are not registered individually but sit
            // next to the main assembly.
            string anchor;
            if (_pathsByAssemblyName != null &&
                _pathsByAssemblyName.TryGetValue("Siemens.Engineering", out anchor) &&
                simpleName.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(anchor) ?? string.Empty, simpleName + ".dll");
                if (File.Exists(sibling)) return Assembly.LoadFrom(sibling);
            }

            return null;
        }
    }

    internal sealed class OpennessSetupException : Exception
    {
        public OpennessSetupException(string message) : base(message) { }
    }
}
