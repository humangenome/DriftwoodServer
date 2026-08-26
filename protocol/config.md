# The host mod's configuration file

`<game dir>\BepInEx\config\com.humangenome.driftwood.host.cfg`

Written by the panel (through `DriftwoodServer`), read by `DriftwoodHost`. **The plugin id IS the
filename**, so if the two sides disagree about the id the file is simply never read: no error, no
warning, and every setting silently falls back to a built-in default — the wrong port, the wrong
slot count, and no save redirect.

## Sections are part of the contract, not decoration

An earlier reader ignored section headers and matched keys flat. That looked tolerant and was
actually a **structural disagreement** with the side that writes the file. Three live breaks came
out of it, all silent:

| Break | Consequence |
|---|---|
| `[Http] Password` collapsed onto `[Server] Password` | the API token was always empty, so **every authenticated call was rejected** — and the panel and the query companion both depend on that surface |
| `Port` and `BindAddress` exist under more than one section | the game port and the status port silently became one value |
| `[World] Name` was never read at all | the server loaded the wrong world |

**Lookup rule:** a setting's section-qualified names are tried first, in order; a bare key is
accepted only as a last resort and **can never outrank a sectioned match**. Any key that matched
nothing at all is collected, logged as IGNORED at startup, and published in
`unrecognisedConfigKeys` in the readiness document — so a future disagreement is loud at three
levels instead of silent at one.

## The file

```ini
[Server]
Enabled = true
BindAddress = 0.0.0.0          ; EMPTY binds LOOPBACK - unreachable, and the log looks healthy
Port = 22003                   ; fleet band 22003, stride 10. NOT 7777 (nine games default there)
MaxPlayers = 8                 ; SOLD slots. The transport is configured with MaxPlayers + 1
StartDelaySeconds = 10
WorldReadyTimeoutSeconds = 240
ServerName = ...
SaveRoot = <instance root>\Saves   ; REQUIRED - see below
MuteAudio = true
HostMode = true                ; a locked invariant; false is warned about and ignored
CountHostPlayer = false
SuppressGhostHost = true
TargetFrameRate = 0            ; 0 = uncapped. Worth nothing on this game (see the resource profile)

[Http]
Port = 22004                   ; gameplay port + 1
Password = <token>             ; the API token for every mutating route. NOT the join password.

[Identity]
SteamWebApiKeyFile = <path>    ; OPTIONAL. A file holding a Steam Web API key (32 hex chars).
                               ; With it the host resolves player SteamIDs to real persona names
                               ; over ISteamUser/GetPlayerSummaries - a headless box has no Steam
                               ; client, and the game never puts names on the wire. Without it
                               ; the roster shows stable synthetic placeholders and everything
                               ; else works. A hosting provider points this at a machine-level
                               ; secrets file OUTSIDE the customer's FTP jail; the key must never
                               ; sit in a customer-readable tree.
SteamWebApiKey = <key>         ; OPTIONAL, inline form of the same - a self-hoster's convenience
                               ; (their box, their config file). The file form wins when both set.

[Discord]
WebhookUrl = <url>             ; OPTIONAL. Joins/leaves, boss kills, island moves and blocked-join
                               ; attempts are posted to this Discord webhook. Unset = alerts off,
                               ; everything else works. The customer's own file
                               ; <instance root>\Driftwood\discord-webhook.txt OUTRANKS this key
                               ; (first non-empty, non-# line is the URL), because a hosting panel
                               ; rewrites this config on every start and would erase a hand-added
                               ; value. Only a genuine Discord webhook URL is accepted (https, a
                               ; discord.com / discordapp.com host, an /api/webhooks/ path) - the
                               ; value can be customer-typed, and anything looser is an outbound
                               ; request-forgery primitive. Read at boot; changes take a restart.
AlertJoinLeave = true          ; OPTIONAL, each defaults to true. Which alerts the webhook
AlertBoss = true               ; receives: joins and leaves, boss kills, island moves, and
AlertIsland = true             ; blocked players who tried to come back. A missing flag is
AlertBlocked = true            ; the default - a missing alert is a missing courtesy, never a
                               ; missing safety, so nothing here fails closed.

[Chat]
PlayerCommands = true          ; OPTIONAL, default on. Players may type !help, !stuck, !playtime
                               ; and !top into the game's own chat; the host answers with a
                               ; [Server] line. Nothing to install. false = the lines are
                               ; ordinary chat again.
StuckCooldownSeconds = 60      ; OPTIONAL. Per-player cooldown on the !stuck teleport (the
                               ; owner's `rescue` console command has none). 0 = no cooldown.

[Leaderboard]
Enabled = true                 ; OPTIONAL, default on. The per-player catch leaderboard
                               ; (catches, earnings, bosses, playtime), kept at
                               ; <SaveRoot>\<World.Name>.leaderboard.tsv beside the world save
                               ; and published on /status as `leaderboard`. Counts identified
                               ; players only (the client's identity claim); a player on a
                               ; synthetic per-connection id gets no row rather than a row
                               ; that would migrate to whoever lands that slot next.

[World]
Name = Driftwood               ; a FILENAME: the save is <SaveRoot>\<Name>.txt. Never "local".
AutoSaveMinutes = 5            ; the game clamps 1-60

[Gameplay]
FriendlyFire = true            ; the game's own default; NOT stored in the save, so re-applied every start
OneShotKills = false           ; likewise

[Performance]
PauseWorldWhenEmpty = false    ; lever 3, off until resumption is proven
PhysicsStepSeconds = 0         ; 0 = leave the game's value (0.02). Not free: it is simulation fidelity
NetworkTickRate = 0            ; 0 = leave the game's value. Not free: it is how often the world reaches players

[Paths]
StateDirectory = <instance root>\host-state
InstanceRoot = <instance root>
```

## `SaveRoot` is required, and there is no safe default

Unity's `persistentDataPath` is **per Windows user, not per instance**, and no launch flag moves it
— Unity resolves LocalLow through the known-folder API, which does not read the environment. On a
fleet host every instance runs as the same account, so an unset `SaveRoot` means **every customer's
world pools into one directory** and two servers read and write each other's saves. Silently.

So there is no default: a missing `SaveRoot` is a refusal to host.

## `InstanceRoot` vs the game dir

```
instance root : <...>\<dirid>                  <- Saves\, Logs\, host-state\
game dir      : <...>\<dirid>\How to Fish      <- the executable; SteamCMD owns this
```

The install **nests**. Saves and the boot markers live under the instance root, **outside** the game
dir, deliberately — a SteamCMD validate must never be able to own or delete them.

If `InstanceRoot` is absent the mod uses the parent of the game dir, which is correct for the
standard layout. It is written explicitly anyway, because without a resolvable `Logs\` the mod
cannot write the markers the panel asserts on, and **every server would report Stopped**.
