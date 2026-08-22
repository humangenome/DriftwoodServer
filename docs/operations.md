# Running a Driftwood server

## What starts what

The panel owns the lifecycle. `DriftwoodServer` is the process that the panel starts and stops; it
owns the game process, writes the host mod's configuration, verifies the pinned build before
anything runs, and refuses rather than starting a server that cannot host.

```
panel  ->  DriftwoodServer --config appsettings.json
             |-- verifies the pinned build (game code hash + Steam's install record)
             |-- writes BepInEx/config/com.humangenome.driftwood.host.cfg
             |-- starts the game with -batchmode -nographics, working directory = game root
             |-- reads host-ready.json and refuses to report Hosting on anything else
             `-- serves /health, and snapshots the world after a clean stop
```

## Stopping

Write `stop.requested` into the state directory, or stop `DriftwoodServer`, which writes it for
you. The host mod saves, closes the transport and asks Unity to quit; the game saves twice more on
its own way out. Only if it does not go quietly does the supervisor wait for the save tree to stop
changing and then force the process down — and it records which of those happened.

**Never kill the game by process name.** These servers and their clients are frequently the same
executable, so scope every match by path.

## Ports

| | |
|---|---|
| gameplay | `port`, UDP. This is the address a player connects to. |
| status / admin | `port + 1`, TCP. Scoped by firewall to loopback and the web server. |
| reserved | `port + 2` .. `port + 9` |

The band is 22003 with a stride of 10. **Not 7777** — nine games on this fleet already default to
7777, and taking it guarantees a first-boot bind failure on a shared host.

## When a server will not start

Read one file: `host-ready.json`. `phase` is `WillNotHost` and `reason` is one plain sentence. The
common ones:

| Reason mentions | What happened |
|---|---|
| "the game build no longer contains ..." | Steam updated the game and the host mod needs rebuilding against the new build. The named methods are the ones that moved. |
| "queued an update" / "not fully installed" / "last update ... failed" | Steam is midway through changing the game files. Let it finish, then re-verify. |
| "the game's code has changed" | Same as the first, caught by the code hash rather than by a missing method. |
| "saves would be shared with every other server" | The save redirect did not take. **Do not start it anyway** — two servers would overwrite each other's world. |
| "the world did not finish loading" | The port has already been closed. The server reports as down, which is the correct answer. |
| "could not bind UDP port" | Something else holds the port. Check `netsh int ipv4 show excludedportrange` — Windows NAT reserves runtime port ranges and re-grabs them on every boot. |

## Checking a server without starting it

```
DriftwoodServer --verify-build appsettings.json
```

Prints the installed game's code hash and Steam's build id, and exits non-zero if they do not match
the pin.

## Backups

```
DriftwoodServer --snapshot appsettings.json
```

Asks the running server to save, waits for the save tree to settle, zips it, and then **reads the
archive back** to confirm the world file is in it. A snapshot that would not restore the world is
reported as a failure rather than written and forgotten.
