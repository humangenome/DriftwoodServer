# Self-hosting a Driftwood server

Run your own always-on How to Fish server on a Windows machine you control. Everything the server
side needs is in this repo and its [releases](https://github.com/HumanGenome/DriftwoodServer/releases) —
no hosting company required. If you would rather not run one,
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/how_to_fish/?utm_source=github&utm_medium=selfhosting&utm_campaign=driftwoodserver)
runs these for a living.

**You need:**

- Windows 10 / 11 / Server, x64
- your own Steam copy of How to Fish, installed — the server runs the game's files, and this repo
  ships none of them
- the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), used once to build the
  supervisor
- one UDP port for gameplay, plus the TCP port directly above it for status and admin

Players join with the [Driftwood app](https://github.com/HumanGenome/Driftwood), you included —
How to Fish cannot reach a dedicated server without it.

## 1. Lay out a directory

```
C:\Driftwood\my-server\
  How to Fish\        your copy of the game's files
  Saves\              the world lives here
  host-state\         status, logs, the stop file
  Backups\
  bin\                the supervisor
  appsettings.json
```

The shape is deliberate: the world, the logs and the state live **outside** the game folder, so
nothing that replaces game files — an update, a reinstall, a verify — can touch your world.

## 2. Copy the game

Copy your installed game folder — by default
`C:\Program Files (x86)\Steam\steamapps\common\How to Fish` — to
`C:\Driftwood\my-server\How to Fish`. **Copy, never point at the Steam install directly**: Steam
updates the original whenever it likes, and a server should take a game update when you decide to
(step 5 makes that a checked decision instead of an accident).

Optional: also copy `steamapps\appmanifest_4001890.acf` into `C:\Driftwood\my-server\steamapps\`.
Build verification then cross-checks Steam's own install record too, which catches a half-applied
update.

## 3. Install the server overlay

Download `DriftwoodServer-<version>.zip` from the
[latest release](https://github.com/HumanGenome/DriftwoodServer/releases/latest) and extract it
somewhere temporary — **not** into the game folder. Everything in the zip nests under
`DriftwoodServer\bepinex\`, and extracted straight into the game folder nothing loads: the game
boots vanilla, silently, with no error anywhere.

Stage it into the game folder like this:

```powershell
$overlay = "C:\Driftwood\overlay\DriftwoodServer\bepinex"   # wherever you extracted it
$game    = "C:\Driftwood\my-server\How to Fish"
Copy-Item "$overlay\winhttp.dll"         $game
Copy-Item "$overlay\doorstop_config.ini" $game
Copy-Item "$overlay\BepInEx"             $game -Recurse -Force
```

Afterwards `winhttp.dll` and `doorstop_config.ini` sit next to `How to Fish.exe`, and
`BepInEx\plugins\DriftwoodHost.dll` exists. That is the whole install — a few hundred kilobytes on
top of the game.

(Prefer to build the host mod from source? See **Building** in the [README](../README.md); the
resulting DLL replaces `BepInEx\plugins\DriftwoodHost.dll`.)

## 4. Build the supervisor

Releases currently ship the overlay only; the supervisor is one command from source:

```powershell
git clone https://github.com/HumanGenome/DriftwoodServer.git
dotnet publish DriftwoodServer/src/DriftwoodServer/DriftwoodServer.csproj -c Release -o C:\Driftwood\my-server\bin
```

Building on one machine and hosting on another that has no .NET runtime? Add
`-r win-x64 --self-contained` and copy the output folder across — same result.

## 5. Configure, and pin the build

Copy [`appsettings.example.json`](../appsettings.example.json) to
`C:\Driftwood\my-server\appsettings.json` and set:

- every path to your layout from step 1 (`instanceId` is any short name, e.g. `my-server`)
- `steamAppsDirectory` — your `steamapps` copy from step 2, or `""` if you skipped it
- `serverName` — what players see in the Driftwood app
- `authToken` — the admin password for this server's console and backups in the Driftwood app.
  Set one; without it every admin route refuses.
- `gamePort` — the UDP port players connect to. `httpPort` stays `0`: the status/admin API then
  takes the port directly above `gamePort` on its own.

The server **refuses to start until you pin the game build you validated** — that is the feature
that stops a Steam update from silently breaking your server. Bootstrap the pin: set
`pinnedBuild.assemblySha256` to 64 zeros, then ask the install for its real values:

```powershell
C:\Driftwood\my-server\bin\DriftwoodServer.exe --verify-build C:\Driftwood\my-server\appsettings.json
```

It prints `BUILD_PIN_FAILED` plus the truth:

```
assemblySha256=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
steamBuildId=19842631
```

Paste both into `pinnedBuild` (`buildId` stays `""` if you skipped the steamapps copy), run it
again, and expect `BUILD_PIN_OK`.

When Steam ships a game update: update your Steam copy, re-copy the game folder, re-run
`--verify-build`, and re-pin. If the update moved code the host mod patches, the server will name
exactly what moved and refuse rather than host broken — take the next release, or rebuild the mod
against the new build.

## 6. Open the ports

```powershell
New-NetFirewallRule -DisplayName "Driftwood game" -Direction Inbound -Protocol UDP -LocalPort 22003 -Action Allow
New-NetFirewallRule -DisplayName "Driftwood api"  -Direction Inbound -Protocol TCP -LocalPort 22004 -Action Allow
```

Use your `gamePort` and `gamePort + 1`. Hosting from home, forward both on your router too.

## 7. Start it

```powershell
C:\Driftwood\my-server\bin\DriftwoodServer.exe --config C:\Driftwood\my-server\appsettings.json
```

Two lines mean it worked — first `BUILD_PIN_OK`, then, after the world loads (give a fresh world a
minute or two):

```
DRIFTWOOD_HOSTING port=22003 slots=8 world=Driftwood pid=4242
```

Everyone joins from the Driftwood app: add `<your address>:22003`, **Connect**. From another
machine, `curl http://<your address>:22004/api/v1/health` answers as soon as the world is up.

## 8. Stop, back up, keep it running

- **Stop:** `Ctrl+C` in the console, or create a file named `stop.requested` in `host-state\`.
  Either way the world is saved before the process exits.
- **Back up, while it runs:** `DriftwoodServer.exe --snapshot appsettings.json` — tells the server
  to save, waits for the save to land, zips it into `Backups\`, then reads the archive back to
  prove the world file is really in it.
- **It will not start?** Read one file: `host-state\host-ready.json`. `reason` is one plain
  sentence, and the common ones are decoded in [operations.md](operations.md). The game's own
  output is in `host-state\unity.log`.
- **Survive reboots:** a Task Scheduler task, *At startup*, running the step 7 command. One
  supervisor per config — the state directory is locked, so a double start refuses cleanly.
- **More servers on the same box:** repeat from step 1 in a second directory with its own ports,
  ten apart (22013, 22023, ...).
