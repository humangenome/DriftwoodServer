param(
  [int]$Instance = 1,
  [int]$Port = 0,
  [int]$Slots = 8,
  [int]$Fps = 30,
  [string]$World = 'Driftwood',
  [string]$Suppress = 'true',
  [string]$PhysicsStep = '0',
  [int]$TickRate = 0,
  [string]$PauseEmpty = 'false'
)
$ErrorActionPreference = 'Stop'
# Fleet band: 22003, stride 10. NOT 7777 - nine games on this fleet already default to 7777.
if ($Port -le 0) { $Port = 22003 + (($Instance - 1) * 10) }
$dst   = "C:\driftbench\i$Instance"
$state = "$dst\driftwood-state"
$saves = "$dst\Saves"
$logs  = "$dst\Logs"
New-Item -ItemType Directory -Force -Path $state, $saves, $logs, "$dst\BepInEx\plugins", "$dst\BepInEx\config" | Out-Null
Copy-Item 'C:\driftbench\DriftwoodHost.dll' -Destination "$dst\BepInEx\plugins\DriftwoodHost.dll" -Force

$cfg = @"
[Server]
Enabled = true
BindAddress = 0.0.0.0
Port = $Port
HttpPort = $($Port + 1)
MaxPlayers = $Slots
StartDelaySeconds = 10
WorldReadyTimeoutSeconds = 240
ServerName = DriftBench $Instance
AuthToken = benchtoken$Instance
SaveRoot = $saves
MuteAudio = true
HostMode = true
CountHostPlayer = false
SuppressGhostHost = $Suppress
TargetFrameRate = $Fps
PhysicsStepSeconds = $PhysicsStep
PauseWorldWhenEmpty = $PauseEmpty
NetworkTickRate = $TickRate

[World]
WorldName = $World
AutoSaveMinutes = 5

[Gameplay]
FriendlyFire = true
OneShotKills = false

[Paths]
StateDirectory = $state
InstanceRoot = $dst
"@
# The plugin id IS the config filename, so a rename leaves an orphan behind. Remove ANY Driftwood
# host config that is not the current one, so a stale file can never be mistaken for the live one.
Get-ChildItem "$dst\BepInEx\config" -Filter '*.driftwood.host.cfg' -EA SilentlyContinue |
  Where-Object { $_.Name -ne 'com.humangenome.driftwood.host.cfg' } |
  ForEach-Object { Remove-Item $_.FullName -Force -EA SilentlyContinue }
Set-Content -Path "$dst\BepInEx\config\com.humangenome.driftwood.host.cfg" -Value $cfg -Encoding UTF8
Write-Output "deployed i$Instance port=$Port http=$($Port+1) slots=$Slots fps=$Fps physics=$PhysicsStep tick=$TickRate pauseEmpty=$PauseEmpty suppress=$Suppress world=$World"
