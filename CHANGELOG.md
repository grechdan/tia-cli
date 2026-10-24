# Changelog

Notable changes to tia-cli. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Added

**Tag tables as TIA's own XML.** The CSV carries tags in one language; the Openness XML TIA Portal
exports carries the user constants and the comments in every language too, and is what
repositories that keep tag tables next to their SCL already hold.

- `table export --xml` and `tables export --xml` write tag tables as XML; an `--out` ending `.xml`
  does the same for one table. The export is made with defaults (`ExportSetting` `WithDefaults`),
  so attributes at their default value, such as the HMI flags, are in the file too. Its creation
  stamp is dropped, as it is for blocks, so an unchanged table exports to the same bytes.
- `table import`, `tables import` and `tia sync` take `.xml` tag tables as well as `.csv`. The XML
  replaces the table it names - tags, constants and comments - in the folder TIA has it in, or in
  `--folder` (or the file's folder) when the table is new. `--name` does not apply: the name is in
  the file.

## 0.4.2 — 2026-10-21

### Added

**Plural for all of them: `blocks`, `types` and `tables` export and import.** One is singular,
all is plural - `tia block export PLC_1 Motor`, `tia blocks export PLC_1` - so taking everything
no longer needs `--all` hung on a command named for one thing. `tia blocks`, `tia types` and
`tia tables` on their own still list.

- `blocks export`, `types export` and `tables export` write every block, data type or tag table,
  into `<station>/Program blocks`, `PLC data types` or `PLC tags` by default - under `source/` in a
  workspace - or `--out <dir>`. `blocks export --source` writes the text ones as text.
- `blocks import`, `types import` and `tables import` are new: every file of that part in a folder
  (`--from <dir>`, same default) into the project, its folders becoming TIA's. Nothing is deleted
  and nothing compiled - the whole-workspace version with deletions and a compile is `tia sync`.
- `--all` (`block export --all`, `block source --all`, `table export --all`, `type export --all`)
  still works, and is no longer in the help.

### Fixed

- `tia sync` brings code blocks in before data blocks, so an instance DB no longer fails because
  its FB had not been generated yet.

## 0.4.1 — 2026-10-21

### Added

**Workspaces: a folder that knows its project.** A `.tia` folder marks a folder holding one TIA
project, the way `.git` marks a repository: `.tia/config` (name, TIA version, default station -
committed), `.tia/project/` (the working project) and `.tia/archive/` (the `.zapNN`, for blob
storage), with `.tia/.gitignore` keeping those two out of git. The program as files goes in
`source/` beside it. tia finds the workspace by walking up from the current folder, like git.

- `tia init [<folder>]` makes one, and with `--from` copies an existing project or archive in - the
  original is left where it was. `--station` sets the default station. The root `.gitignore` is
  not touched.
- `tia open` starts TIA Portal with the project the way a double-click does, waits until it is
  open, and exits. Nothing stays running but TIA itself, which is yours: closing it is closing the
  window. With only an archive, it retrieves it into `.tia/project/` over one short attach - TIA
  keeps a project open after the connection that opened it has gone. `--headless` keeps a session
  instead, since a headless portal closes with whatever started it, and a session already running
  with no project open is used rather than starting another. The TIA Portal is started through
  `cmd /c start` by ShellExecute, so it is nobody's child and holds none of tia's handles: stopping
  the command's process tree leaves it running, and `tia open | ...` returns as soon as tia does.
  It warns when the project folder's path is long - TIA Portal V20 refused one of 151 characters.
- Every later command finds the TIA Portal with the workspace's project open and attaches for as
  long as it runs. Measured on V20: 0.33 s a command, against 0.19 s through a session.
- `tia workspace` shows where everything is and who has the project open.
- Inside a workspace, every command goes to the session or portal that has *its* project open,
  matched by the project file, and stops with exit 3 when none has - instead of acting on whatever
  other project happens to be open. An `--attach` or `TIA_PORTAL` naming a portal without the
  project is refused, so a `TIA_PORTAL` left over in the shell cannot send commands to another
  project. `--no-workspace` ignores the workspace for one command.
- With `station:` in `.tia/config`, commands that take a station may leave it out: `tia blocks`,
  `tia block export Motor`, `tia compile`.

**`tia export` and `tia sync`: the program as files, and back.** Inside a workspace, `tia export`
writes the station's program into `source/<station>/`, in folders named like TIA's tree:
`Program blocks/` (SCL, STL and DB blocks as text, LAD/FBD/GRAPH as XML), `PLC data types/` (one
`.udt` per type) and `PLC tags/` (one `.csv` per tag table). It replaces what those three folders
held, so a block deleted in TIA shows in git as a deleted file. `tia sync` asks git what changed
there and brings it back: tag tables, then data types, then blocks, then deletions, then a compile
- and exits non-zero when any file or the compile failed, so `tia sync && git commit` commits only
what compiled. These replace the bash helpers GIT-WORKFLOW.md used to need.

- Tag tables are CSV with the columns of TIA's own Excel export: `Name`, `Data Type`,
  `Logical Address`, `Comment`, `Hmi Visible`, `Hmi Accessible`, `Hmi Writeable`. Sync makes the
  table hold exactly those tags - created, changed, or deleted when gone from the file. User
  constants are not in the CSV, and are left alone. Comments are in the project's editing language.
- Text goes into TIA through an external source, which sync removes again once its block or type
  exists, rather than leaving one per file under "External source files".
- `--file <path>` brings in one file, git or not - for after `git checkout -- <file>`, when git
  sees nothing changed. `--no-compile` skips the compile.

**Tag tables and data types on their own.** The parts `tia export` writes can each be written and
read back by themselves, workspace or not:

- `tia table export <station> <table>` writes one tag table's CSV, `--all` every table;
  `tia table import <station> <file.csv>` makes a table hold exactly the file's tags.
- `tia types <station>` lists PLC data types; `tia type export <station> <type>` writes one as
  `.udt`, `--all` every one; `tia type import <station> <file.udt>` generates it.
- Every `--all` export - blocks too - now defaults to `<station>/<TIA's folder name>`: under
  `source/` in a workspace, under the current folder elsewhere. The three parts land side by side
  the way `tia export` lays them out.
- `tia source add` of a `.udt` counts the data types it made, instead of reporting "produced no
  blocks".

**`tia hw export` and `tia hw import`: hardware as CAx data.** The program can live in git as text;
the hardware could not leave the project except inside an archive. CAx data exchange - AutomationML,
the format behind TIA Portal's own "Export CAx data" - is the native way to take a hardware
configuration out of one project and put it into another.

- `hw export <file>` writes the whole project's hardware, `--device <station>` one station.
- `hw import <file>` reads it into the open project. `--existing` decides what happens to a device
  already there: `retain` keeps it (the default, since it cannot destroy anything), `overwrite`
  replaces it, `park` moves it to the parking lot.
- Both report CAx's own messages and exit non-zero when it reports errors.
- `tools/caxtest.sh` sends a real project's hardware round trip - export, import into an empty
  project, export again - and reports what survived: stations, every CPU attribute, a hardware
  compile, and the two AML files compared. It is how to find out what CAx carries before relying
  on it.

**`tia block export <station> --all`: the whole program in one command.** Exporting a program meant
a script calling `block export` once per block. `--all` walks the program in the session and writes
every block, one file each, into a copy of the program's folders under `--out`. The default is
`<station>/Program blocks/`, under `source/` inside a workspace and under the current folder
elsewhere, so the station's folder mirrors TIA's tree.

- `--source` writes SCL, STL and DB blocks as text and only LAD, FBD and GRAPH as XML - the layout
  a git repository wants. `tia block source <station> --all` is the same thing.
- `--filter` and `--type` narrow it, as in `tia blocks`.
- Know-how protected blocks are skipped and named; a block that will not export is reported and
  the rest still go out, with exit 1 at the end. A missing licence stops it at the first block.
- Files already in the directory are overwritten, never deleted.

### Changed

- **`tia tags` prints CSV, and refuses `--json`.** It was a table, or JSON with `--json`. The CSV
  is the tag table file format: with `--table <name>` it is that table's file, which
  `tia table import` takes back; without, it lists every table with a leading `Table` column.
- **`--timeout` is in seconds everywhere.** `sim create --timeout` was milliseconds; it is seconds
  now, default 60, like `open --timeout` (default 900).
- **The help calls the first argument `<station>` throughout**, as the workspace's `station:`
  does; it was `<device>` in most places and `<station>` in a few. Only the name changed.

### Fixed

**`session list` and `session status` show a project opened through tia.** The session's cached
state only refreshed on some commands, so a project opened with `project open` or `project new`
showed as none until something else happened.

## 0.4.0 — 2026-10-18

### Added

**Several sessions, one per TIA Portal, picked by the portal's pid.** There was one background
session per Windows user, so working on two projects in two portals meant stopping one session to
start the other. Each session now has a pipe and a state file of its own.

- `tia session start --attach <pid>` starts a session for that portal next to any already running,
  and `--new` one with a portal of its own. A plain `session start` with a session already running
  says so instead of starting a second, so two sessions never share a portal.
- `--attach <pid>` on any command now goes to the session holding that portal, and attaches for
  that one command when none does. Before, a running session made it ignore `--attach`.
- `TIA_PORTAL=<pid>` in the environment does the same for every command in a shell.
- With several sessions running and no portal named, commands stop with exit 6 and list the portals,
  rather than guessing. With exactly one, nothing changes.
- `tia session list` shows every session, its portal, project and who started it.
  `tia session stop --attach <pid>` stops one, `--all` stops every one.

### Fixed

**`tia portals` no longer closes the TIA Portals it lists.** Listing disposed the handle Openness
returns for each portal, and disposing one closes that portal - window, project, unsaved work and
all. So `tia portals` closed every portal you had open, and `tia session start` with one open
closed it while looking for it, then failed with "No attachable TIA Portal process with id …". The
field test never saw it: it only ever lists portals it started itself. Handles are now let go
without being disposed or finalised, a session that stops leaves an attached portal running, and
`session start` joins the portal through the handle it found instead of looking it up again.

## 0.3.0 — 2026-10-18

### Added

**`GIT-WORKFLOW.md`: the program in git, compiled in TIA.** A Git Bash runbook, shipped in the zip,
for keeping an existing project's blocks and tag tables in a git repository: export from the open
portal, edit in VS Code, import only what git says changed, compile, and commit only when that is
clean. It replaces the VCI add-in for the program side; hardware stays in the project.

**`project archive` and `project retrieve`.** A project is a folder of several hundred files; the
thing people actually pass around and back up is the single `.zapNN` archive, and making or opening
one meant going through the TIA Portal menus.

- `project archive [<path>]` packs the open project. With no path the archive lands beside the
  project folder under the project's name; a folder gets that name inside it; anything else is the
  file, with the version's extension (`.zap20` for an `.ap20`) added when it is missing. TIA only
  archives a saved project, so unsaved changes are refused by name unless `--save` is given.
  `--minimize` leaves out the data TIA can rebuild. An existing archive is refused unless
  `--overwrite`, and then moved aside until the new one exists rather than deleted first.
- `project retrieve <archive> <dir>` unpacks an archive into a directory and leaves the project
  open, `--upgrade` for one from an older TIA version.
- `project open` on an archive now says to use `project retrieve` instead of failing with a bare
  Openness error.

### Fixed

**The same block exported twice is the same file.** TIA stamps every XML export with the moment it
was made, so a repository of exports showed every LAD and FBD block as changed on every commit.
`block export` now drops that `<Created>` line; import never read it.

**Compile errors say which block they are in.** TIA names the block on a parent message and only
the position on the error itself, so `tia compile` printed `2` where it meant line 2 of `Twice`. The
`where` column now shows `Program blocks/Twice/2`, and `--json` carries it as `location`.

**`session stop; session start` on one line starts a session.** A session told to stop keeps
answering its pipe while it closes TIA Portal, and a `session start` arriving in those seconds found
it, said a session was already running, exited 0 — and left you with nothing a moment later. That
was the documented way to recover from exit 9. `session stop` now returns once the session has
actually gone, and `session start` waits for one that is on its way out instead of joining it.

### Changed

**`tools/gittest.ps1` tests the program as a git repository.** It exports a throwaway project to
text files, edits them the way an editor would, imports only what git says changed, compiles, and
commits only when that is clean — then checks that exports are stable, that TIA gives back what was
edited, that broken SCL is caught before commit, and that a project rebuilt from the repository alone
exports to the same files. Like the field test it ships separately from the zip.

**The field test covers every verb.** `tools/fieldtest.ps1` gained the cases it was missing — device
attributes and protection, tag and table listings, stdin and `--no-generate` sources, `--print` and
`--overwrite`, the shell, a one-shot command with its own portal, attaching by pid, archive and
retrieve, and a second session started with `--project` — and a unit test now fails when a verb is
added without one. When `device add` fails, the cases that need its station are skipped by name
rather than failing fifty times for the same reason.

**`install.ps1` approves what it installs.** It used to stay inside your profile and leave the
machine-wide part to `repair.ps1`, which nobody remembers at the moment it matters — so every fresh
install, and every upgrade, left a `tia.exe` TIA Portal had never approved, and the first background
session timed out against an approval window nobody could see. Install now finishes by running
`repair.ps1` elevated: one UAC prompt from a normal shell, none from an admin one. That writes the
Openness whitelist entry for the exact bytes just installed, adds you to the Siemens TIA Openness
group if you are missing, and clears a machine-wide `RUNASADMIN` flag. `-NoElevate` restores the old
profile-only behaviour; declining UAC is reported, not fatal.

**`uninstall.ps1 -All` clears a machine that has collected copies.** It finds every install from
your PATH, the machine PATH, the Openness whitelist, any running `tia.exe` and the default location,
and removes the files, the PATH entries and the registry entries together, asking about each folder
first. Whitelist entries are only cleared wholesale when every install was; keep one, keep its
approval.

**A folder is no longer deleted unless everything in it came from the zip.** Uninstall used to empty
its install folder and then delete the folder — fine for `%LOCALAPPDATA%\Programs\tia-cli`, and a
disaster for someone who installed into `C:\`, a profile root or a Desktop, all of which are
perfectly legal `-InstallDir` values. Those now keep the folder and lose only the files tia put
there, by name, with any leftover DLLs reported rather than assumed.

**The access_denied hint names the fix that works without a dialog.** It used to say to answer TIA
Portal's confirmation window — which is exactly what a background session cannot show, so the advice
was unusable at the moment you needed it. It now says that, and points at `repair.ps1`. The
pending-approval warning does the same.

`repair.ps1` asks for elevation itself instead of refusing to run, takes `-User` and `-AddToGroup`,
approves the `tia.exe` next to it rather than assuming the default install folder, reads a group
holding an orphaned SID without mistaking it for a missing group, and exits non-zero on failure.

## 0.2.0 — 2026-09-16

### Added

**The program-blocks surface, which was two commands and is now the whole lifecycle.** `blocks` and
`block export` could tell you a block existed and hand you its XML; nothing else about the program
was reachable without opening TIA Portal.

- `blocks --tree` draws the folders rather than flattening them, and `--type OB,FB,FC,DB` filters by
  kind. `DB` covers both global and instance data blocks, since that is what people mean by it. The
  tree's spine is ASCII: the console inherits an OEM code page unless somebody has changed it, and
  box-drawing characters arrive there as mojibake.
- `block show` prints a block's header, its full interface — nested struct members and all — and its
  network titles. Openness exposes none of that directly: only `DataBlock` has an `Interface`
  property and it yields nothing but member names, so the block is exported to a temporary file and
  the SimaticML read back. Everything is matched by local element name, because those namespaces
  carry a schema version that moves with each TIA release.
- `block source` writes a block out as SCL, STL or a DB declaration — the readable form, and the one
  that round-trips back in through `source add`. `--deps` brings the types it depends on with it.
  LAD, FBD and GRAPH blocks have no textual form and say so rather than failing obscurely.
- `block import` reads Openness XML back into the project, into `--folder` if you name one, over the
  top of what is there with `--overwrite`.
- `block rename` and `block delete`, and `folder add` / `folder delete` for the tree itself.
  Deleting asks first unless `--force` is given, and refuses outright when stdin is redirected and
  nobody is there to answer.
- `sources`, `source generate` and `source delete` complete the external-source side. `source add`
  is the new name for `scl import`, which still works, and gained `--folder` to put the blocks it
  generates somewhere other than the root.

### Changed

- Block lookup was rewritten. A name that two folders both hold is now refused as ambiguous instead
  of resolving to whichever the old path walk happened to reach, which could silently be the wrong
  block. A folder-qualified name that is not in that folder is likewise a failure — it says where
  the block actually is rather than acting on one somewhere else — while a bare name still finds the
  block wherever it lives. Folder paths may be given with or without the leading `Program blocks`.
- The `group` column in `tia blocks` is called `folder`, matching the new verbs.
- The hints on block failures no longer overwrite a cause that is already understood. Openness
  reports a missing licence as an ordinary `EngineeringException`, so `block source` on an
  unlicensed machine used to answer "only textual blocks have a source form" about a perfectly
  textual block; the licence, security and dead-portal cases now pass through to the error that
  names them, and keep their exit codes.

## 0.1.0 — 2026-09-01

First release. Drives Siemens TIA Portal through the Openness API from a terminal or a script.

### Added

**Sessions.** `tia <command>` joins the TIA Portal already open on your screen, with no setup. Where
that is too slow — opening a project takes minutes and a CLI process lives for a second —
`tia session start` launches a portal and holds it in a background daemon so the next fifty commands
reuse it. A portal that `tia` started is closed with the session; one that was already running is
left alone.

- `portals`, `session start|status|stop`, `shell`

**Projects and hardware.** Create or open a project, add stations from the hardware catalog, set
addresses, and save.

- `project new|open|info|save|close`, `devices`, `catalog`, `device add`, `device ip`
- `device attrs` lists a CPU's Openness attributes and `device set` changes one. Hardware settings
  that have no command of their own are reachable that way, under names that differ between TIA versions.
- `sim create` makes a PLCSIM instance for a device and powers it on, through PLCSIM's own Simulation
  Runtime API - loaded by path and called by reflection, since it ships inside the PLCSIM
  installation. Downloading is then the ordinary `tia download`. S7-1500 and upwards only; PLCSIM's
  API has no S7-1200 type, so those instances are still made by hand. An instance created through the
  API counts as **PLCSIM Advanced** and asks for that licence key - PLCSIM offers a trial - while one
  created by hand in the PLCSIM window does not.
- `download` and `sim start` take `--secret` for the master-secret prompt and `--plc-password` for
  the access-level ones. A CPU that has a master secret - which current firmware needs before it
  compiles - asks for it on every download, so without this a project built entirely from the CLI
  could never be downloaded from it.
- `device protection` sets the access level, the full-access password and the password for
  confidential configuration data. A current CPU refuses to compile until the last two exist, so
  without it every project built from scratch needs a trip through the TIA UI. Passwords given on a
  command line end up in shell history: prefer a prepared project where that matters.

**Program blocks and tags.** Import SCL, compile, list and export blocks, and manage tag tables.

- `blocks`, `block export`, `scl import`, `tables`, `tags`, `table add`, `tag add`, `compile`

**Transfers and editors.**

- `download`, `upload`, `sim start`
- `show hw|device|block|table`, which need a session started with a user interface

**Scripting.** `--json` prints the raw result of any command, and failures map to specific exit
codes: `1` failed, `2` bad arguments, `3` no session, `4` no project, `5` not found, `6` ambiguous,
`7` access denied, `8` licence missing, `9` portal unusable.

**Install and uninstall.** `install.ps1` checks the machine over first — .NET Framework 4.8, an
Openness installation, your membership of the Siemens TIA Openness group — then installs to
`%LOCALAPPDATA%\Programs\tia-cli` and puts it on your PATH. No administrator rights, nothing written
outside your profile. `uninstall.ps1` stops the session before removing anything, so a background
session cannot be orphaned holding a TIA Portal open.

It also clears the two things that stop a downloaded copy from running on a fresh machine, both of
which surface as the daemon refusing to start with "the operation was canceled by the user": the
mark of the web inherited from the downloaded zip, and a `RUNASADMIN` compatibility flag on
`tia.exe`. Where the first Openness approval has not been granted yet, it says how to answer that
dialog — in the foreground, against a portal on screen — rather than leaving a background session to
time out against a window nobody can see.

**repair.ps1.** Diagnoses a machine where `tia` still will not run: environment, mark of the web,
compatibility flags, Openness group membership and token, and the whitelist entry, which it can
write directly from an elevated prompt. `-DryRun` reports without changing anything.

**Build tooling.** `tools/package.ps1` builds the release zip: it runs the tests, publishes fresh
rather than shipping whatever is in `dist\`, and writes a SHA-256 alongside. `tools/build.ps1` is the
development inner loop only and does not touch PATH.

**Tests.** 58 unit tests over the parts that run without Openness — argument parsing, the wire
protocol, error translation and the documented exit-code table, and the daemon state file. For the
rest, `tools/fieldtest.ps1` runs the hardware-dependent commands on a machine that has the hardware
and writes a report to bring back.

### Verified

Exercised against TIA Portal V20 on Windows 10 x64: a full cycle of create project, add a CPU 1214C,
set its address, create tag tables and tags, import SCL, compile clean, list and export blocks, save,
reopen from disk, and confirm everything survived. Install, reinstall over an existing copy, and
uninstall were each run from the built zip.

### Known limitations

**`sim create` makes an instance TIA will not download to.** Verified on V20: the Runtime API creates
an *Advanced* instance (PLCSIM shows it as `EXTERNAL - ADVANCED`), and a download to it fails with
"Connect to module failed" even with TCP/IP communication mode, a routable address, port 102 open and
the software target answered as PLCSIM Advanced. An instance started from TIA's own "Start simulation"
is an *internal softbus* one, and `tia download --interface PLCSIM` puts the program on it first time.
There is no public API for creating the internal kind, and the Advanced kind needs the PLCSIM Advanced
licence. Until that is understood, the working flow is: start the simulation in TIA, then download.

**`sim start` downloads to a simulation but cannot start one.** From TIA V18 the PLCSIM connection
mode is gone; a simulated PLC is a PN/IE target behind the PLCSIM adapter, and TIA will not start an
instance for a download. Power the instance on first, then run `sim start`.

To fix: PLCSIM ships a Runtime API
(`PLCSIM_V<n>\resources\bin\wwwroot\assets\lib\runtime\Siemens.Simatic.Simulation.Runtime.Api.x64.dll`)
that could create and start an instance before the download. The calls, read off the V20 assembly:

```
SimulationRuntimeManager.RegisterInstance(ECPUType, string name) -> IInstance
IInstance.SetIPSuite(uint interfaceIndex, SIPSuite4 suite, bool persistent)
IInstance.PowerOn(uint timeoutMs) -> ERuntimeErrorCode
IInstance.Run(uint timeoutMs)
IInstance.UnregisterInstance()
```

`SIPSuite4` holds `IPAddress`, `SubnetMask`, `DefaultGateway`, each an `SIP`; how an `SIP` is built is
the one piece still unchecked. Load the assembly by path and call it by reflection - the dev machine
has no PLCSIM, so a compile-time reference would break the build. It covers **S7-1500, ET200SP/PRO, software and SINUMERIK CPUs
only**: `ECPUType` has no S7-1200 entry, and `RegisterCustomInstance` rejects the 1214C article number
(`WrongArgument`), though PLCSIM's own UI simulates S7-1200 from firmware 4.0. So an S7-1200
simulation will still have to be started by hand until Siemens exposes it.

### Notes for upgrading

TIA Portal's Openness whitelist records the approved executable's path **and** a hash of its bytes,
so every new version asks for the "Openness access" confirmation once more. Answer that window the
first time you run a new build; until you do, the command waits and appears to hang.

A running session only knows the wire methods of the binary it was started from, so after installing
a version that adds commands, run `tia session stop` and start again — otherwise the old daemon
answers the new verbs with "Unknown method".
