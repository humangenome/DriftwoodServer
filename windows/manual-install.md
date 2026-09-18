## How to Fish - Setup Guide

### Requirements

* Windows 10/11 x64
* How to Fish Game (Steam)
* Git: https://git-scm.com/install/
* .NET 8 SDK: https://dotnet.microsoft.com/en-us/download/dotnet/8.0

### Install (Client & Server)

Running PowerShell as an admin, use the following commands from top to bottom:

```powershell
# Main (set these)
$server_id = "the-island"
$server_name = "The Island"
$server_version = "0.1.7"
$game_port = 22003
$client_version = "0.1.3"
$d = "C:\Driftwood"
$d_steamapps = "E:\SteamLibrary\steamapps"

# This is the admin password (currently random)
$server_auth_token = [guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N")
# Or set manually
# $server_auth_token = "MY-SUPER-SECRET-PASSWORD-123"

# Constants (do not change)
$d_client = "$d\Client"
$d_game = "$d_steamapps\common\How to Fish\How to Fish"
$d_server = "$d\$server_id"
$d_server_game = "$d_server\How to Fish"
$d_server_steamapps = "$d_server\steamapps"
$d_tmp = "$d\.tmp"
$d_repo = "$d_tmp\repo"
$url_repo = "https://github.com/humangenome"
$url_client = "$url_repo/Driftwood/releases/download/v$client_version/Driftwood-Portable-v$client_version.zip"
$url_server = "$url_repo/DriftwoodServer/releases/download/v$server_version/DriftwoodServer-$server_version.zip"
$d_config = "$d_server\appsettings.json"

### ---------- PRELIM ---------- ###

# Create directories
New-Item -ItemType Directory -Path "$d"
New-Item -ItemType Directory -Path "$d_client"
New-Item -ItemType Directory -Path "$d_server"
New-Item -ItemType Directory -Path "$d_server_game"
New-Item -ItemType Directory -Path "$d_server_steamapps"
New-Item -ItemType Directory -Path "$d_tmp"
New-Item -ItemType Directory -Path "$d_server\Saves"
New-Item -ItemType Directory -Path "$d_server\host-state"
New-Item -ItemType Directory -Path "$d_server\Backups"
New-Item -ItemType Directory -Path "$d_server\bin"
# Verify
Test-Path "$d" -PathType Container
Test-Path "$d_client" -PathType Container
Test-Path "$d_server" -PathType Container
Test-Path "$d_server_game" -PathType Container
Test-Path "$d_server_steamapps" -PathType Container
Test-Path "$d_tmp" -PathType Container
Test-Path "$d_server\Saves" -PathType Container
Test-Path "$d_server\host-state" -PathType Container
Test-Path "$d_server\Backups" -PathType Container
Test-Path "$d_server\bin" -PathType Container
# Set current directory to install folder.
cd $d

### ---------- CLIENT ---------- ###

# Download
Invoke-WebRequest "$url_client" -OutFile "$d_tmp\Driftwood-Portable-v$client_version.zip"
# Extract
Expand-Archive -Path "$d_tmp\Driftwood-Portable-v$client_version.zip" -DestinationPath "$d_client"
# Verify
Test-Path "$d_client\Driftwood.Launcher.exe"
# Cleanup
Remove-Item "$d_tmp\Driftwood-Portable-v$client_version.zip"
# Open with `& $d_client\Driftwood.Launcher.exe`

### ---------- SERVER ---------- ###

# Copy game from Steam dir to Driftwood server dir
Copy-Item "$d_game\*" "$d_server_game" -Recurse -Force
# Verify
Test-Path "$d_server_game\How to Fish.exe"

# Copy Steam app manifest to server
Copy-Item "$d_steamapps\appmanifest_4001890.acf" "$d_server_steamapps"
# Verify
Test-Path "$d_server_steamapps\appmanifest_4001890.acf"

# Download
Invoke-WebRequest "$url_server" -OutFile "$d_tmp\DriftwoodServer-$server_version.zip"
# Extract
Expand-Archive -Path "$d_tmp\DriftwoodServer-$server_version.zip" -DestinationPath "$d_tmp"
# Verify
Test-Path "$d_tmp\DriftwoodServer" -PathType Container
# Cleanup
Remove-Item "$d_tmp\DriftwoodServer-$server_version.zip"

# Copy server files to game dir
Copy-Item "$d_tmp\DriftwoodServer\bepinex\winhttp.dll" $d_server_game
Copy-Item "$d_tmp\DriftwoodServer\bepinex\doorstop_config.ini" $d_server_game
Copy-Item "$d_tmp\DriftwoodServer\bepinex\BepInEx" $d_server_game -Recurse -Force
# Verify
Test-Path "$d_server_game\winhttp.dll"
Test-Path "$d_server_game\doorstop_config.ini"
Test-Path "$d_server_game\BepInEx\plugins\DriftwoodHost.dll"

# Build Supervisor (handles server start/stop/backups)
git clone --branch "v$server_version" --single-branch https://github.com/HumanGenome/DriftwoodServer.git "$d_repo"
# Verify
Test-Path "$d_repo\src\DriftwoodServer" -PathType Container
# Build the software bin
dotnet publish "$d_repo\src\DriftwoodServer\DriftwoodServer.csproj" -c Release -o "$d_server\bin"

# Copy the `appsettings.json` example
Copy-Item "$d_repo\appsettings.example.json" $d_config -Force
# Edit config
$config = Get-Content $d_config -Raw | ConvertFrom-Json
$config.instanceId = $server_id
$config.gameRoot = $d_server_game
$config.instanceRoot = $d_server
$config.stateRoot = "$d_server\host-state"
$config.saveRoot = "$d_server\Saves"
$config.backupRoot = "$d_server\Backups"
$config.steamAppsDirectory = $d_server_steamapps
$config.serverName = $server_name
$config.authToken = $server_auth_token
$config.gamePort = $game_port
$config.httpPort = 0
$config.pinnedBuild.assemblySha256 = "0" * 64
$config.pinnedBuild.buildId = ""
# Set (json)
$config | ConvertTo-Json -Depth 20 | Set-Content $d_config -Encoding UTF8
# Verify
Test-Path $d_config -PathType Leaf

### ---------- BUILD PIN ---------- ###

# Run initial verification (BUILD_PIN_FAILED is expected)
$verify_output = & "$d_server\bin\DriftwoodServer.exe" --verify-build "$d_config" 2>&1
# Show result
$verify_output

# Get assembly hash
$assembly_hash = (($verify_output | Select-String "^assemblySha256=").Line -replace "^assemblySha256=", "")
# Get Steam build ID
$steam_build_id = (($verify_output | Select-String "^steamBuildId=").Line -replace "^steamBuildId=", "")
# Verify
$assembly_hash.Length -eq 64
$steam_build_id.Length -gt 0

# Edit config
$config = Get-Content $d_config -Raw | ConvertFrom-Json
$config.pinnedBuild.assemblySha256 = $assembly_hash
$config.pinnedBuild.buildId = $steam_build_id
# Set (json)
$config | ConvertTo-Json -Depth 20 | Set-Content $d_config -Encoding UTF8

# Verify build (BUILD_PIN_OK is expected)
& "$d_server\bin\DriftwoodServer.exe" --verify-build "$d_config"

### ---------- CLEANUP ---------- ###

# Remove temp files
Remove-Item "$d_tmp" -Recurse -Force
# Verify
Test-Path "$d_tmp" -PathType Container
```

### Running the server

```powershell
# Main (set these)
$d = "C:\Driftwood"
$server_id = "the-island"

# Constants (do not change)
$d_server = "$d\$server_id"
$d_config = "$d_server\appsettings.json"

# Start server
& "$d_server\bin\DriftwoodServer.exe" --config "$d_config"
```
