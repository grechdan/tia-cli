using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace TiaCli.Openness
{
    /// <summary>
    /// Reads TIA Portal's Openness whitelist.
    ///
    /// The first time a given executable talks to Openness, TIA Portal puts an "Openness access"
    /// window on screen and blocks the call until somebody answers it. On approval it records the
    /// exe's path and a base64 SHA-256 of its bytes under HKLM. The hash is of the file, so every
    /// rebuild of tia.exe needs a fresh approval - which is worth warning about, because otherwise
    /// the first command after a rebuild just appears to hang, and a daemon started in the
    /// background hangs where nobody can see the dialog it is waiting for.
    ///
    /// Nothing here references a Siemens type, so it is safe to call before the resolver runs.
    /// </summary>
    internal static class OpennessWhitelist
    {
        private const string RegistryRoot = @"SOFTWARE\Siemens\Automation\Openness";

        /// <summary>
        /// True when this exact file is already approved. False means the next Openness call will
        /// most likely wait for a dialog. Any uncertainty is reported as approved: a missing warning
        /// is much better than a wrong one telling people to expect a dialog that never appears.
        /// </summary>
        public static bool IsApproved(string executablePath)
        {
            try
            {
                if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath)) return true;

                var fullPath = Path.GetFullPath(executablePath);
                var name = Path.GetFileName(fullPath);
                var hash = HashOf(fullPath);

                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var root = hklm.OpenSubKey(RegistryRoot))
                {
                    if (root == null) return true;

                    foreach (var version in root.GetSubKeyNames())
                    {
                        using (var entries = root.OpenSubKey(version + @"\Whitelist\" + name))
                        {
                            if (entries == null) continue;

                            foreach (var entryName in entries.GetSubKeyNames())
                            {
                                using (var entry = entries.OpenSubKey(entryName))
                                {
                                    if (entry == null) continue;

                                    var path = entry.GetValue("Path") as string;
                                    var recorded = entry.GetValue("FileHash") as string;

                                    if (string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(recorded, hash, StringComparison.Ordinal))
                                        return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static string HashOf(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        /// <summary>The warning to print when <see cref="IsApproved"/> is false.</summary>
        public const string PendingApprovalNotice =
            "TIA Portal has not approved this build of tia.exe for Openness access yet. It will show " +
            "an 'Openness access' window and wait until you answer it - look for that window if this " +
            "seems to hang. Approval is remembered until tia.exe is rebuilt.";
    }
}
