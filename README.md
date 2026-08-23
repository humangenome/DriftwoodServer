# DriftwoodServer

The dedicated-server supervisor and host mod behind **Driftwood**, dedicated server hosting for
[How to Fish](https://store.steampowered.com/app/4001890/).

How to Fish ships no dedicated server, but it does ship a complete dedicated-server *code path*:
single-player starts a real FishNet server on a raw-UDP transport and connects a local client to
it. Driftwood drives that path headlessly, keeps the world persistent, hides the host's own
placeholder player, and refuses to host rather than hosting nothing.

## What is here

| | |
|---|---|
| `host-mod/DriftwoodHost` | The BepInEx 5 plugin that runs inside the game. Starts the server on the game's own transport, loads or creates the world, enforces the slot limit, hides the host's placeholder player, guards every Steam call, keeps the process silent, and publishes a readiness signal. |
| `src/DriftwoodServer` | The .NET 8 supervisor. Verifies the pinned game build before anything starts, owns the game process and its logs, consumes the readiness signal, and serves a health endpoint. |
| `bench/` | Measurement rigs. Never shipped — see `bench/NOT-SHIPPED.md`. |

## Run your own server

[docs/self-hosting.md](docs/self-hosting.md) walks the whole thing end to end on a Windows machine
you control: copy your own game files, install the release overlay, build the supervisor, pin the
build, open two ports, start. How to Fish ships no dedicated server, so this repo is the only way
to run one — hosted by us or by you.

## Design rules this codebase is built around

**A server that cannot host must not present a port.** Every patch target is resolved before any
patch is applied, every miss is reported in one block, and the count of what was actually patched
is asserted against the patch library rather than inferred. A required patch missing is a refusal
with one plain sentence naming what failed.

**Readiness means the world is running, not that the port is open.** The gameplay port binds
before the world exists, so process-alive, port-listening and "the process answered" are all
equally true of a server whose entire mod stack failed to apply. The only signal anything consumes
is derived from the world actually being up, and if the world never arrives the port is closed
again so the server reports as down rather than as a healthy server with nothing behind it.

**An unknown player count stays unknown.** Zero is what marks a server empty and empty servers get
reaped, so an unknown population is reported as unknown and never coerced to zero.

**Catching is not fixing.** Two patches deliberately swallow exceptions from methods that must
keep running. Every swallow is counted, the rate is alarmed on, and the totals are published — a
handler firing thousands of times a second is a broken feature wearing a seatbelt.

## Building

```
dotnet build src/DriftwoodServer/DriftwoodServer.csproj -c Release
dotnet test  src/DriftwoodServer.Tests/DriftwoodServer.Tests.csproj -c Release
```

The host mod compiles against the game's own assemblies. Point `ManagedDir` at a real install:

```
dotnet build host-mod/DriftwoodHost/DriftwoodHost.csproj -c Release \
  -p:ManagedDir="C:\Path\To\How to Fish\How to Fish_Data\Managed"
```

Game binaries are never committed.

## Verifying an install without starting it

```
DriftwoodServer --verify-build appsettings.json
```

Only `Assembly-CSharp.dll` identifies a build — the Unity launcher stub does not change between
versions — so the pin is that assembly's hash, cross-checked against Steam's own install record
for a queued, failed or half-applied update.

## Official hosting

DriftwoodServer is officially supported by
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/how_to_fish/?utm_source=github&utm_medium=readme&utm_campaign=driftwoodserver),
which runs How to Fish servers with Driftwood installed and kept on the latest pinned
release.

## License

[MIT](LICENSE) © HumanGenome
