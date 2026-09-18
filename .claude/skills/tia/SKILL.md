---
name: tia
description: Drive Siemens TIA Portal from the terminal with the `tia` CLI (tia-cli) — open a project, inspect stations, CPUs and program blocks, read and change block code (SCL, XML), manage folders, external sources, tags and tag tables, compile, save, change hardware and CPU settings, open editors on screen, and download to or upload from a PLC or PLCSIM. Use whenever the user asks about a TIA Portal project, a PLC program, S7 blocks (OB/FB/FC/DB), SCL code, PLC tags, or wants something done in TIA Portal from here, even if they do not name tia-cli.
---

# Using tia-cli

`tia` drives TIA Portal through the Openness API. Most work is on a project that already exists:
look around it, read the code, change some of it, compile, save, maybe download. This file covers
every command; `tia help` has the exact flags when you need one not shown here.

## Ground rules

- **Pass `--json`** whenever you will read the output. Tables are for people.
- **Slow is normal.** `project open` and `session start --project` take minutes; compile, download
  and anything that exports a block (`block show`, `block source`) take seconds to minutes. Use a
  10-minute timeout and do not retry because it is slow.
- **The user's portal and project are theirs.** Do not `project close`, `project save`, or
  `session stop` a portal you did not start unless they asked. `tia session status` says whether the
  session's portal is owned by tia or by the user.
- **Ask first, every time,** before:
  - `download`, `sim start` — they **stop and reload a PLC**. Only against a target the user has
    said, in this conversation, is safe to stop. Never add `--force` on your own.
  - `upload`, `sim create`, `device set`, `device protection`, `device ip`.
  - `block delete`, `folder delete`, `source delete`, `block import --overwrite`, `project save`.
- **Never invent passwords.** `--secret`, `--plc-password`, `--password` take values only the user
  gives.
- **Do not run `tia portals` while a session is running** — in 0.1.0 it kills the session's portal
  (exit 9 on everything after). `tia session status` is the safe way to look.
- **`tia shell` is interactive** — for people, not for you. Use a session instead.

## 1. Connect

Every command needs a portal and finds one in this order: the background session; else the
**single** TIA Portal running on the machine; else it stops with exit 3 (unless `--start`).

```bash
tia session status --json          # a session? which portal, which project, owned by whom
tia portals --json                 # only when no session: the portals running, with pids
```

Pick one of three ways to work:

| Situation | Do |
| --- | --- |
| The user has TIA Portal open with the project | Nothing — just run commands. Each attaches and detaches. |
| Several commands on a project file, nobody has it open | `tia session start --project "C:\p\Line.ap20"` (minutes). `--headless` for no window, `--idle 30` to stop itself. |
| Two portals running | `--attach <pid>` on each command, or `tia session start --attach <pid>` |

A session holds the portal and project between commands, so a 20-step task does not re-attach 20
times. Without one, a project the user opens in the UI is still picked up.

Project commands: `tia project open <path>` (`--upgrade` for an older version's project),
`tia project info --json`, `tia project save`, `tia project close` (`--save`).
`tia project new <dir> <name>` creates one; nothing reaches disk before `project save`.

End: `tia session stop` — closes a portal tia started, leaves the user's alone. Save first if the
user wants the changes.

## 2. Look around

```bash
tia devices --json                        # stations, CPUs, addresses — the <dev> argument is the station name
tia blocks <dev> --tree                   # the program tree with folders
tia blocks <dev> --type FB,FC --filter Motor --json    # --system includes system blocks
tia block show <dev> <block> --json       # header, full interface, network titles
tia block source <dev> <block>            # the code as SCL / STL / DB text; --deps adds the UDTs it uses
tia block export <dev> <block> --print    # Openness XML — the only form for LAD, FBD and GRAPH
tia sources <dev> --json                  # external source files in the project
tia tables <dev> --json                   # tag tables
tia tags <dev> --table <t> --json         # tags, all tables if --table omitted
tia device attrs <dev> --filter <text>    # the CPU's Openness attributes and values
```

Blocks are named bare (`Motor`) or by folder path (`Pumps/Motor`, with or without a leading
`Program blocks/`). A bare name that two folders hold is refused with exit 6 — use the path.
Know-how protected blocks cannot be read at all.

To answer "what does this program do": `blocks --tree`, then `block show` on the OBs to see what
they call, then `block source` on the blocks that matter.

## 3. Change the program

### Edit a textual block (SCL, STL, DB)

1. `tia block source <dev> Motor --out Motor.scl`
2. Edit the file. It must stay a complete unit — `FUNCTION_BLOCK "Motor" ... END_FUNCTION_BLOCK`.
3. `tia source add <dev> Motor --file Motor.scl` — registers it as an external source and generates
   the block from it, replacing the existing one. `--folder <path>` places new blocks.
   Also accepts `--code "<text>"` or stdin. `--no-generate` stores the source without generating;
   generate later with `tia source generate <dev> <name>`.
4. `tia compile <dev> --json` — a successful `source add` does **not** mean the code compiles.
   Non-zero exit means errors; read them, fix the file, repeat from 3.

New blocks are the same: write the SCL, `source add`, compile. A source that already exists is
replaced; `tia source delete <dev> <name>` removes it and leaves its blocks.

### Edit a graphical block (LAD, FBD, GRAPH)

There is no text form. `tia block export <dev> <block> --out B.xml`, edit the XML carefully, then
`tia block import <dev> B.xml --overwrite` (ask first). Import without `--overwrite` fails if the
block exists. XML from another project imports the same way; `--folder <path>` places it.

### Organise

```bash
tia block rename <dev> <block> <new>      # callers are NOT updated — compile to find them
tia folder add <dev> Pumps/Inlet          # creates missing parents
tia folder delete <dev> Pumps --force     # and everything in it — ask first
tia block delete <dev> <block> --force    # ask first
```

Deletes refuse with exit 2 when there is nobody to answer their prompt, which is always the case
from here — so they need `--force`, and therefore the user's yes.

### Tags

```bash
tia table add <dev> Motors
tia tag add <dev> Motor1_Run --table Motors --type Bool --address %Q0.0
```

## 4. Compile and save

`tia compile <dev> --json` compiles the PLC software; exit non-zero on errors. Then
`tia project save` — only when the user wants the change kept.

A new or reset S7-1200/1500 CPU will not compile until protection is set:
`tia device protection <dev> --password <p> --secret <s>` (`--level FullAccess|ReadAccess|HMIAccess|NoAccess`).
Ask the user for both passwords.

## 5. Hardware

```bash
tia catalog "1516-3" --json               # order numbers / type identifiers (--limit <n>)
tia device add "OrderNumber:6ES7 516-3AN02-0AB0/V2.9" PLC_2    # --device-name <n>
tia device ip <dev> --address 192.168.0.10 --mask 255.255.255.0 --subnet PN_1   # --router <ip> | --no-router
tia device set <dev> <attribute> <value>  # names come from device attrs; they differ by TIA version
```

Changing hardware needs a STEP 7 licence; without one these exit 8.

## 6. Online: PLC and PLCSIM

**Confirm with the user before every download, stating the device, address and options.**

```bash
tia download <dev>                          # software, to the device's configured address, over PN/IE
tia download <dev> --changes                # only what changed
tia download <dev> --hardware               # hardware config too
tia download <dev> --address 192.168.0.20   # a different address
tia download <dev> --interface PLCSIM       # to a running S7-PLCSIM instance
```

Other options: `--via <mode>` (default PN/IE), `--interface <PC adapter>` and `--slot <n>` when the
PC has several, `--target <device interface>` (e.g. `"1 X1"`), `--stopped` leaves the CPU in STOP.
A wrong `--interface` or `--target` fails with a list of what exists — use that list.

Routine prompts are answered like the dialog's defaults and listed in the output. Destructive ones
(reset module, reinitialise DBs, firmware change, protection change) abort by name unless `--force`
— report the name to the user and let them decide. A password prompt aborts unless the user gave
`--secret` (master secret) or `--plc-password` (access level) for this run.

`tia upload <ip>` reads the station at that address into the project as a new station
(`--via`, `--interface`, `--slot`).

**Simulation.** Start the simulation from TIA or the PLCSIM window, then
`tia download <dev> --interface PLCSIM`. The dedicated verbs are in development:
`tia sim create <dev>` makes a PLCSIM instance and powers it on (S7-1500 only, counts as PLCSIM
Advanced, and TIA will not download to it); `tia sim start <dev>` does not currently work.
Mention this rather than relying on them.

## 7. On screen

Needs a portal with a window (not `--headless`). Use it when the user wants to look for themselves.

```bash
tia show hw --view network                  # device | network | topology
tia show device <dev> --view device
tia show block <dev> <block>
tia show table <dev> <table>
```

## Exit codes

| Code | Meaning | Usual fix |
| --- | --- | --- |
| 0 | ok | |
| 1 | failed | read the message and the hint under it |
| 2 | bad arguments | `tia help`; deletes also exit 2 without `--force` |
| 3 | no portal | the user opens TIA Portal, or `tia session start`, or `--start` |
| 4 | no project | `tia project open <path>` |
| 5 | not found | list with `devices` / `blocks` / `tables` and use the exact name |
| 6 | ambiguous | two portals: `--attach <pid>`; two blocks: folder path |
| 7 | access denied | the Openness approval window is waiting — see below |
| 8 | licence missing | the machine lacks STEP 7 — tell the user; do not retry |
| 9 | portal unusable | it died or closed — `tia session stop`, start again |

**Exit 7 on the first call** almost always means TIA Portal's "Openness access" window is waiting
for a click (first run, or `tia.exe` rebuilt or moved). A headless session has nowhere to show it.
Ask the user to run `tia devices --no-daemon` once with TIA Portal open on screen and approve it.

**"Unknown method"** from a session: `tia.exe` was rebuilt after the session started —
`tia session stop` and start again.

Other global options: `--openness-version 20.0` picks an installed Openness version, `--quiet`
drops incidental notes, `--no-daemon` ignores the session.
