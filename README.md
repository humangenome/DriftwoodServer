# DriftwoodServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#what-you-need)
[![Game](https://img.shields.io/badge/Game-How_to_Fish-4aa3df.svg)](https://store.steampowered.com/app/4001890/)

DriftwoodServer runs an always-on dedicated server for
[How to Fish](https://store.steampowered.com/app/4001890/). The game does not ship one; this
package adds one. The world lives on the server and keeps going whether or not anyone is online,
and everyone joins it with the [Driftwood app](https://github.com/HumanGenome/Driftwood), you
included.

The rest of this page sets one up on a Windows machine you control, start to finish. What changed
in each release is in [CHANGELOG.md](CHANGELOG.md).

## What you need

- Windows 10 / 11 / Server, x64
- Your own Steam copy of How to Fish, installed. The server runs the game's files, and this repo
  ships none of them.
- [Git](https://git-scm.com/downloads) and the
  [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), both free, used once in step 4
- Two open ports: one UDP port for the game, plus the TCP port directly above it

## 1. Make the folders

```
C:\Driftwood\my-server\
  How to Fish\        your copy of the game's files
  Saves\              the world lives here
  host-state\         status, logs, the stop file
  Backups\
  bin\                the supervisor
  appsettings.json
```

The world, the logs and the state live outside the game folder, so nothing that replaces game
files can touch your world. (`appsettings.json` is created in step 5.)

## 2. Copy the game

Copy your installed game folder, by default
`C:\Program Files (x86)\Steam\steamapps\common\How to Fish`, to
`C:\Driftwood\my-server\How to Fish`. Copy it; never point the server at the Steam install
itself. Steam updates the original whenever it likes, and a server should take a game update when
you decide to (step 5 makes that a checked decision instead of an accident).

Optional: also copy `steamapps\appmanifest_4001890.acf` into `C:\Driftwood\my-server\steamapps\`.
The build check in step 5 then reads Steam's own install record too.

## 3. Add the server files to the game

Download `DriftwoodServer-<version>.zip` from the
[latest release](https://github.com/HumanGenome/DriftwoodServer/releases/latest) and extract it
somewhere temporary, not into the game folder. Everything in the zip nests under
`DriftwoodServer\bepinex\`, and extracted straight into the game folder nothing loads: the game
boots vanilla, silently, with no error anywhere.

Copy the pieces in from where you extracted them:

```powershell
$overlay = "C:\Driftwood\overlay\DriftwoodServer\bepinex"   # wherever you extracted it
$game    = "C:\Driftwood\my-server\How to Fish"
Copy-Item "$overlay\winhttp.dll"         $game
Copy-Item "$overlay\doorstop_config.ini" $game
Copy-Item "$overlay\BepInEx"             $game -Recurse -Force
```

Done when `winhttp.dll` and `doorstop_config.ini` sit next to `How to Fish.exe`, and
`BepInEx\plugins\DriftwoodHost.dll` exists.

## 4. Build the supervisor

The supervisor is the program that starts the server, watches it, and takes the backups. The
release zip does not include it; build it from this repo with two commands:

```powershell
git clone https://github.com/HumanGenome/DriftwoodServer.git
dotnet publish DriftwoodServer/src/DriftwoodServer/DriftwoodServer.csproj -c Release -o C:\Driftwood\my-server\bin
```

## 5. Fill in the config, and pin the build

Copy [`appsettings.example.json`](appsettings.example.json) from the folder you just cloned to
`C:\Driftwood\my-server\appsettings.json`, open it in any text editor, and set:

- every path to your layout from step 1 (`instanceId` is any short name, e.g. `my-server`)
- `steamAppsDirectory`: your `steamapps` copy from step 2, or `""` if you skipped it
- `serverName`: what players see in the Driftwood app
- `authToken`: the admin password for this server's console and backups in the Driftwood app.
  Set one; without it every admin route refuses.
- `gamePort`: the UDP port players connect to. `httpPort` stays `0`: the status/admin API then
  takes the port directly above `gamePort` on its own.

Leave `worldName` alone once the server has run. Renaming it later starts a fresh world under the
new name; the old world stays in `Saves\` under the old one.

Last, pin the game build. The server refuses to start until you pin the build you copied, and
that is what stops a Steam update from silently breaking your server. Set
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

When Steam updates the game: update your Steam copy, re-copy the game folder, re-run
`--verify-build`, and paste in the new values. If the update moved something the server depends
on, it names what moved and refuses to host broken; take the next release of this package.

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

Two lines mean it worked. First `BUILD_PIN_OK`, then, after the world loads (give a fresh world a
few minutes):

```
DRIFTWOOD_HOSTING port=22003 slots=8 world=Driftwood pid=4242
```

Everyone joins from the Driftwood app: add `<your address>:22003`, **Connect**.

## 8. Stop, back up, keep it running

- **Stop:** `Ctrl+C` in the console, or create a file named `stop.requested` in `host-state\`.
  Either way the world is saved before the process exits.
- **Back up, while it runs:** `DriftwoodServer.exe --snapshot appsettings.json` saves the world
  and zips it into `Backups\`.
- **It will not start?** Read `host-state\host-ready.json`. `reason` is one plain sentence, and
  the common ones are decoded in [docs/operations.md](docs/operations.md). The game's own output
  is in `host-state\unity.log`.
- **Survive reboots:** a Task Scheduler task, *At startup*, running the step 7 command.
- **More servers on the same box:** repeat from step 1 in a second directory with its own ports,
  ten apart (22013, 22023, ...).

## Official hosting

DriftwoodServer is officially supported by
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/how_to_fish/?utm_source=github&utm_medium=readme&utm_campaign=driftwoodserver),
which runs How to Fish servers with Driftwood installed and kept on the latest pinned
release.

## License

[MIT](LICENSE) © HumanGenome
