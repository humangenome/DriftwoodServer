param([int]$From = 1, [int]$To = 1, [int]$ServerPort = 7801, [string]$ServerAddress = '127.0.0.1')
$ErrorActionPreference = 'Stop'
for ($i = $From; $i -le $To; $i++) {
  $dst = "C:\driftbench\c$i"
  New-Item -ItemType Directory -Force -Path "$dst\BepInEx\plugins", "$dst\BepInEx\config" | Out-Null
  Copy-Item 'C:\driftbench\DriftwoodBenchClient.dll' -Destination "$dst\BepInEx\plugins\DriftwoodBenchClient.dll" -Force
  $cfg = @"
[Bench]
Address = $ServerAddress
Port = $ServerPort
Walk = true
TurnSeconds = 4
StartDelaySeconds = 12
"@
  Set-Content -Path "$dst\BepInEx\config\com.humangenome.driftwood.benchclient.cfg" -Value $cfg -Encoding UTF8
  Write-Output "client c$i -> ${ServerAddress}:$ServerPort"
}
