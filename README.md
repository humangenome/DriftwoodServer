<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/HumanGenome/Driftwood/main/docs/img/driftwood-logo-dark.png">
    <img src="https://raw.githubusercontent.com/HumanGenome/Driftwood/main/docs/img/driftwood-logo-light.png" alt="Driftwood" width="460">
  </picture>
</p>

<p align="center">
  <a href="#what-you-need"><img src="https://img.shields.io/badge/Platform-Windows_x64-blue.svg" alt="Platform"></a>
  <a href="https://store.steampowered.com/app/4001890/"><img src="https://img.shields.io/badge/Game-How_to_Fish-4aa3df.svg" alt="Game"></a>
  <a href="https://github.com/HumanGenome/Driftwood"><img src="https://img.shields.io/badge/Player_App-Driftwood-444.svg" alt="Player App"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/8.0"><img src="https://img.shields.io/badge/.NET-8.0-blueviolet.svg" alt=".NET 8"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT"></a>
</p>

# DriftwoodServer

DriftwoodServer runs a How to Fish dedicated server on Windows. Players join with the [Driftwood app](https://github.com/HumanGenome/Driftwood), or with its DriftwoodConnect mod from [Thunderstore](https://thunderstore.io/c/how-to-fish/p/HumanGenome/DriftwoodConnect/).

How to Fish has no dedicated server of its own: its co-op runs in a Steam lobby on one player's PC. DriftwoodServer runs the game headless on a Windows machine with a public IP and port. Every player needs their own Steam copy of the game.

## What it does

### 🧭 Join by IP
Players add your server's address in the Driftwood app and press **Connect**, or type it into How to Fish's own **Join Game** box. Each player loads the character saved under their own Steam account, whatever order the crew joins in.

### 💾 World backups
`--snapshot` saves the world and zips it while the server runs. From the app's World window, the owner takes, downloads and restores snapshots and uploads a save from their PC. A restore is put in place at the next start, before the world loads, so the running game cannot write the old world back over it.

### 🖥️ Server console
The owner connects the app's Console tab with the server's admin password: status, players, saves and snapshots, kick and block, a `[Server]` line in everyone's chat, the crew's shared wallet, moving the crew to another island, and `rescue` for a stuck player. Every console call is signed with that password.

### 💬 Chat commands and a catch leaderboard
Players type `!help`, `!stuck` (back to the island spawn), `!playtime` and `!top` into the game's own chat. The leaderboard credits each sale at the sell box to the player who caught the fish and keeps catches, earnings, bosses and playtime.

### 🔔 Discord alerts
Point the server at a Discord webhook and it posts joins and leaves, boss kills, island moves and blocked join attempts.

### 🛡️ Game update check
The server starts only on the game build you pinned. If a game update moved something the server depends on, it refuses to host and names what moved, instead of starting a server nobody can join.

### ⏸️ Pause when empty
Optional (`pauseWorldWhenEmpty`). With nobody on, the world stands still and the server uses much less CPU. The world is saved first, and the first player to join wakes it.

## What you need

- Windows 10, 11 or Server, x64
- Your own Steam copy of How to Fish, installed. The server runs the game's files, and this repo ships none of them. Release 0.1.13 hosts game 1.1.x (tested on 1.1.3); releases before 0.1.9 do not start on 1.1.x.
- [Git](https://git-scm.com/downloads) and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), both free, used once in step 4
- Two open ports: one UDP port for the game, plus the TCP port directly above it

## Set it up

### 1. Make the folders

```
C:\Driftwood\my-server\
  How to Fish\        your copy of the game's files
  Saves\              the world lives here
  host-state\         status, logs, the stop file
  Backups\
  bin\                the supervisor
  appsettings.json
```

The world, the logs and the state live outside the game folder, so nothing that replaces game files can touch your world. (`appsettings.json` is created in step 5.)

### 2. Copy the game

Copy your installed game folder, by default `C:\Program Files (x86)\Steam\steamapps\common\How to Fish`, to `C:\Driftwood\my-server\How to Fish`. Copy it; never point the server at the Steam install itself. Steam updates the original whenever it likes, and a server should take a game update when you decide to (step 5 makes that a checked decision instead of an accident).

Optional: also copy `steamapps\appmanifest_4001890.acf` into `C:\Driftwood\my-server\steamapps\`. The build check in step 5 then reads Steam's own install record too.

### 3. Add the server files to the game

Download `DriftwoodServer-<version>.zip` from the [latest release](https://github.com/HumanGenome/DriftwoodServer/releases/latest) and extract it somewhere temporary, not into the game folder. Everything in the zip nests under `DriftwoodServer\bepinex\`, and extracted straight into the game folder nothing loads: the game boots vanilla, silently, with no error anywhere.

Copy the pieces in from where you extracted them:

```powershell
$overlay = "C:\Driftwood\overlay\DriftwoodServer\bepinex"   # wherever you extracted it
$game    = "C:\Driftwood\my-server\How to Fish"
Copy-Item "$overlay\winhttp.dll"         $game
Copy-Item "$overlay\doorstop_config.ini" $game
Copy-Item "$overlay\BepInEx"             $game -Recurse -Force
```

Done when `winhttp.dll` and `doorstop_config.ini` sit next to `How to Fish.exe`, and `BepInEx\plugins\DriftwoodHost.dll` exists.

### 4. Build the supervisor

The supervisor is the program that starts the server, watches it, and takes the backups. The release zip does not include it; build it from this repo with two commands:

```powershell
git clone https://github.com/HumanGenome/DriftwoodServer.git
dotnet publish DriftwoodServer/src/DriftwoodServer/DriftwoodServer.csproj -c Release -o C:\Driftwood\my-server\bin
```

### 5. Fill in the config and pin the build

Copy [`appsettings.example.json`](appsettings.example.json) from the folder you just cloned to `C:\Driftwood\my-server\appsettings.json`, open it in any text editor, and set:

- every path to your layout from step 1 (`instanceId` is any short name, e.g. `my-server`)
- `steamAppsDirectory`: your `steamapps` copy from step 2, or `""` if you skipped it
- `serverName`: what players see in the Driftwood app
- `authToken`: the admin password for this server's console and backups in the Driftwood app. Set one; without it every admin route refuses.
- `gamePort`: the UDP port players connect to. `httpPort` stays `0`: the status and admin port then takes the port directly above `gamePort` on its own.
- `discordWebhookUrl` (optional): see [Settings](#settings).

Leave `worldName` alone once the server has run. Renaming it later starts a fresh world under the new name; the old world stays in `Saves\` under the old one.

Last, pin the game build. The server refuses to start until you pin the build you copied, and that is what stops a Steam update from silently breaking your server. Set `pinnedBuild.assemblySha256` to 64 zeros, then ask the install for its real values:

```powershell
C:\Driftwood\my-server\bin\DriftwoodServer.exe --verify-build C:\Driftwood\my-server\appsettings.json
```

It prints `BUILD_PIN_FAILED` plus the truth:

```
assemblySha256=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
steamBuildId=19842631
```

Paste both into `pinnedBuild` (`buildId` stays `""` if you skipped the steamapps copy), run it again, and expect `BUILD_PIN_OK`.

When Steam updates the game: update your Steam copy, copy the game folder again, run `--verify-build` again, and paste in the new values. If the update moved something the server depends on, it names what moved and refuses to host; take the next release of this package.

### 6. Open the ports

```powershell
New-NetFirewallRule -DisplayName "Driftwood game" -Direction Inbound -Protocol UDP -LocalPort 22003 -Action Allow
New-NetFirewallRule -DisplayName "Driftwood api"  -Direction Inbound -Protocol TCP -LocalPort 22004 -Action Allow
```

Use your `gamePort` and `gamePort + 1` (see [Ports](#ports)). Hosting from home, forward both on your router too.

### 7. Start it

```powershell
C:\Driftwood\my-server\bin\DriftwoodServer.exe --config C:\Driftwood\my-server\appsettings.json
```

Two lines mean it worked. First `BUILD_PIN_OK`, then, after the world loads (give a fresh world a few minutes):

```
DRIFTWOOD_HOSTING port=22003 slots=8 world=Driftwood pid=4242
```

Everyone joins from the Driftwood app: add `<your address>:22003`, then **Connect**. After a player's first Connect, the same address also works in How to Fish's own **Join Game** box. Players with the DriftwoodConnect mod from Thunderstore use that box straight away.

### 8. Stop, back up, keep it running

- **Stop:** `Ctrl+C` in the console, or create a file named `stop.requested` in `host-state\`. Either way the world is saved before the process exits.
- **Back up, while it runs:** `DriftwoodServer.exe --snapshot appsettings.json` saves the world and zips it into `Backups\`.
- **It will not start?** Read `host-state\status.json`. `reason` is one plain sentence, and the common ones are decoded in [docs/operations.md](docs/operations.md). The game's own output is in `host-state\unity.log`.
- **Survive reboots:** a Task Scheduler task, *At startup*, running the step 7 command.
- **After a crash:** the supervisor writes "This server stopped unexpectedly" to `status.json` and exits; it does not start the game again by itself. Run the step 7 command again.
- **More servers on the same machine:** repeat from step 1 in a second folder with its own ports, ten apart (22013, 22023, ...).

## Ports

| Port | Protocol | What uses it |
|---|---|---|
| `gamePort` (default 22003) | UDP | the game: players connect here |
| `gamePort + 1` | TCP | status and admin: the Driftwood app reads who is on here, and the owner's Console tab and World window connect here |

Keep 22003 and its neighbours rather than 7777: many games default to 7777, and two servers on one port fail to start.

## Settings

The settings you can change, in `appsettings.json`. The supervisor writes the host's own config file from these on every start, so a hand edit there does not last; [protocol/config.md](protocol/config.md) lists every key the host reads.

| Setting | Default | What it does |
|---|---|---|
| `serverName` | empty | The name players see in the Driftwood app |
| `authToken` | empty | The admin password for the Console tab and World window. Empty refuses every admin call |
| `gamePort` | `22003` | The UDP port players connect to |
| `slots` | `8` | How many players fit. A full server turns the next player away. How to Fish's Steam page says 1-4 players |
| `worldName` | `Driftwood` | The world's file name in `Saves\`. A new name starts a new world |
| `autoSaveMinutes` | `5` | How often the game saves the world |
| `friendlyFire` | `true` | Players can hurt each other, as in the game's own default |
| `oneShotKills` | `false` | The game's own One Shot option |
| `pauseWorldWhenEmpty` | `false` | Stand the world still while nobody is on (see [Pause when empty](#%EF%B8%8F-pause-when-empty)) |
| `discordWebhookUrl` | empty | A Discord webhook for joins and leaves, boss kills, island moves and blocked join attempts. A file named `discord-webhook.txt` in `C:\Driftwood\my-server\Driftwood\` does the same and wins when both are set |
| `pinnedBuild` | | The game build this server accepts (step 5) |

## Rent a server

[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/how_to_fish/?utm_source=github&utm_medium=readme_install&utm_campaign=driftwoodserver) runs How to Fish servers with Driftwood installed and the ports open.

## Build from source

The supervisor builds with the .NET 8 SDK (step 4). The tests:

```powershell
dotnet test src/DriftwoodServer.Tests/DriftwoodServer.Tests.csproj -c Release
```

The host mod (`host-mod/DriftwoodHost`) compiles against the game's own assemblies, which are never committed. Point `-p:ManagedDir=` at your install's `How to Fish_Data\Managed`. `tools/ci/check-game-build.sh` compiles the host against one or more game builds and checks every game member it uses still exists on each; run it before trusting a build on a new game version. `scripts/package-server-overlay.sh` builds the release zip and refuses to package unless that check passes.

## Releases

Each [release](https://github.com/HumanGenome/DriftwoodServer/releases) carries `DriftwoodServer-<version>.zip`, the files step 3 copies into the game, and notes on what changed. From 0.1.12 a release also carries `SHA256SUMS.txt`, the zip's SHA-256, and `build-check.txt`, the game builds the host was checked against. The full history is in [CHANGELOG.md](CHANGELOG.md). The player app's releases are on the [Driftwood repo](https://github.com/HumanGenome/Driftwood/releases).

## Contributing

Bug reports go to [this repo's issues](https://github.com/HumanGenome/DriftwoodServer/issues), with the server version, the game build and the `reason` from `host-ready.json` if it will not start. See [CONTRIBUTING.md](CONTRIBUTING.md) before a pull request. Security issues go through [private reporting](.github/SECURITY.md), never a public issue.

## Community note

Driftwood is a community project and is not affiliated with, endorsed by, or supported by Dazed Games. How to Fish is their game: [buy it on Steam](https://store.steampowered.com/app/4001890/).

## License

[MIT](LICENSE) © HumanGenome
