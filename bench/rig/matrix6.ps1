$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix6.log'
Set-Content -Path $m -Value ("MATRIX6 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }
function Readiness { $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'; if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json) } return $null }

for ($i = 0; $i -lt 600; $i++) {
  if ((Get-Content 'C:\driftbench\samples\reopen.log' -EA SilentlyContinue) -match 'REOPEN_DONE') { break }
  Start-Sleep -Seconds 20
}
Note 'reopen test done'

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 5
# deploy/profile with -IdleFps landed after the matrix5 task had already read its script, so they
# are swapped in here.
Copy-Item 'C:\driftbench\next\deploy.ps1'  'C:\deploy.ps1' -Force
Copy-Item 'C:\driftbench\next\profile.ps1' 'C:\profile.ps1' -Force
Copy-Item 'C:\driftbench\next\DriftwoodHost.dll' 'C:\driftbench\DriftwoodHost.dll' -Force
Note 'swapped in deploy/profile with -IdleFps and the watchdog build'

# L: the empty-server RATE lever - 5 fps while nobody is on, 30 fps with players. Distinct from the
# world freeze: this slows the loop rather than stopping the clock, and the two reach different work.
Note '--> L-idle5-cap30'
& C:\profile.ps1 -Tag 'L-idle5-cap30' -Instances 1 -Fps 30 -IdleFps 5 -Warmup 180 -Window 420
$j = Readiness
if ($j) { Note ("    actualFps=" + [math]::Round($j.actualFrameRate,1) + " loopIdling=" + $j.loopIdling + " idleTransitions=" + $j.idleTransitions) }
Note '<-- L-idle5-cap30'
Start-Sleep -Seconds 10

# N: both empty-server levers together - the loop slowed AND the clock stopped.
Note '--> N-idle5-and-pause'
& C:\profile.ps1 -Tag 'N-idle5-and-pause' -Instances 1 -Fps 30 -IdleFps 5 -PauseEmpty true -Warmup 180 -Window 420
$j = Readiness
if ($j) { Note ("    actualFps=" + [math]::Round($j.actualFrameRate,1) + " paused=" + $j.worldPaused + " loopIdling=" + $j.loopIdling) }
Note '<-- N-idle5-and-pause'
Start-Sleep -Seconds 10

# M: the proof BOTH come back. A client joins a server that is frozen AND running at 5 fps.
Note '--> M-both-then-join'
& C:\profile.ps1 -Tag 'M-both-then-join' -Instances 1 -Fps 30 -IdleFps 5 -PauseEmpty true -Warmup 180 -Window 300 -Clients 1
$j = Readiness
if ($j) { Note ("    players=" + $j.players + " actualFps=" + [math]::Round($j.actualFrameRate,1) + " paused=" + $j.worldPaused + " loopIdling=" + $j.loopIdling + " resumes=" + $j.worldResumeCount + " idleTransitions=" + $j.idleTransitions + " swallowed=" + $j.swallowedTotal) }
$bl = 'C:\driftbench\c1\BepInEx\LogOutput.log'
if (Test-Path $bl) { (Get-Content $bl | Select-String -SimpleMatch 'BENCH_SPAWNED','BENCH alive' | Select-Object -Last 3) | ForEach-Object { Note ("    c1: " + $_.Line) } }
Note '<-- M-both-then-join'

Add-Content -Path $m -Value "MATRIX6_DONE" -Encoding UTF8
