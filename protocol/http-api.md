# Host HTTP API

Served by the host mod on **gameplay port + 1**, TCP. This game runs as no Steam game server at
all, so there is **no A2S responder and no query port**; this endpoint is the A2S replacement and
the panel's query companion is its intended caller.

## Auth

| Route | Auth |
|---|---|
| `GET /api/v1/status` | **None, by design.** It exposes nothing secret, and the firewall scopes the port to loopback and the web server. |
| `POST /api/v1/save` | `X-Driftwood-Auth: <token>`, compared in constant time. |

**Every mutating route requires the header.** Do not relax the firewall on the assumption that the
API authenticates — half of it deliberately does not.

## `GET /api/v1/status` -> `200`

```json
{
  "players": 0,
  "gameVersion": "1.0.4",
  "pluginVersion": "0.1.0",
  "phase": "Hosting",
  "reason": "Hosting \"Driftwood\" on port 22003",
  "worldRunning": true,
  "bootAssertionsPassed": true,
  "port": 22003,
  "slots": 8,
  "world": "Driftwood",
  "roster": []
}
```

### `players`: the unknown-versus-zero rule

**`-1` means UNKNOWN. It is never coerced to `0`.**

Zero is what marks a server empty, and an empty server gets reaped. A server that is still loading,
wedged, or whose world is not running has an *unknown* population, not an empty one, and reporting
zero for it hands the empty-server reaper a live customer.

- `worldRunning: true` -> `players` is the real count, and `0` is then an honest answer.
- anything else -> `players` is `-1`.

Callers must propagate unknown as unknown. `stopempty.php` already initialises its own count to
`-1` and skips unknown explicitly; anything new must do the same.

### `gameVersion`

Truncated to 45 characters, because `gameservers.reported_version` is `varchar(45)` and truncates
silently **on the way in**, where the version cron's detector cannot see that it happened.

## `POST /api/v1/save` -> `200 {"saved": true}`

Runs the game's own save routine synchronously and answers when it has run. `401` without a valid
token, `405` on a non-POST, `500` if the save routine could not be invoked.

**Stop and restart must call save, then snapshot, in that order.** A forced kill skips the game's
own quit-time save entirely, and a snapshot taken before the flush captures the stale file the
flush was meant to replace — silently, with a valid-looking archive.
