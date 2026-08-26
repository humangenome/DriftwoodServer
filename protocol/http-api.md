# Host HTTP API

Served by the host mod (`DriftwoodHost`) on **gameplay port + 1**, TCP, bound to `0.0.0.0`.

This game runs as no Steam game server at all, so there is **no A2S responder and no query port**.
This endpoint is the A2S replacement *and* the only thing a player's Driftwood launcher can ask
whether a server is up.

It is written on a raw `TcpListener`, not `HttpListener`. An `HttpListener` wildcard prefix needs a
URL ACL and throws without one; the previous implementation caught that and fell back to a
loopback-only prefix, logging success. On a port that faces players, that fallback is invisible and
total — every launcher reads Offline while the panel, on loopback, sees a perfectly healthy host.

## Two audiences, and that is the whole design

| audience | reaches it from | routes |
|---|---|---|
| the hosting panel and the supervisor | `127.0.0.1` | `/status`, `/save` |
| the player's Driftwood launcher | the open internet | `/health`, `/players`, `/manifest`, `/console`, `/snapshots*` |

**A SteamID64 never leaves the box unauthenticated.** `/status` publishes the roster as
`"<steamid64>:<name>"` and is **loopback-only** unless the caller signs. `/players` publishes names
and connect durations and nothing else. They are two payloads built by two serializers, not one
payload behind a flag — a flag can be wrong; a serializer that never receives the id cannot leak it.

## Auth: HMAC, and only HMAC

```
X-Driftwood-Timestamp: <unix seconds>
X-Driftwood-Signature: hex(HMACSHA256(sha256(AuthToken), "METHOD\n<path>\n<ts>\n<sha256hex(body)>"))
```

- The HMAC **key is the raw 32-byte sha256 digest** of `AuthToken`, not its hex text.
- `<path>` is the URL path only — no host, no query string.
- Signatures older or newer than **300 seconds** are refused, and each signature is accepted **once**
  (replay guard). Without that, anyone who captured one `restore` request could roll a world back.
- `AuthToken` is the server's API secret, written by the hosting endpoint into
  `[Server] AuthToken`. An empty token makes every signed route refuse — fail-closed, and
  logged loudly at boot.

There used to be a second scheme here, a static token in `X-Driftwood-Auth`, while the launcher
signed every request the way the rest of the family signs them. Two schemes on one API is how one of
them ends up unimplemented, and that is exactly what had happened: the launcher's console and its
snapshot buttons could not have authenticated to a Driftwood host at all. **The static header is
gone.** Every caller signs — the hosting endpoint, the launcher
(`DriftwoodHttpClient.BuildSignedRequest`), the bench rig, and the .NET supervisor.

## Route table

| route | auth | notes |
|---|---|---|
| `GET /api/v1/health` | none | **200 only when the world is running**, else 503 |
| `GET /api/v1/players` | none | names + durations, no ids |
| `GET /api/v1/manifest` | none | the server's real loaded plugin set |
| `POST /api/v1/identity` | none | a modded client claims its own SteamID64; the answer never varies |
| `GET /api/v1/status` | loopback, or signed | the panel document, ids included |
| `POST /api/v1/save` | loopback, or signed | flush the world now |
| `POST /api/v1/console` | signed | one host command |
| `GET /api/v1/snapshots` | signed | list |
| `POST /api/v1/snapshots` | signed | save, then archive |
| `GET /api/v1/snapshots/{id}/download` | signed | zip + `X-Driftwood-Sha256` |
| `POST /api/v1/snapshots/{id}/restore` | signed | replaces the world, then ends the process |
| `POST /api/v1/snapshots/import-restore` | signed | uploaded zip, same as above |

A trailing slash is not a different route. There is no default tier: a path that matches no row is
404, never "allowed because nobody said otherwise".

## `GET /api/v1/health`

```json
{
  "ok": true,
  "instance": "Driftwood",
  "server_name": "Bob's island",
  "driftwood_version": "0.1.0",
  "driftwood_build": "1.0.4",
  "gameplay_port": 22003,
  "max_players": 8,
  "player_count": 0,
  "password_protected": false,
  "phase": "Hosting"
}
```

**200 only when a player could actually join.** A server still loading, or wedged, answers `503`
with the same body and `ok: false`. Answering 200 for it would put an Online badge on a server
nobody can get into.

The consequence worth stating: because a 200 means the world is running, `player_count` on a 200 is
always a real number. The `-1` unknown sentinel belongs to `/status`, whose callers reap empty
servers and must be able to tell empty from unknown.

`driftwood_version` is the launcher's liveness proof — it treats an absent or empty value as
Offline — so that field is never blank. `password_protected` is always `false`: the panel emits no
join password and `HostConfig.Validate()` refuses to start when one is configured, so publishing
`false` explicitly keeps the launcher's pre-flight prompt off rather than leaving it on "unknown".

### The world block and player positions (0.1.3)

`/status` also carries, on the same loopback-or-signed tier as the roster ids:

```json
{ "island": 2, "islandTotal": 5, "islandUnlocked": 3, "islandChanging": false,
  "wallet": 1234, "islandCentre": [-200.0, 598.7], "islandRadius": 55.0,
  "uptimeSeconds": 4210.5,
  "positions": [ { "id": "7656119...", "name": "Steve", "connected_seconds": 412,
                   "x": -196.9, "y": -1.0, "z": 612.0 } ] }
```

- `island` is 1-based, the way players count islands; `islandTotal` is the number a crew can
  stand on; `islandUnlocked` is how far the save has progressed. `0` means unknown (the world
  is not running). `islandChanging` is true while an island swap is loading.
- `wallet` is the crew's ONE shared wallet; `-1` means unknown.
- `islandCentre` is the current island's authored centre `[x, z]` and `islandRadius` its
  authored size, so a plan-view picture of the island can be registered to `positions`.
  `null` / `0` when no island is loaded.
- `positions` is one entry per connected player. `x`/`y`/`z` are **absent**, not zero, for a
  row the sampler could not place, so a consumer draws nothing rather than a dot at the
  origin. **Never on the public routes** - where a person is standing is the same class of
  fact as who they are.
- `uptimeSeconds` is time since this host process started.

## `GET /api/v1/players`

```json
{ "instance": "Driftwood", "count": 1,
  "players": [ { "name": "Steve", "connected_seconds": 412, "ping_ms": 38 } ] }
```

The same facts an A2S player query publishes for every other game on this fleet. `ping_ms` is
**absent**, not zero, when this build of FishNet exposes no per-connection round-trip time — the
launcher renders the row without it, and a fabricated number would be worse than a missing one.
Empty while the world is not running, because a population that is unknown is not a roster.

## `GET /api/v1/manifest`

```json
{ "manifest_version": 1, "instance": "Driftwood", "driftwood_version": "0.1.0",
  "generated_unix": 1755900000,
  "server_mods": [ { "id": "com.humangenome.driftwood.host", "name": "Driftwood Host",
                     "version": "0.1.0", "ours": true } ],
  "required": [], "recommended": [], "blocked": [] }
```

Public on purpose: a player needs to know what a server runs *before* they hold any credential for
it. `server_mods` is read from BepInEx's own chainloader, so it names what is actually loaded rather
than what happens to be on disk. The curated lists are empty on a hosted instance because
this product ships no mod picker for this game; the launcher hides an empty curated section rather
than rendering a card that apologises for itself.

## `POST /api/v1/identity`

Request `{"clientId": "3", "steamId": "76561197960287930", "name": "Steve"}` - both numbers as
**decimal strings**, because a SteamID64 does not survive every JSON number path. Reply
`{"ok": true}`, always: nothing in the response varies with the claim's fate, so a probe cannot
learn who is aboard by watching this route.

A client running DriftwoodConnect posts its own real SteamID64 the moment it connects, and the
spawn that follows keys the player's save record on the **person** instead of on a connection
slot (game 1.0.6 stopped sending the id, so an unmodded player's character is keyed on a
synthetic per-connection identity and a rejoin onto a different slot lands somebody else's
body). A client that sends nothing keeps the synthetic fallback; joining and playing never
depend on a claim.

Public by necessity - the claimant holds no credential yet - and bounded by what a claim is
allowed to say:

- only an **issuable individual-account SteamID64** is claimable: never the host's reserved
  identity, never the synthetic per-connection range, never a malformed id;
- the claim must arrive **from the same IP address the claimed connection plays from**, read
  off the HTTP socket itself, never a header. Unparseable addresses fail closed;
- the claimed connection must be live and remote, the id must collide with nobody aboard
  (claimed or spawned), and the **first valid claim wins for the connection's life** - it
  cannot be swapped under a spawned character. A claim that arrives after the spawn only
  attaches the display name;
- claims are rate-limited and the table is bounded; a dropped claim degrades to the synthetic
  fallback, never to an unverified identity.

**What this deliberately does not prove: Steam account ownership.** The server runs without a
Steam client, so a claim asserts an id the way the game's own 1.0.4/1.0.5 join flow did - the
client states it, nothing countersigns it. Someone who knows an absent player's SteamID64 and
runs a modded client can claim it and inherit that saved character; that is the game's own
historical trust model, restored, not widened. The name is display-only, sanitised, and
corrected by the Steam Web API resolver when a key is configured.

## `POST /api/v1/console`

Request `{"command": "status"}`, reply `{"ok": true, "output": "..."}`.

A refused or unknown command answers **200 with `ok: false`** and an output line saying why, so the
console prints a reason instead of an HTTP status.

**This is a HOST console, not a game console.** The shipped How to Fish build has no admin concept,
no ban list and no server console — its cheat commands are local and client-side. The read-side
commands are what the host process genuinely knows; the owner actions are built by the host from
the two primitives the game genuinely has (FishNet's server-side connection kick, and the game's
own chat RPC invoked from the server, which every vanilla client renders as a `[Server]` line):

```
help  status  players  version  world  save  snapshot  snapshots
kick  block  unblock  blocked  say  audit
money  island  spawn  killboss
rescue  top
```

- `players` on this console carries the **SteamID64** beside each name — the caller authenticated,
  and the id is the only honest key for `kick` and `block`. The public `/players` route still
  never carries an id.
- `kick <SteamID64 or name>` removes a connected player (they can reconnect). A name must be
  unambiguous among connected players; two players can wear the same name, so ties refuse and
  point at the id.
- `block <SteamID64 or name>` adds them to the server's block list, removes them if connected, and
  keeps them out (a sweep on the roster sampler removes a blocked player within ~2 seconds of
  connecting — the game offers no earlier hook that already knows the SteamID). `unblock <id>`
  reverses it; `blocked` lists entries. **Blocks key on the SteamID64, never the name.** The list
  lives under the instance root (`Driftwood\blocklist.txt`), outside the game tree and outside
  `Saves\`, so a validate cannot clear a ban and a world restore cannot roll one back. `ban` /
  `unban` / `banlist` are accepted aliases.
- `say <text>` broadcasts a `[Server]` chat line to every connected player, through the game's own
  `OnlineChatManager` RPC with a from-id of 0 — which vanilla clients render as the server because
  a launcher-joined client's lobby id is nil. No client mod involved.
- `audit [n]` shows the last n owner actions (default 20). Every kick, block, unblock and
  broadcast is recorded to `Logs\owner-actions.log` — timestamp, actor, verb, target, ok/refused,
  detail — with the actor **transport-derived, never claimed**: `panel` for a loopback caller,
  `console` for a remote caller that proved itself with the signed API secret, `server` for the
  block-list sweep itself, `chat` for a player's own `!stuck` (the target column names them). "Your host kicked me for no reason" arrives in a support ticket days
  later, and this line is the difference between an answer and a shrug.

The gameplay commands ride the game's own host-gated dev suite (`DazedCommands` and the managers
under it). No player can invoke that suite on a dedicated server; the host process is the host, so
these are owner commands with the same audit trail as everything above. Each drives the underlying
manager directly with server-appropriate arguments, because the game's own command layer reaches
for a local player and a camera a headless host does not have:

- `money` shows the crew's ONE shared wallet (that is the game's economy model, not a
  simplification); `money add <n>` / `money remove <n>` change it, capped at 1,000,000 per
  command, and the game itself clamps the wallet at zero.
- `island` shows where the crew is, 1-based; `island next` / `island prev` are the game's own
  island moves with its own wrap-around; `island set <n>` sails everyone to a specific island and
  unlocks it first if the crew had not reached it - the command exists for stuck-progression
  rescues, and teleporting a crew onto a locked island would leave the save disagreeing with
  itself. Every island move teleports the whole crew to the island spawn together; that is how
  the game itself changes islands.
- `spawn <item>` drops one of the game's spawnable items (the in-game name, spaces optional) next
  to a connected player. An empty server refuses rather than dropping the item where nobody
  stands.
- `killboss` deals the active boss a killing blow through the same server-side HP path a real hit
  lands, so the death, the trophy and the progression follow the game's own kill flow. Refuses in
  a sentence when no boss is up or the boss is in an invulnerable phase.

The player-facing layer, from the owner's side:

- `rescue <SteamID64 or name>` sends a connected player back to the island spawn - the same
  teleport a player gets by typing `!stuck` in chat, with the same refusals (see below), audited
  as `rescue`.
- `top [n]` prints the catch leaderboard: rank, name, catches, earnings, bosses, playtime and
  best catch, ranked by earnings (default 10 rows). `leaderboard` is an alias. Also carried by
  `/status` as `leaderboard` for a panel card.

`stop`, `restart`, `op` and friends are **named refusals**: they answer with where that thing
actually lives rather than with "unknown command", which would read as a typo. Lifecycle
is deliberately absent — the panel's stop and restart flush the world and take a backup first, and a
console shortcut past that ordering would be a data-safety regression dressed as a convenience.

## Player chat commands (no route - the game's own chat)

Players type these into the game's ordinary chat box. Vanilla clients, nothing to install:
the game already sends every chat line to this server (`Server.SendChatMessage`, a ServerRpc
whose only server-side job is to rebroadcast), and the host answers on the same pipe.

```
!help      the list
!stuck     teleport yourself back to the island spawn (the shore by the wreck)
!playtime  your time on this server, this session and in total
!top       the catch leaderboard's top three, and your own rank
```

- A command line is **not rebroadcast** - the crew sees the server's answer, not the request.
  Anything that is not `!` followed by a letter (`!!!`, `! hi`) is ordinary chat and passes
  through untouched.
- **Replies are public.** The game has no private server-to-player chat (its one server chat
  pipe is an observers broadcast), so every answer is a `[Server]` line addressed by name that
  everyone sees. Hence the throttle: one reply per player per 3 s and at most 12 crew-wide per
  10 s, excess dropped silently; `!stuck` has its own per-player cooldown (`[Chat]
  StuckCooldownSeconds`, default 60) whose refusal names the seconds remaining.
- The sender is the player whose **transport connection** the line arrived on (a prefix on the
  RPC reader captures it), never the id the client wrote into the message.
- **How `!stuck` works, and what "server-authoritative" means here.** A player's position is
  client-owned in this game - the server relays positions, it never simulates a player - and the
  one movement primitive the game ships is a server-to-owner order, `Player.RPCTeleport`, which
  the owning client obeys by running its own teleport and then reporting the new position flagged
  so every other client snaps its proxy. That is exactly how an island change moves the whole crew.
  The server decides whether and where; the client carries it out. The destination is the island's
  authored spawn transform (`SpawnManager.PlayerSpawnPos/Rot`), the same point the game uses for a
  fresh spawn, a respawn and an island arrival.
- **Refusals**, all decided on server state: the world is not running; an island change is in
  progress or finished less than 5 s ago (the game's own guard); no island is loaded; the player
  is down (the game's own respawn is the way back); a boss fight is on (the game blocks giving up
  during a boss for the same reason); the player is driving the boat (the client glues the driver
  to the wheel every frame, so the teleport would be undone within a frame); cooldown.
- Every `!stuck`, granted or refused, lands in `owner-actions.log` with actor `chat`.

Off switch: `[Chat] PlayerCommands = false`. Readiness carries `playerChatCommands` as one
sentence: `on (!help, !stuck, !playtime, !top)` / `off (...)`.

## The catch leaderboard (carried by `/status`, no route of its own)

`/status` and the readiness document carry `leaderboard`: the top ten rows as

```json
[{"rank":1,"steamId":"7656119...","name":"Ryan","catches":12,"earnings":3450,"bosses":1,
  "playtimeSeconds":8040,"bestCatch":"Tuna","bestCatchWorth":800,"lastSeenUnix":1756100000}]
```

and `leaderboardState` as one sentence (`on (N player(s) on the board)` / `off (...)`). Ids are
included because this surface is loopback or signed - the same trust as the roster; the public
`/players` route never carries it.

**Where the numbers come from** - every one a server-side event in the game's own flow:

| column | event | why it is honest |
|---|---|---|
| catches | a fish the server hooked on player P's rod is later held by anyone | the bite is rolled by `CreatureManager.HookItem`, server-only, which ties the new fish to the rod's holder; the landing is the server's own holder write (`Item.SetSyncedHolder`). Credit goes to the angler who hooked it, not the hand that grabbed it |
| earnings | `MoneyManager.SellItem` (the sell box, server-side) for an item whose bite this server saw; else the item's `LastHolder` | the sale is the moment the shared wallet is paid |
| bosses | `Server.HitCreature` leaves a boss at zero HP; the player the hit names | the console's `killboss` credits nobody, deliberately |
| playtime | seconds between consecutive roster samples in which the player was present | this server's clock, never the client's |

Not claimed, because it cannot be attributed: a boss killed by an explosion (that path carries
no player), a fish that died to a fall or a bird before anyone held it.

**Only identified players get rows.** Game 1.0.6 stopped sending a joining player's SteamID64,
so an unmodded player is keyed on a synthetic per-connection identity - a connection SLOT that
FishNet reuses. A row keyed on a slot would migrate to whoever lands that slot next, and a
leaderboard that credits the wrong player is worse than none - so the board refuses every id
below the first real SteamID64 and counts a player from the moment their client claims its real
id (`POST /api/v1/identity`, the DriftwoodConnect claim). Until then their catches go
unrecorded, deliberately, rather than recorded against a stranger-to-be.

**Where it lives:** `<SaveRoot>\<world>.leaderboard.tsv`, one tab-separated row per SteamID64,
beside the world save - deliberately WITH the world, so a snapshot or a panel backup carries it and
a world restored to last Tuesday shows last Tuesday's board, the same way it shows last Tuesday's
money. The game's own loader reads only `*.txt` there. Flushed at most once a minute when
something changed, and on shutdown. Off switch: `[Leaderboard] Enabled = false`.

## `GET|POST /api/v1/snapshots...`

A snapshot is a zip of the server's save directory, kept in `<instance root>\Snapshots` — beside
`Logs\` and outside the game tree SteamCMD owns, so a validate cannot take a customer's backups.
The newest 20 are kept.

- `GET /api/v1/snapshots` → `{"snapshots":[{"id","taken_unix","size_bytes","sha256"}]}`
- `POST /api/v1/snapshots` → `{"snapshot":{"id":"..."}}`. **Flushes the world first and refuses if
  the flush fails.** A snapshot taken before the flush captures the file the flush was about to
  replace — a valid-looking archive of stale data, which is the worst failure a backup can have
  because it only shows up when it is restored.
- `GET /api/v1/snapshots/{id}/download` → the zip, with `X-Driftwood-Sha256`.
- `POST /api/v1/snapshots/{id}/restore` and `POST /api/v1/snapshots/import-restore`:
  1. snapshot what is there now, so a restore is never a one-way door;
  2. extract into a staging directory, so a corrupt archive cannot half-replace a live world;
  3. reconcile the world **name** (below);
  4. swap the staged tree in;
  5. answer `{"ok":true}`, **then end the process** so the supervisor brings the world back on the
     restored save.

**The world-name trap.** The world a server loads is `Saves\<WorldName>.txt` and `WorldName` comes
from the panel, not from the archive. Restore somebody's `MyIsland.txt` onto a server configured for
`Driftwood` and the files land, the server starts, finds no `Driftwood.txt`, **creates an empty
one** — and the customer is looking at a brand new world having just been told the restore
succeeded. A single world file in the archive is renamed to the configured name; several, and none
matching, is a refusal with a sentence saying so.

**Zip slip.** Entry names are attacker-controlled — the launcher lets a player upload any zip. Every
entry is flattened to a basename and re-resolved against the destination, and anything that lands
outside it refuses the whole archive.

## The supervisor dependency, stated plainly

Restore needs the process to end and be brought back. Both candidate supervisors (the inline
PowerShell loop that runs in production today, and the unshipped .NET `DriftwoodServer`) relaunch a
host that exits — that is what a supervisor is — so the dependency is on the property, not on which
one wins. **Everything else in this API is independent of the open S1 question**, which is why the
API lives in the mod: the mod is the component that is definitely on the fleet.
