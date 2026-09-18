# How to Fish - Driftwood Installer

Installers and setup notes for running a dedicated How to Fish server with **Driftwood**.

## What game is this for?

[**How to Fish**](https://store.steampowered.com/app/4001890/) by **Dazed Games**

## What is Driftwood?

**Driftwood** is a multiplayer suite for How to Fish, made by **[HumanGenome](https://github.com/HumanGenome)**:

- **[Driftwood](https://github.com/HumanGenome/Driftwood)** (client)
- **[DriftwoodServer](https://github.com/HumanGenome/DriftwoodServer)** (server)
- **[Server Guide](https://www.survivalservers.com/wiki/How_to_Create_a_How_to_Fish_Server_Guide)** (docs)

This repository is not affiliated with Dazed Games or HumanGenome, it's a Windows installer that simplifies the installation process for Driftwood.

## What’s in this repo

| File | Purpose |
| --- | --- |
| [`automatic-installer.bat`](automatic-installer.bat) | Guided Windows installer. |
| [`manual-install.md`](manual-install.md) | Manual setup if you want full control. |

Installs Driftwood Client and a Driftwood Server under the folder you pick (default `C:\Driftwood`).

## Requirements

- Windows 10/11 x64
- [How to Fish](https://store.steampowered.com/app/4001890/) installed via Steam
- [Git](https://git-scm.com/install/)
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Administrator privileges for PowerShell

## Run

### Client

Run `Driftwood.Launcher.exe` in the `Client\` folder.

### Server

See the "Running the server" section in [`manual-install.md`](manual-install.md).

For firewall information see step 7 in the **[Server Guide](https://www.survivalservers.com/wiki/How_to_Create_a_How_to_Fish_Server_Guide)**. If you only want local LAN play it's not needed.

## Roadmap

* Add documentation for setting up firewall. Right now this only sets it up for local only (my current use-case).
* **CAUTION**: Figure out why `.dll` files from the [Driftwood Server Releases](https://github.com/humangenome/DriftwoodServer/releases/tag/v0.1.7) are being tagged as threats by antivirus software.

## Credits

- **How to Fish** by [Dazed Games](https://howtofishgame.io/)
- **Driftwood & DriftwoodServer** by [HumanGenome](https://github.com/HumanGenome)
