param([int]$Count = 1)
$ErrorActionPreference = 'Stop'
$src  = 'C:\htftest\How to Fish'
$root = 'C:\driftbench'
New-Item -ItemType Directory -Force -Path $root | Out-Null

for ($i = 1; $i -le $Count; $i++) {
  $dst = Join-Path $root "i$i"
  if (Test-Path (Join-Path $dst 'DriftBench.exe')) { Write-Output "i$i already provisioned"; continue }
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  # /XD skips the burst debug folder; nothing at runtime reads it.
  robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /XD 'How to Fish_BurstDebugInformation_DoNotShip' 'plugins' | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed for i$i with code $LASTEXITCODE" }
  # Rename the executable AND its data folder so no name-matched kill can cross between lanes in
  # either direction (playbook 2b measurement discipline). Unity resolves <exebasename>_Data.
  Rename-Item (Join-Path $dst 'How to Fish.exe') 'DriftBench.exe'
  Rename-Item (Join-Path $dst 'How to Fish_Data') 'DriftBench_Data'
  New-Item -ItemType Directory -Force -Path (Join-Path $dst 'BepInEx\plugins') | Out-Null
  Write-Output "i$i provisioned"
}
Get-ChildItem $root | ForEach-Object { Write-Output ("  " + $_.Name) }
