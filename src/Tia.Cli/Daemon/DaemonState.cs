using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaCli.Protocol;

namespace TiaCli.Daemon
{
    /// <summary>
    /// The daemon's advertisement on disk. It is a hint, not a lock: the pipe is the real test of
    /// whether a daemon is alive, and a stale file left by a killed process is expected and handled.
    /// </summary>
    internal sealed class DaemonState
    {
        [JsonPropertyName("pipe")] public string Pipe { get; set; }
        [JsonPropertyName("processId")] public int ProcessId { get; set; }
        [JsonPropertyName("startedAt")] public string StartedAt { get; set; }

        public static void Write(DaemonState state)
        {
            File.WriteAllText(DaemonPaths.StateFile,
                JsonSerializer.Serialize(state, WireJson.Pretty), new UTF8Encoding(false));
        }

        public static DaemonState Read()
        {
            try
            {
                if (!File.Exists(DaemonPaths.StateFile)) return null;
                return JsonSerializer.Deserialize<DaemonState>(
                    File.ReadAllText(DaemonPaths.StateFile), WireJson.Options);
            }
            catch
            {
                return null;
            }
        }

        public static void Delete()
        {
            try { if (File.Exists(DaemonPaths.StateFile)) File.Delete(DaemonPaths.StateFile); }
            catch { }
        }

        public bool IsProcessAlive()
        {
            try
            {
                var process = Process.GetProcessById(ProcessId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
