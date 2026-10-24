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
    1. use the session holding the portal named by --attach <pid> or TIA_PORTAL, or attach to
       that portal for this command if no session holds it;
    2. inside a workspace, the session or portal that has the workspace's project open - or
       stop, rather than change some other project;
    3. otherwise use the session started by 'tia session start', if exactly one is running;
    4. otherwise attach to the single TIA Portal already running on this machine;
    5. otherwise stop and tell you, unless --start was given.
  Attaching to a portal you opened yourself is the normal case and needs no setup. A portal that
  tia starts belongs to tia, so it only outlives a single command when a session is holding it.

SESSION
  session start            Start a background session and keep it. Joins a running portal if there
                           is one, otherwise launches its own.
      --new                Launch a new portal even if one is already running
      --headless           Launch it without the TIA Portal window (only with a new portal)
      --attach <pid>       Join this portal specifically - and start a second session next to
                           one that is already running
      --project <path>     Open a project as part of starting up
      --idle <minutes>     Stop by itself after this long with nothing to do
  session list             Every running session and the portal it holds
  session status           What is running, what it is attached to, what is open
  session stop             End the session. A portal tia started closes; one you started stays.
      --all                End every session

  There is one session per TIA Portal. With several running, a command names its portal with
  --attach <pid> or TIA_PORTAL=<pid>, and is refused rather than guessed at without one.
  shell                    Interactive prompt holding one session open
  portals                  List the TIA Portal instances running on this machine

WORKSPACE
  init [<folder>]          Make a workspace: .tia/ (config, project/, archive/) and source/
                                 [--name <project>] [--station <station>]
                                 [--from <project.apNN | archive.zapNN>] copies one in
  open                     Open the workspace's project in TIA Portal, started like a double-click:
                           opens .tia/project, else retrieves .tia/archive. Nothing stays running
                           but TIA itself. A session with no project open takes it instead.
                                 [--headless] in a session, which then holds the portal
                                 [--upgrade] [--timeout <seconds>, default 900]
  workspace                Where the workspace's project, archive and source are, and who has it open
  export [<station>]       The station's program into source/<station>/, replacing what was there:
                           Program blocks/ (SCL, STL, DB as text; LAD, FBD, GRAPH as XML),
                           PLC data types/ (.udt) and PLC tags/ (one .csv per table)
  sync [<station>]         Bring what git says changed under source/<station>/ into the project,
                           take out what was deleted, then compile. Exits non-zero on any failure.
                                 [--file <path>] that file only   [--no-compile]

  Inside a workspace - any folder below one holding .tia/config - commands go to the TIA Portal that
  has its project open, and refuse when none has, or when --attach or TIA_PORTAL names another.
  With 'station:' in .tia/config, commands that take a <station> may leave it out.
  --no-workspace ignores the workspace for one command.

PROJECT
  project open <path>      Open a project (minutes, not seconds)   [--upgrade]
  project new <dir> <name> Create a project
  project info             Describe the open project
  project save             Save it
  project close            Close it                                [--save]
  project archive          Pack it into one .zapNN file            [<path>] [--save] [--overwrite]
                           No path puts it beside the project folder; a folder gets the project's
                           name in it. TIA only archives a saved project, hence --save.
                                 [--minimize] leaves out the data TIA can rebuild
  project retrieve <archive> <dir>
                           Unpack an archive into <dir> and open it        [--upgrade]

HARDWARE
  devices                  List stations, their CPUs and addresses
  catalog <filter>         Search the hardware catalog for order numbers   [--limit <n>]
  device attrs <station>   List the CPU's Openness attributes             [--filter <text>]
  device set <station> <attribute> <value>
                           Set one attribute
  device protection <station>
                           Access level and passwords, which a new CPU needs before it compiles
                                 [--level <FullAccess|ReadAccess|HMIAccess|NoAccess>]
                                 [--password <full access>] [--secret <confidential config data>]
  device add <type> <name> Add a station from a type identifier    [--device-name <n>]
  hw export <file>         Export the hardware configuration as CAx data (AutomationML)
                                 [--device <station>] one station instead of the whole project
  hw import <file>         Import CAx data into the open project
                                 [--existing retain|overwrite|park] for a device already there:
                                 keep it (default), replace it, or move it to the parking lot
  device ip <station>      Set the Ethernet address        [--address <ip>] [--mask <netmask>]
                                                           [--subnet <name>] names the TIA subnet
                                                           [--router <ip>] [--no-router]

PROGRAM BLOCKS
  blocks <station>         List program blocks    [--filter <text>] [--type OB,FB,FC,DB]
                                                  [--tree] shows the folders  [--system]
  block show <station> <block>
                           Header, interface and network titles of one block
  block source <station> <block>
                           Print the block as SCL/STL/DB text   [--out <path>] [--deps]
                           LAD, FBD and GRAPH blocks have no text form - use 'block export'.
  block export <station> <block>
                           Export a block as Openness XML  [--out <path>] [--print]
  block import <station> <file>
                           Import blocks from Openness XML [--folder <path>] [--overwrite]
  blocks export [<station>]
                           Every block, one file each, in the program's folders
                                 [--source] SCL/STL/DB blocks as text, the rest as XML
                                 [--out <dir>]  [--filter <text>] [--type OB,FB,FC,DB]
                                 Overwrites files there but deletes none.
  blocks import [<station>]
                           Every block file in a folder - .scl, .awl, .db, .xml - its folders
                           becoming the program's. Deletes nothing, compiles nothing.
                                 [--from <dir>]

  The plural exports and imports default to <station>/<the part's folder in TIA's tree> -
  Program blocks, PLC data types, PLC tags - under source/ in a workspace and under the current
  folder elsewhere: the layout 'tia export' writes.
  block rename <station> <block> <new name>
                           Rename a block. Callers are not rewritten; compile to find them.
  block delete <station> <block>
                           Delete a block                  [--force] skips the question
  folder add <station> <path>
                           Create a folder, and any missing folder above it
  folder delete <station> <path>
                           Delete a folder and everything in it            [--force]

  A block is named by a bare name (""Motor"") or by its folder path (""Pumps/Motor""). A bare
  name that two folders both hold is refused rather than guessed at.

EXTERNAL SOURCES
  sources <station>        List external sources
  source add <station> <name>
                           Add SCL and compile it into blocks
                                          [--file <path> | --code <text> | stdin]
                                          [--no-generate] keeps the source without compiling
                                          [--folder <path>] puts the blocks in a folder
  source generate <station> <name>
                           Compile a source already in the project    [--folder <path>]
  source delete <station> <name>
                           Delete a source. Blocks it made stay.      [--force]

TAGS (as CSV: Name, Data Type, Logical Address, Comment, Hmi Visible, Hmi Accessible, Hmi Writeable;
      or with --xml as TIA's own XML, which also holds the user constants and every comment language)
  tables <station>         List tag tables
  tags <station>           Print tags as CSV; never JSON   [--table <name>] gives that table's
                           own file, which 'table import' takes back
  table add <station> <name>
                           Create a tag table
  table export <station> <table>
                           One tag table as CSV            [--out <file>, default ./<table>.csv]
                                 [--xml] as XML, default ./<table>.xml; an --out ending .xml is too
  table import <station> <file.csv>
                           Make the table hold exactly the file's tags: created, changed, and
                           deleted when not listed. User constants are left alone.
                                 [--name <table>, default the file's name] [--folder <path>]
                           A .xml replaces the table it names: tags, constants and comments.
                                 [--folder <path>] for a new table; one TIA has stays where it is
  tables export [<station>]
                           Every tag table, in its folders [--out <dir>] [--xml]
  tables import [<station>]
                           Every tag table .csv and .xml in a folder, as 'table import' each
                                 [--from <dir>]
  tag add <station> <name>
                           Create a tag       [--table <t>] [--type <dt>] [--address <addr>]

DATA TYPES (as .udt text)
  types <station>          List PLC data types
  type export <station> <type>
                           One data type as .udt           [--out <file>, default ./<type>.udt]
  type import <station> <file.udt>
                           Generate the data type(s) in the file   [--folder <path>]
  types export [<station>]
                           Every data type, in its folders [--out <dir>]
  types import [<station>]
                           Every .udt in a folder, its folders becoming the types' folders
                                 [--from <dir>]

  compile <station>        Compile the PLC software. Exits non-zero when it reports errors.

PLC (online)
  download <station>       Download to the PLC     [--address <ip>] [--via <mode>, default PN/IE]
                                                   [--interface <name>] [--slot <n>] [--hardware]
                                                   [--changes] [--stopped] [--force] [--target <name>]
                                                   [--secret <master secret>] [--plc-password <pw>]
                           Routine prompts are answered like the dialog's defaults; destructive
                           ones (reset, reinitialize, up/downgrade) abort unless --force.
                           Password-protected targets are refused - use the TIA Portal UI.
  upload <ip>              Upload the station at <ip> into the project as a new station
                                                   [--via <mode>] [--interface <name>] [--slot <n>]
  sim create <station>     Create a PLCSIM instance for the device and power it on
                                                   [--cpu <type>] [--address <ip>] [--mask <netmask>]
                                                   [--timeout <seconds>, default 60]
                           S7-1500 and friends only - PLCSIM's API has no S7-1200. Download to it
                           afterwards with 'tia download <station>'.
  sim start <station>      Download to S7-PLCSIM; from V18 start the instance first
                                                   [--address <ip>] [--advanced] [--stopped]
                           Needs S7-PLCSIM installed; PLCSIM Advanced instances take --advanced.

ON SCREEN (needs a portal with a window, so not --headless)
  show hw                  Open the hardware editor    [--view device|network|topology]
  show device <station>    Open one station in it      [--view device|network|topology]
  show block <station> <block>
                           Open a block's editor
  show table <station> <table>
                           Open a tag table

GLOBAL OPTIONS
  --json                   Print raw JSON instead of tables (not for 'tags', which is CSV)
  --attach <pid>           Work on this TIA Portal: through the session holding it, or by
                           attaching for this command when no session does
                           TIA_PORTAL=<pid> in the environment does the same for every command
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
  tia blocks export PLC_1 --source             # every block, the text ones as text
  tia export && git diff                       # in a workspace: the program as files, and what moved
  tia sync && git commit -am ""Motor fix""     # bring edits back, compile, commit only if clean
  type block.scl | tia source add PLC_1 Motor
  tia compile PLC_1 && tia project save
  tia show hw --view network                   # put it on screen instead of clicking there

EXIT CODES
  0 ok   1 failed   2 bad arguments   3 no session   4 no project   5 not found
  6 ambiguous   7 access denied   8 licence missing   9 portal unusable");
        }
    }
}
