# The measurement rig

The scripts that produced the resource profile. Kept in the repo so the numbers are reproducible
rather than remembered, and so the next person does not rebuild them from a description.

They run on a **dedicated test box**, never on a fleet host and never on anybody's desktop. Copy
them to `C:\` on that box and drive them with `runtask.ps1`.

## Rules baked into these scripts, each of which was learned by breaking it

- **Every kill is scoped by executable PATH** (`C:\driftbench\*`), never by process name. These
  servers and their clients are frequently the same executable, and a blanket `taskkill /IM` has
  destroyed a live test server mid-session before.
- **The rig renames the executable and its data folder** (`DriftBench.exe` / `DriftBench_Data`) so
  no name-matched kill can cross between lanes in either direction.
- **Runs are Windows scheduled tasks under SYSTEM**, not SSH children. Windows OpenSSH kills the
  process tree on disconnect, and the first two ten-minute profiles died that way with the game
  still running and no CSV written. `schtasks /tr` truncates at 261 characters, so each task runs a
  small `.cmd` wrapper.
- **Discard at least 180 seconds.** The first three minutes of an instance's life cost ~49% of a
  core in boot and world generation; a 90-second warm-up produced a number five times too high.
- **Check what else is on the box first** (`topcpu.ps1`). A single forgotten process invalidated
  every number taken before it was found, and it made a hungry server look *cheaper*, not more
  expensive.
- **`analyse.py` reports private bytes, not working set.** Working set collapsed from 533 MB to
  73 MB under memory pressure on the same instance whose private bytes never moved.

## corebench

`../corebench` is the core-normalisation benchmark: identical .NET IL run on the measurement rig
and on a modern machine, three workloads (latency-bound FP, branchy integer, dependent random
memory), so "% of one core" can be quoted against a named CPU instead of floating free.
