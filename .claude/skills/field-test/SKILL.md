---
name: field-test
description: Run or interpret the tia-cli field test on a machine with real TIA Portal hardware — a licensed portal, S7-PLCSIM, or a physical PLC. Use when asked to test tia-cli against real hardware, to run fieldtest.ps1, to triage a field test report, or when a change touches download, upload, sim, or the show verbs and needs verifying somewhere other than the development machine.
---

# Field testing tia-cli

Most of tia-cli can be tested anywhere. A handful of commands cannot: they need a licensed TIA
Portal, S7-PLCSIM, a portal window on screen, or a real PLC. Those are tested on a separate machine
that has the hardware but no development tools, and the results come back as a report.

This skill covers both ends: running the test on that machine, and reading the report afterwards.

## The rule that matters

**Never run a PLC write test unless the person asking has said, in this conversation, that the
target PLC is safe to stop.** `tia download` stops and reloads the controller. On a machine that is
connected to anything real, that is a physical event with physical consequences — not a test step.

`fieldtest.ps1` enforces this: PLC writes need `-AllowPlcWrite`, `-PlcAddress`, and a typed
confirmation. Do not pass `-Yes` to skip that confirmation on someone's behalf, and do not add
`-AllowPlcWrite` because it would make the report more complete. An incomplete report is fine. A
stopped production PLC is not.

Everything else in the harness is safe: it builds a throwaway project in a temp folder, and deletes
it afterwards.

## Running it

The machine needs two things, from two places:

- **tia-cli itself**, from the release zip. End users get exactly this, so testing it is the point.
- **`tools/fieldtest.ps1`**, from the repository. It is maintainer tooling and deliberately does not
  ship in the zip. Take it from the **same tag** as the release under test: the harness hardcodes
  command names, arguments and exit codes, so a harness from a different commit tests a CLI that
  does not exist. Git is not needed — a clone, GitHub's Download ZIP, or the raw file all work. If
  the repository is private, the raw URL needs credentials, so copy the file over instead.

Then, in this order — each step depends on the one before:

```powershell
# 1. install: clears the mark of the web, sets up PATH
powershell -ExecutionPolicy Bypass -File install.ps1

# 2. answer the Openness dialog once, in the foreground, against a portal on screen
tia devices --no-daemon

# 3. now the harness
powershell -ExecutionPolicy Bypass -File fieldtest.ps1
```

Step 2 is the one people skip. A session runs its portal in the background, so if the approval has
never been granted the harness stalls and then fails with exit 7 on `session start` — and every
group after it is recorded as untested.

Start with **no `tia session` running** — the harness refuses otherwise, since sharing a session
would mean ending it when done. Your own TIA Portals can stay open: the harness always starts a
portal of its own with `--new`, puts its throwaway project there, and closes only that one.

**Known issue in 0.1.0:** `tia portals` run while a session is up disposes that session's portal —
every later command fails with exit 9. Until it is fixed, do not run `tia portals` by hand during a
session; `tia session status` is the safe way to see what is running.

The harness can also run against an unpacked zip without installing, with `-ExePath` pointing at
its `tia.exe`. It clears the mark of the web itself in that case, but a later install is then a
different file in a different place, so TIA Portal asks for the Openness dialog again. Installing
first is the cheaper path.

Useful variations:

| Situation | Command |
| --- | --- |
| No portal window wanted | add `-SkipUi` |
| A PLC to read from | add `-PlcAddress 192.168.0.1` |
| A PLC that is safe to stop | add `-PlcAddress ... -AllowPlcWrite` |
| tia installed somewhere unusual | add `-ExePath C:\path\to\tia.exe` |
| Keep the project to look at | add `-KeepProject` |

It writes `fieldtest-<timestamp>.md` and `.json` to `Desktop\tia-cli-fieldtest`. Both files are the
deliverable — the markdown to read, the JSON to compare against a later run.

### What to expect while it runs

- The whole thing takes several minutes. Opening a project in TIA Portal is slow by nature; a case
  that takes 90 seconds is normal, not a hang.
- **The first run on a machine, or the first after an upgrade, shows an "Openness access" window and
  waits until somebody answers it.** If the run seems stuck, look for that window before assuming
  the harness is broken. Approval follows the exe's bytes, so every new build asks again.
- Without `-SkipUi`, the show verbs open editors in the portal window. That is the test — watch that
  they open, because the harness can only report that the command returned 0.

## Reading the report

Work through it in this order:

1. **The Machine section first.** A `FAIL` means nothing until you know what the machine had. No
   licence, no PLCSIM, `NOT in token` for the Openness group — each explains a whole class of
   failure, and none of them is a bug in tia-cli.
2. **Skips are information, not gaps to close.** A skipped group means the machine could not host
   it. Report it as untested; do not suggest re-running with the safety flags turned on.
3. **Then the failures**, using the transcript at the bottom, which holds the full output of every
   command. Match the exit code against the documented table: `2` bad arguments, `3` no session,
   `4` no project, `5` not found, `6` ambiguous, `7` access denied, `8` licence missing,
   `9` portal unusable.

Exit `7` on the very first command usually means the approval window went unanswered, not that
anything is wrong with the code.

## Turning a report into work

When a failure is real, reproduce it locally if you can. Much of what looks like a hardware problem
is not: argument parsing, the wire protocol, error translation and exit codes are all covered by
`dotnet test src/Tia.Cli.Tests/Tia.Cli.Tests.csproj` and run anywhere.

If it can only fail on that machine, add the case to the harness rather than to a person's memory,
so the next run checks it too. Record the outcome in `docs/smoke-test-*.md` alongside the release it
belongs to.
