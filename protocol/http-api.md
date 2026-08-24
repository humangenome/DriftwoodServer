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
  block-list sweep itself. "Your host kicked me for no reason" arrives in a support ticket days
  later, and this line is the difference between an answer and a shrug.

`stop`, `restart`, `op` and friends are **named refusals**: they answer with where that thing
actually lives rather than with "unknown command", which would read as a typo. Lifecycle
is deliberately absent — the panel's stop and restart flush the world and take a backup first, and a
console shortcut past that ordering would be a data-safety regression dressed as a convenience.

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
