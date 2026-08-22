# Boot markers

Two files the host mod writes into `<instance root>\Logs\` on every start. The panel reads them
back and reports the server **stopped** when either is absent or disagrees, so a half-applied start
fails closed instead of running as a healthy-looking server.

**They live in `Logs\`, not `Saves\`.** `Saves\` is the customer's FTP jail; a marker a customer
can write or delete proves nothing.

Both are deleted at the start of every boot, so a stale marker from a previous run can never be
read as a pass for this one.

## `.driftwood-saveroot`

One line: the absolute save directory the mod **actually resolved**, not the one it was asked for.

```
C:/driftbench/i1/Saves
```

### Why this file exists

Unity's `persistentDataPath` is per **Windows user**, not per instance, and no launch flag moves it
— Unity resolves LocalLow through the known-folder API, which does not read the environment. On a
fleet host every instance runs as the same account, so without a redirect **every customer's world
pools into one directory** and two servers read and write each other's saves. Nothing errors; the
log looks perfectly healthy.

The mod redirects the save root by rewriting the game's own static save-folder fields before first
use, reads the value back before believing it, and writes this marker. The panel compares.

## `.driftwood-guards`

One installed guard per line, `Type.Method`:

```
Steamworks.SteamUser.GetSteamID
Steamworks.SteamFriends.GetPersonaName
Steamworks.SteamFriends.GetFriendPersonaName
...
```

**Only guards that actually installed appear here.** A patch whose target method was not found must
never be listed — the panel reads this as the truth about what is in force, and the list is built
from what the patch library confirmed it patched, not from what was requested.

`SteamFriends.GetPersonaName` and `SteamFriends.GetFriendPersonaName` are **required**. Without
them the player-spawn path throws, the throw escapes into the netcode's shared spawn loop, and
every object queued behind it is left registered but never initialised — so the socket stays up and
no player can ever appear. **A missing marker is a failed start, not a pass.**
