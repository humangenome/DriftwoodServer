param(
  [string]$Tag,
  [int]$Instances = 1,
  [int]$Fps = 30,
  [int]$Warmup = 180,
  [int]$Window = 420,
  [string]$World = 'Driftwood',
  [string]$Suppress = 'true',
  [int]$Clients = 0,
  [string]$PhysicsStep = '0',
  [int]$TickRate = 0,
  [string]$PauseEmpty = 'false'
)
$ErrorActionPreference = 'Continue'
$log = "C:\driftbench\samples\$Tag.log"
New-Item -ItemType Directory -Force -Path 'C:\driftbench\samples' | Out-Null
"=== PROFILE $Tag started $(Get-Date -Format o) instances=$Instances fps=$Fps clients=$Clients warmup=${Warmup}s window=${Window}s ===" | Out-File $log -Encoding utf8

& C:\stopall.ps1 *>> $log
Start-Sleep -Seconds 3
for ($i = 1; $i -le $Instances; $i++) {
  & C:\deploy.ps1 -Instance $i -Slots 8 -Fps $Fps -World $World -Suppress $Suppress -PhysicsStep $PhysicsStep -TickRate $TickRate -PauseEmpty $PauseEmpty *>> $log
}
& C:\launch.ps1 -From 1 -To $Instances *>> $log

if ($Clients -gt 0) {
  # Let the server finish loading its world before any client dials in.
  Start-Sleep -Seconds 60
  & C:\deployclient.ps1 -From 1 -To $Clients -ServerPort 22003 *>> $log
  & C:\launchclient.ps1 -From 1 -To $Clients *>> $log
}

& C:\sample.ps1 -Tag $Tag -Seconds $Window -WarmupSeconds $Warmup -IntervalMs 2000 *>> $log
"=== readiness ===" | Out-File $log -Append -Encoding utf8
& C:\readystate.ps1 -From 1 -To $Instances *>> $log
for ($i = 1; $i -le $Instances; $i++) {
  $ul = "C:\driftbench\i$i\unity.log"
  if (Test-Path $ul) { ("i$i unityLogLines=" + (Get-Content $ul).Count) | Out-File $log -Append -Encoding utf8 }
}
if ($Clients -gt 0) {
  for ($c = 1; $c -le $Clients; $c++) {
    $bl = "C:\driftbench\c$c\BepInEx\LogOutput.log"
    if (Test-Path $bl) { (Get-Content $bl | Select-String 'BENCH' | Select-Object -Last 3) | Out-File $log -Append -Encoding utf8 }
  }
}
& C:\stopall.ps1 *>> $log
"=== PROFILE_DONE $Tag ===" | Out-File $log -Append -Encoding utf8
