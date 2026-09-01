using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaCli.Daemon
{
    /// <summary>
    /// Where the daemon keeps its pipe, its state file and its log. One daemon per Windows user:
    /// a TIA Portal session belongs to a desktop session, so sharing one between users cannot work.
    /// </summary>
    internal static class DaemonPaths
    {
        public static string PipeName
        {
            get
            {
                var user = new string(Environment.UserName
                    .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    .ToArray());
                if (user.Length == 0) user = "user";
                return "tia-cli-" + user.ToLowerInvariant();
            }
        }

        public static string Directory
        {
            get
            {
                // TIA_CLI_HOME redirects everything (state file, log) elsewhere - tests use it to
                // keep clear of a real daemon's advertisement.
                var dir = Environment.GetEnvironmentVariable("TIA_CLI_HOME");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "tia-cli");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string StateFile => Path.Combine(Directory, "daemon.json");

        public static string LogFile => Path.Combine(Directory, "daemon.log");

        /// <summary>The running tia.exe, used to spawn the daemon as a copy of ourselves.</summary>
        public static string ExecutablePath =>
            System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never be the reason a command fails.
            }
        }

        /// <summary>Keeps the log from growing without bound across many daemon lifetimes.</summary>
        public static void TrimLog()
        {
            try
            {
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > 512 * 1024) file.Delete();
            }
            catch { }
        }
    }
}
