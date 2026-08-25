# Changelog

All notable changes to DriftwoodServer are recorded here.

## [0.1.4] - 2026-08-25

### Fixed

- The roster, the map positions, kick, block, the blocklist sweep and the Discord
  join/leave alerts now actually see remote players. The game registers players into
  `PlayerManager` from a client-context callback (`Player.OnStartClient`) that a
  headless host never runs for a remote player - the ghost client deliberately never
  enters the world - so every identity feature was reading a list that stays empty on
  exactly the machine this product ships to. Proven live on the canary: a player
  standing in the world, transport count 1, roster empty. The host now reads the
  server's own connection table (`ServerManager.Clients`), where the game itself puts
  every owned player object with its SteamID64 set server-side before ownership is
  granted. `PlayerManager` stays as a merge source in case a future game build moves
  the ownership shape.

## [0.1.3] - 2026-08-25

### Added

- `/api/v1/status` carries the world block the hosting panel's map and console read:
  the crew's current island (1-based), how many islands there are and how many the save
  has unlocked, whether an island change is in flight, the crew's one shared wallet, the
  current island's authored centre and radius, uptime, and one position entry per connected
  player (`x`/`y`/`z`, absent rather than zero when the sampler could not place a row).
  Positions ride the same loopback-or-signed tier as the roster ids and never the public
  routes: where somebody is standing is the same class of fact as who they are. Everything
  is sampled on the main thread beside the roster; a game build that moves a manager costs
  the world block, never the roster sample.
- `[Discord] AlertJoinLeave` / `AlertBoss` / `AlertIsland` / `AlertBlocked`, all default on,
  so the hosting panel can let an owner pick which alerts their channel receives. Turning
  joins and leaves off still tracks the roster, so switching them back on later starts from
  the present rather than a flood of "joined" for everyone already aboard.

## [0.1.2] - 2026-08-24

### Added

- Owner gameplay commands on the console, built on the game's own host-gated dev suite
  (no player can invoke it on a dedicated server; the host process is the host):
  `money [add|remove <n>]` for the crew's one shared wallet, `island [next|prev|set <n>]`
  to move the whole crew between islands (a `set` beyond the crew's progression unlocks the
  island, deliberately - the command exists for stuck-progression rescues), `spawn <item>`
  to drop any of the game's spawnable items next to a connected player, and `killboss` to
  end a boss fight as a real kill - the same server-side hit path a legitimate kill takes,
  so the trophy and the progression follow. Every mutation is audited like the rest of the
  owner actions; every refusal says why in a sentence.
- Discord alerts, opt-in via a webhook: joins and leaves (with real names once the Steam
  Web API key is in place), boss kills, island moves, and blocked players who tried to come
  back. The webhook comes from `<instance root>\Driftwood\discord-webhook.txt` (the
  customer's own file, which survives config rewrites) or `[Discord] WebhookUrl` in the
  plugin config; only a genuine Discord webhook URL is accepted, so the field can never
  point the server's outbound requests at an arbitrary machine. Fail-soft throughout: a
  dead webhook drops alerts with one warning and affects nothing else, and no join, page
  or frame ever waits on Discord. The readiness document and `status` carry a one-sentence
  `discordAlerts` state so "why no alerts" is answerable from the panel.
- A shutdown warning in chat: when a stop or restart is requested through the stop file and
  players are connected, the server now broadcasts a `[Server]` line telling the crew it is
  saving and going down, and gives the message a moment to reach them before the transport
  closes.
- Real player names on the roster. The game never transmits a name — each client resolves the
  replicated SteamID64 through its own Steam client, which a headless host does not have — so the
  roster showed stable synthetic placeholders. With a Steam Web API key configured
  (`[Identity] SteamWebApiKeyFile`, or `SteamWebApiKey` inline for self-hosters) the host now
  resolves ids to persona names over `ISteamUser/GetPlayerSummaries`: batched, cached across
  restarts, and strictly off the hot path — no page render, join or frame ever waits on the
  lookup, and any API failure just keeps the placeholder. Names stay display-only; every
  per-player decision keys on the SteamID64. `steamNameResolution` in the readiness document says
  in one sentence why names are or are not resolving.
- Owner actions on the console, built from the two primitives the shipped game actually has
  (the game itself ships no admin concept, no kick, no ban and no server-to-player chat):
  `kick` (FishNet's server-side connection kick), `block` / `unblock` / `blocked` (a persistent
  block list keyed on SteamID64 — never on a name — enforced within ~2 s of a blocked player
  connecting, stored under the instance root so neither a validate nor a world restore can touch
  it), and `say` (a `[Server]` chat line through the game's own chat RPC, rendered by every
  vanilla client). `players` on the authenticated console now shows each player's SteamID64; the
  public `/players` route still never carries an id.
- An owner-action audit log (`Logs\owner-actions.log`, `audit [n]` on the console): every kick,
  block, unblock and broadcast with a transport-derived actor, its target and whether it worked —
  so "the host kicked me for no reason", arriving days later, has an answer.

### Fixed

- The supervisor now writes `steam_appid.txt` into the game folder before every launch. Without
  that file the game's Steam wrapper quits during boot - exit code 0, clean log - so a server
  built by following the README's self-hosting guide failed on its very first start: the fleet's installer
  writes the file, but a self-host install had nobody to do it. Found by executing the
  self-hosting walkthrough end to end on a clean machine.
- `appsettings.example.json` now ships `httpPort: 0`, which the self-hosting guide already
  documented as the value to keep. The old example value of 22004 could never host: the
  supervisor's own health endpoint claimed the port before the game loaded, and the host mod -
  whose API takes game port + 1 on its own - then refused with "could not open its status port".
- A stop requested through `host-state\stop.requested` is now reported as the clean stop it is.
  The host mod consumes the stop file (save, final readiness, delete, quit), so by the time the
  supervisor saw the exit the file was gone and every clean stop was announced as "stopped
  unexpectedly" - which reads like a crash to exactly the operator who just followed the manual.
  The supervisor now also accepts the mod's final "Stopped" readiness as proof of a requested
  stop.
- `global.json` no longer rejects current .NET 8 SDKs. It pinned 8.0.100 with
  `rollForward: latestPatch`, which only matches the 8.0.1xx feature band - so on the 8.0.4xx
  SDK that dotnet.microsoft.com actually ships today, every `dotnet` command run from inside the
  repo failed with "A compatible .NET SDK was not found" (the README build commands among them).
  `latestFeature` accepts any .NET 8 SDK and still refuses 9.x.

## [0.1.1] - 2026-08-22

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

## [0.1.0] - 2026-08-22

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
