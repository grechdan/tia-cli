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
  device attrs <device>    List the CPU's Openness attributes             [--filter <text>]
  device set <device> <attribute> <value>
                           Set one attribute
  device protection <device>
                           Access level and passwords, which a new CPU needs before it compiles
                                 [--level <FullAccess|ReadAccess|HMIAccess|NoAccess>]
                                 [--password <full access>] [--secret <confidential config data>]
  device add <type> <name> Add a station from a type identifier    [--device-name <n>]
  device ip <device>       Set the Ethernet address        [--address <ip>] [--mask <netmask>]
                                                           [--subnet <name>] names the TIA subnet
                                                           [--router <ip>] [--no-router]

PROGRAM BLOCKS
  blocks <device>          List program blocks    [--filter <text>] [--type OB,FB,FC,DB]
                                                  [--tree] shows the folders  [--system]
  block show <device> <block>
                           Header, interface and network titles of one block
  block source <device> <block>
                           Print the block as SCL/STL/DB text   [--out <path>] [--deps]
                           LAD, FBD and GRAPH blocks have no text form - use 'block export'.
  block export <device> <block>
                           Export a block as Openness XML  [--out <path>] [--print]
  block import <device> <file>
                           Import blocks from Openness XML [--folder <path>] [--overwrite]
  block rename <device> <block> <new name>
                           Rename a block. Callers are not rewritten; compile to find them.
  block delete <device> <block>
                           Delete a block                  [--force] skips the question
  folder add <device> <path>
                           Create a folder, and any missing folder above it
  folder delete <device> <path>
                           Delete a folder and everything in it            [--force]

  A block is named by a bare name (""Motor"") or by its folder path (""Pumps/Motor""). A bare
  name that two folders both hold is refused rather than guessed at.

EXTERNAL SOURCES
  sources <device>         List external sources
  source add <device> <name>
                           Add SCL and compile it into blocks
                                          [--file <path> | --code <text> | stdin]
                                          [--no-generate] keeps the source without compiling
                                          [--folder <path>] puts the blocks in a folder
  source generate <device> <name>
                           Compile a source already in the project    [--folder <path>]
  source delete <device> <name>
                           Delete a source. Blocks it made stay.      [--force]

TAGS
  tables <device>          List tag tables
  tags <device>            List tags                       [--table <name>]
  table add <device> <name>
                           Create a tag table
  tag add <device> <name>  Create a tag       [--table <t>] [--type <dt>] [--address <addr>]
  compile <device>         Compile the PLC software. Exits non-zero when it reports errors.

PLC (online)
  download <device>        Download to the PLC     [--address <ip>] [--via <mode>, default PN/IE]
                                                   [--interface <name>] [--slot <n>] [--hardware]
                                                   [--changes] [--stopped] [--force] [--target <name>]
                                                   [--secret <master secret>] [--plc-password <pw>]
                           Routine prompts are answered like the dialog's defaults; destructive
                           ones (reset, reinitialize, up/downgrade) abort unless --force.
                           Password-protected targets are refused - use the TIA Portal UI.
  upload <ip>              Upload the station at <ip> into the project as a new station
                                                   [--via <mode>] [--interface <name>] [--slot <n>]
  sim create <device>      Create a PLCSIM instance for the device and power it on
                                                   [--cpu <type>] [--address <ip>] [--mask <netmask>]
                                                   [--timeout <ms>]
                           S7-1500 and friends only - PLCSIM's API has no S7-1200. Download to it
                           afterwards with 'tia download <device>'.
  sim start <device>       Download to S7-PLCSIM; from V18 start the instance first
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
  tia blocks PLC_1 --tree                      # the program tree, folders and all
  tia blocks PLC_1 --type FB --filter Motor
  tia block show PLC_1 Motor                   # interface and networks, without opening TIA
  tia block source PLC_1 Motor > Motor.scl     # round-trips through 'tia source add'
  tia block export PLC_1 Motor --print > Motor.xml
  type block.scl | tia source add PLC_1 Motor
  tia compile PLC_1 && tia project save
  tia show hw --view network                   # put it on screen instead of clicking there

EXIT CODES
  0 ok   1 failed   2 bad arguments   3 no session   4 no project   5 not found
  6 ambiguous   7 access denied   8 licence missing   9 portal unusable");
        }
    }
}
