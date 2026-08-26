# Changelog

All notable changes to DriftwoodServer are recorded here.

## [0.1.7] - 2026-08-26

### Fixed

- **No connected player's progress ever reached the world save.** `SaveManager.SaveServer`
  opens by walking `PlayerManager.Players` and calling each player's `SaveInventory()` -
  the call that folds their inventory, held item, health, fullness, baits, pockets and
  tutorial state into the save before the file is written. `PlayerManager` fills from
  `Player.OnStartClient`, a CLIENT-context callback that never runs on a headless host for
  a remote player, so on every dedicated server that walk was a no-op: every autosave,
  every panel save and every shutdown save wrote the world WITHOUT the people in it. It
  survived unnoticed because the game separately saves a departing player at disconnect,
  so a clean leave followed by a later autosave did persist - what was lost, every time,
  was everything a connected player had done since they last disconnected (the panel's own
  restart saves first, and that save could not see them), and everyone aboard on a crash.
  The host now prefixes `SaveServer` with the same walk over the server's own connection
  table the roster already uses, running the game's own `SaveInventory` for every connected
  player the vanilla walk misses; the game then serializes the records exactly as it always
  intended to. Offline players are untouched - the save carries the player list forward by
  reference and `SavePlayer` updates records in place by SteamID - and a failure captures
  as much of the crew as it can, never costing the save itself. The patch is optional: a
  game build that renames `SaveServer` stands it down (disconnect-only persistence, named
  in `featuresStoodDown`) rather than refusing to host.

### Added

- **A modded client can claim its real SteamID64** - `POST /api/v1/identity`, public by
  necessity because the claimant holds no credential yet, and bounded by what a claim may
  say: only an issuable individual-account id (never the host's reserved identity, never
  the synthetic per-connection range), only from the IP address the claimed connection
  plays from (read off the socket, never a header; unparseable fails closed), only onto a
  live remote connection, colliding with nobody aboard, and the first valid claim wins for
  the connection's life so it can never be swapped under a spawned character. The spawn
  that follows keys the player's save record on the person instead of on a connection slot
  - game 1.0.6 stopped sending the id, so an unmodded player's character belongs to
  whatever slot they land on and a rejoin can land somebody else's body. A client that
  claims nothing keeps the synthetic fallback and plays exactly as before; the answer is
  the same `{"ok":true}` whatever the claim's fate, so a probe learns nothing about who is
  aboard. What a claim deliberately does not prove is Steam account ownership - the server
  runs without Steam, so this restores the game's own 1.0.4/1.0.5 trust model (the client
  states its id), not more.

- Player chat commands, typed into the game's own chat by vanilla clients - nothing to
  install: `!stuck` teleports the player back to the island spawn (the shore by the wreck)
  through the game's own server-to-owner teleport order, the one an island change already uses
  on every client; `!playtime` answers with their time on this server, this session and in
  total; `!top` shows the catch leaderboard's top three and their own rank; `!help` lists them.
  A command line is never rebroadcast; the answer is a `[Server]` line addressed by name.
  Every refusal is decided on server state - an island change in progress, a downed player
  (the game's own respawn is the way back), a boss fight (the game blocks giving up during one
  for the same reason), the boat's driver (glued to the wheel by the client every frame), a
  cooldown - and says why. A per-player throttle and a crew-wide cap keep the server from ever
  amplifying one keyboard into everybody's chat. Every `!stuck` lands in the owner audit log
  with actor `chat`. `rescue <who>` on the owner console is the same teleport from the owner's
  side. Off switch: `[Chat] PlayerCommands = false`; cooldown: `[Chat] StuckCooldownSeconds`.
- The catch leaderboard: per player, fish landed, earnings, bosses finished, best catch and
  playtime, keyed on the SteamID64 the game replicates and fed only by server-side events in
  the game's own flow - the bite the server rolled (`CreatureManager.HookItem` ties the new
  fish to the rod's holder), the server's holder write when it is landed, the sell box's sale,
  and the server-side hit that finishes a boss. Nothing is a client's claim about itself, and
  what cannot be attributed (an explosion kill, a fish nobody held) is simply absent. Only
  identified players get rows: an unmodded player rides a synthetic per-connection id - a slot
  number FishNet reuses - and a row keyed on a slot would migrate to whoever lands it next, so
  the board refuses every id below the first real SteamID64 and starts counting a player when
  their client claims its real identity. Kept at
  `<SaveRoot>\<world>.leaderboard.tsv` beside the world save so snapshots, restores and panel
  backups carry it with the world; `top [n]` on the console prints it; `/status` and the
  readiness document carry the top ten as `leaderboard` for a panel card. Off switch:
  `[Leaderboard] Enabled = false`.
## [0.1.6] - 2026-08-25

### Fixed

- **Every purchase refused on a dedicated server.** How to Fish holds the crew's money
  twice: `MoneyManager._money` is the real balance (a `SyncVar<int>` loaded from the world
  save and moved by selling, granting and spending), while `MoneyManager.Money` is a plain
  static mirror written only by CLIENT code - `OnStartClient`, `OnStopClient`, and the
  `!asServer` half of the SyncVar's change callback. Two things read that static and both
  of them run on the SERVER: `CanAfford`, which is the gate inside all eight purchase
  ServerRpcs (`BuyItem`, `BuyBait`, `UnlockPocket`, `BuyAttachment`, `BuyBulletUpgrade`,
  `BuySharpnessUpgrade`, `BuyBoatMotor`, `BuyBoatRadar`), and `SaveManager.SaveServer`,
  which persists it into the world file. On a listen server the host is also a client, so
  the mirror tracks and nobody notices. On a dedicated server it freezes at the figure the
  world was loaded with - zero on a new world - so the client's own balance is correct, the
  client sends the purchase, and the server compares it against a stale number and drops it
  with no message. The player sees money that never goes down and an item that never
  arrives, on a server where selling visibly works, and the same frozen number is written
  back over the save so a session's earnings never persist. It fails permissively too: a
  save holding a large figure approves every purchase under it for ever. The host now keeps
  the static in step with the SyncVar on the server, which is exactly what the client half
  of the game does for itself - postfixes on `MoneyManager.OnStartServer`, `AddMoney`,
  `SellItem` and `RemoveMoney`, and a prefix on `SaveManager.SaveServer` because
  `OnStopClient` zeroes the mirror on the way down and a shutdown save would otherwise
  persist that zero over a real balance. The SyncVar is still the only thing that moves
  money and every purchase is still decided server-side. The five patches are optional and
  share one group, so a game build that renames any of them stands the mirror down and the
  server still hosts rather than refusing to host over a shop.

## [0.1.5] - 2026-08-25

### Fixed

- Every player spawning as the server. Game 1.0.6 removed the `steamID` parameter from
  `SpawnPlayer` - the client no longer tells the server who it is. On the non-Steam
  transport this product uses, the game now derives a joining player's identity ON THE
  SERVER from `SteamUser.GetSteamID()`, which a headless host guards to return the
  reserved host placeholder. So every joiner became `76561190000000001`: one shared
  per-player save record for the whole crew, and the roster's ghost-host filter hid
  every real player from the roster, the map, kick, block and the Discord alerts -
  which is why 0.1.4's connection-table fix still showed an empty roster on 1.0.6.
  While the game's own `SpawnPlayer` RPC reader is executing for a remote connection,
  the identity guard now answers a per-connection synthetic id from the same reserved
  sub-account space (`76561190000100000 + connectionId`), so every member of the crew
  is distinct, none of them is ever the host, and each gets their own save record.
  The real SteamID64 never crosses the wire on this game build, so real identities
  (and real persona names) are not knowable server-side; rosters on 1.0.6 show stable
  `Player-NNNN` placeholder names instead. The Steam name resolver skips the entire
  synthetic range rather than asking the Steam Web API about ids that were never issued.
- The game's codegen RPC methods are found by name prefix with a refuse-on-ambiguity
  rule, because their names carry a hash that moves on every game rebuild
  (`RpcReader___SpawnPlayer___596900633` on 1.0.4 vs `___1871804056` on 1.0.6).

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
