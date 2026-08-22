# Changelog

All notable changes to DriftwoodServer are recorded here.

## 0.1.1

### Fixed

- A world could not be created on retail build 1.0.5. The game added a third parameter to
  `SaveManager.CreateServer` (`isPublic`), the host's hard-coded two-argument reflection call threw
  `TargetParameterCountException` inside the boot coroutine, and nothing caught it: the process
  stayed alive with its HTTP API answering while the gameplay socket was never bound and the world
  never loaded. Every argument list is now built from the method the game actually shipped, so a
  parameter added by a future build takes its own default instead of breaking the boot.
- World load can no longer hang the host. Anything thrown while loading or creating the world is
  caught and reported as a refusal, because a host that refuses can be retried and a host that
  hangs cannot.

### Added

- `gameApiDrift` in the readiness document: the parameters the game has grown on methods this host
  calls by reflection. Empty is the normal state.

## 0.1.0

### Added

- `DriftwoodHost`, the BepInEx 5 plugin that runs the game as a dedicated server: it selects the
  game's own direct-UDP transport, binds an explicit address, loads or creates the world, applies
  the slot limit before the server starts listening, and publishes a readiness signal.
- A fail-closed patch plan: every target is resolved before any patch is applied, every miss is
  reported in one block, and the number of methods actually patched is asserted against the patch
  library rather than inferred from a call that returned cleanly. A missing required patch refuses
  to host.
- Steam guards at the SDK boundary rather than at call sites. Without them a headless server cannot
  complete a player spawn: the throw escapes into the netcode's shared spawn loop and every object
  queued behind it is left registered but never initialised.
- Per-instance save redirection, so servers sharing a machine cannot overwrite each other's world,
  plus two boot markers the panel asserts on.
- Suppression of the host's own placeholder player, so an empty server genuinely reports zero
  players and no phantom appears in the world or on any roster.
- A status and control API on the gameplay port plus one. Unknown player counts are reported as
  unknown and never as zero.
- `DriftwoodServer`, the .NET 8 supervisor: build pin verification against the game's own code and
  Steam's install record, process and log ownership, readiness consumption, a health endpoint, and
  save-then-snapshot backups.
- The launcher-facing half of the host API: `/health` (online state, server name, player and slot
  counts), `/players` (who is connected and for how long), `/manifest` (the server's real loaded
  plugin set), `/console` (a host console: status, players, world, save, snapshots) and
  `/snapshots` (list, take, download, restore, and import a save from a file). This game runs as no
  Steam game server, so nothing answers a Source query anywhere and these routes are the query
  surface a join tool has to use.

### Changed

- A refusal now ends the process. It used to leave the process alive, and after the gameplay port
  had already been bound - a world-load timeout is the reachable case - that meant a closed socket
  and a menu scene running flat out on an uncapped loop, which nothing was watching for. The
  gameplay port closes at once; the status API answers "why" for one supervisor poll interval; then
  the process ends, forcibly if `Application.Quit` is ignored.
- `PauseWorldWhenEmpty` saves the world before stopping the clock. The game's auto-saver runs on
  scaled time, so freezing an empty world left everything since the last auto-save dirty in memory
  for as long as the server stayed empty - which is most of its life - and a crash or a host reboot
  days later would lose the final minutes of the last session anyone played.
- The empty-server frame rate is decided from the player count the readiness sampler publishes,
  not from a raw transport-connection count. The old test assumed the host's own loopback client
  was always one of those connections; if it dropped while a remote player stayed, one real player
  read as an empty server and the loop fell to its idle rate with somebody on it.
- `MuteAudio` is honoured. It was read and never consumed: setting it to false was accepted, echoed
  back as recognised, and changed nothing. Silence is still installed before the config is read,
  because the gap before that is exactly when the audio engine finds a device, so the setting is
  honoured by releasing afterwards rather than by not installing.
- A required patch target whose lookup THREW now refuses the start. It was fail-open: the catch
  left the method unresolved, so it was never patched, never counted missing, and the server came
  up presenting a port with a spawn-path guard that had never been installed.
- A frame cap far below the network tick rate refuses the start, and a cap merely below it warns.
  The floor existed only in the supervisor, which is not what runs in production.
- A configured join password refuses the start. Nothing enforces one - these hosts accept any
  client that knows the address - so a server with a password set would have come up open while
  the panel believed it was locked.
- One authentication scheme on the API, and it is the family's HMAC signature over
  `METHOD\npath\ntimestamp\nsha256(body)`, keyed by the sha256 digest of the API token, with a
  five-minute window and a replay guard. The static bearer token it also used to accept is gone:
  two schemes on one API is how one of them ends up unimplemented, which is what had happened -
  a join tool signing every request could not have authenticated to this server at all.
- Every route states its audience in a route table the dispatcher reads, and there is no default:
  a path that matches no row is refused rather than allowed because nobody said otherwise. Player
  identifiers are published only on the loopback status route, never on the public one, and the
  two payloads are built by different code rather than by one payload behind a flag.
- The API listens on a plain socket rather than through the operating system's HTTP stack, which
  needs a registered URL reservation for anything but loopback. Without one, the old listener
  caught the failure and quietly fell back to loopback - reporting success - which on a port that
  faces players is invisible and total.
- Requests that touch the game are run on the game's own thread instead of on the listener's.
  Saving the world from a background thread is an exception on a busy machine and silence on a
  quiet one, and it is the one call whose failure loses somebody's world.

### Fixed

- The host mod no longer writes `PlayerPrefs`. Unity keys those per Windows user rather than per
  install, so zeroing the volume keys reached every copy of the game that account can run,
  including the operator's own. The every-frame audio clamp already guaranteed silence in-process
  and is the only part that cannot leak.
- A refusal publishes an unknown player count rather than zero. Zero marks a server empty and an
  empty server gets reaped; the rule held only because the HTTP layer happened to recompute it.
