using System;
using System.Reflection;

namespace TiaCli.Cli
{
    internal static class Help
    {
        /// <summary>
        /// Read off the assembly rather than written here, so <c>Version</c> in Directory.Build.props
        /// stays the single source and a release cannot ship announcing the wrong number.
        /// </summary>
        public static readonly string Version = ReadVersion();

        private static string ReadVersion()
        {
            var informational = typeof(Help).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informational))
                return typeof(Help).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

            // SDK builds append "+<commit sha>" once the tree is a git repository.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational.Substring(0, plus) : informational;
        }

        public static void Print(Output output)
        {
            output.Line(@"tia - drive Siemens TIA Portal from the command line, through Openness.

USAGE
  tia <command> [arguments] [options]

HOW IT FINDS TIA PORTAL
  Every command needs a portal. In order, tia will:
    1. use the session started by 'tia session start', if one is running;
    2. otherwise attach to the single TIA Portal already running on this machine;
    3. otherwise stop and tell you, unless --start was given.
  Attaching to a portal you opened yourself is the normal case and needs no setup. A portal that
  tia starts belongs to tia, so it only outlives a single command when a session is holding it.

SESSION
  session start            Start a background session and keep it. Joins a running portal if there
                           is one, otherwise launches its own.
      --new                Launch a new portal even if one is already running
      --headless           Launch it without the TIA Portal window (only with a new portal)
      --attach <pid>       Join this portal specifically
      --project <path>     Open a project as part of starting up
      --idle <minutes>     Stop by itself after this long with nothing to do
  session status           What is running, what it is attached to, what is open
  session stop             End the session. A portal tia started closes; one you started stays.
  shell                    Interactive prompt holding one session open
  portals                  List the TIA Portal instances running on this machine

PROJECT
  project open <path>      Open a project (minutes, not seconds)   [--upgrade]
  project new <dir> <name> Create a project
  project info             Describe the open project
  project save             Save it
  project close            Close it                                [--save]

HARDWARE
  devices                  List stations, their CPUs and addresses
  catalog <filter>         Search the hardware catalog for order numbers   [--limit <n>]
  device add <type> <name> Add a station from a type identifier    [--device-name <n>]
  device ip <device>       Set the Ethernet address        [--address <ip>] [--mask <netmask>]
                                                           [--subnet <name>] names the TIA subnet
                                                           [--router <ip>] [--no-router]

SOFTWARE
  blocks <device>          List program blocks             [--filter <text>] [--system]
  block export <device> <block>
                           Export a block as Openness XML  [--out <path>] [--print]
  scl import <device> <name>
                           Create blocks from SCL          [--file <path> | --code <text> | stdin]
                                                           [--no-generate]
  tables <device>          List tag tables
  tags <device>            List tags                       [--table <name>]
  table add <device> <name>
                           Create a tag table
  tag add <device> <name>  Create a tag       [--table <t>] [--type <dt>] [--address <addr>]
  compile <device>         Compile the PLC software. Exits non-zero when it reports errors.

PLC (online)
  download <device>        Download to the PLC     [--address <ip>] [--via <mode>, default PN/IE]
                                                   [--interface <name>] [--slot <n>] [--hardware]
                                                   [--changes] [--stopped] [--force]
                           Routine prompts are answered like the dialog's defaults; destructive
                           ones (reset, reinitialize, up/downgrade) abort unless --force.
                           Password-protected targets are refused - use the TIA Portal UI.
  upload <ip>              Upload the station at <ip> into the project as a new station
                                                   [--via <mode>] [--interface <name>] [--slot <n>]
  sim start <device>       Download to S7-PLCSIM, starting the simulator on the way
                                                   [--address <ip>] [--advanced] [--stopped]
                           Needs S7-PLCSIM installed; PLCSIM Advanced instances take --advanced.

ON SCREEN (needs a portal with a window, so not --headless)
  show hw                  Open the hardware editor    [--view device|network|topology]
  show device <device>     Open one station in it      [--view device|network|topology]
  show block <device> <block>
                           Open a block's editor
  show table <device> <table>
                           Open a tag table

GLOBAL OPTIONS
  --json                   Print raw JSON instead of tables
  --attach <pid>           Attach to a specific portal for this command
  --start                  Let this command launch its own portal if none is running
  --headless               With --start, launch it without a window
  --new                    Launch a new portal even if one is running
  --no-daemon              Ignore the background session and work in this process
  --openness-version <v>   Pick an installed Openness version, e.g. 20.0
  --quiet                  Suppress incidental notes
  --version                Print the version
  -h, --help               This text

EXAMPLES
  tia portals                                  # what is already running
  tia devices                                  # attach to it and list stations
  tia session start --project C:\p\Line.ap20   # start a session and keep the project open
  tia blocks PLC_1 --filter Motor
  tia block export PLC_1 Motor --print > Motor.xml
  type block.scl | tia scl import PLC_1 Motor
  tia compile PLC_1 && tia project save
  tia show hw --view network                   # put it on screen instead of clicking there

EXIT CODES
  0 ok   1 failed   2 bad arguments   3 no session   4 no project   5 not found
  6 ambiguous   7 access denied   8 licence missing   9 portal unusable");
        }
    }
}
