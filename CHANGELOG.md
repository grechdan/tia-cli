# Changelog

Notable changes to tia-cli. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

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

### Notes for upgrading

TIA Portal's Openness whitelist records the approved executable's path **and** a hash of its bytes,
so every new version asks for the "Openness access" confirmation once more. Answer that window the
first time you run a new build; until you do, the command waits and appears to hang.

A running session only knows the wire methods of the binary it was started from, so after installing
a version that adds commands, run `tia session stop` and start again — otherwise the old daemon
answers the new verbs with "Unknown method".
