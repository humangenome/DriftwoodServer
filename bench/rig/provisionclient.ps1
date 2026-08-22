param([int]$Count = 3)
$ErrorActionPreference = 'Stop'
$src  = 'C:\htftest\How to Fish'
$root = 'C:\driftbench'
for ($i = 1; $i -le $Count; $i++) {
  $dst = Join-Path $root "c$i"
  if (Test-Path (Join-Path $dst 'DriftBenchClient.exe')) { Write-Output "c$i already provisioned"; continue }
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /XD 'How to Fish_BurstDebugInformation_DoNotShip' 'plugins' | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed for c$i with code $LASTEXITCODE" }
  Rename-Item (Join-Path $dst 'How to Fish.exe') 'DriftBenchClient.exe'
  Rename-Item (Join-Path $dst 'How to Fish_Data') 'DriftBenchClient_Data'
  New-Item -ItemType Directory -Force -Path (Join-Path $dst 'BepInEx\plugins') | Out-Null
  Write-Output "c$i provisioned"
}
