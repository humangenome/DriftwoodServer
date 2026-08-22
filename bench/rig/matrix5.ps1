$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix5.log'
Set-Content -Path $m -Value ("MATRIX5 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }
function Readiness { $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'; if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json) } return $null }

for ($i = 0; $i -lt 500; $i++) {
  if ((Get-Content 'C:\driftbench\samples\slotproof2.log' -EA SilentlyContinue) -match 'SLOTPROOF2_DONE') { break }
  Start-Sleep -Seconds 20
}
Note 'slot proof done'

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 5
Copy-Item 'C:\driftbench\next\DriftwoodHost.dll' 'C:\driftbench\DriftwoodHost.dll' -Force
Copy-Item 'C:\driftbench\next\deploy.ps1'  'C:\deploy.ps1' -Force
Copy-Item 'C:\driftbench\next\profile.ps1' 'C:\profile.ps1' -Force
Note 'swapped in the build with the real frame limiter and the idle-rate lever'

# THE FRAME CAP, MEASURED FOR REAL. Application.targetFrameRate is ignored in batch mode, so the
# earlier A/B compared two uncapped runs. This build pads the frame by hand and publishes
# actualFrameRate, which is what proves the cap is in force.
foreach ($fps in @(0, 30, 15, 5)) {
  $tag = if ($fps -eq 0) { 'K-uncapped' } else { "K-cap$fps" }
  Note ("--> " + $tag)
  & C:\profile.ps1 -Tag $tag -Instances 1 -Fps $fps -Warmup 180 -Window 420
  $j = Readiness
  if ($j) { Note ("    asked=" + $fps + " actualFps=" + [math]::Round($j.actualFrameRate,1) + " frameMs=" + [math]::Round($j.frameTimeMeanMs,2) + " p95Ms=" + [math]::Round($j.frameTimeP95Ms,2)) }
  Note ("<-- " + $tag)
  Start-Sleep -Seconds 10
}

# L: the empty-server rate. 30 fps when somebody is on, 5 fps when nobody is - which is the shape
# the measurements point at, because the idle cost is the frame loop and not the simulation clock.
Note '--> L-idle5-cap30'
& C:\profile.ps1 -Tag 'L-idle5-cap30' -Instances 1 -Fps 30 -IdleFps 5 -Warmup 180 -Window 420
$j = Readiness
if ($j) { Note ("    actualFps=" + [math]::Round($j.actualFrameRate,1) + " loopIdling=" + $j.loopIdling + " idleTransitions=" + $j.idleTransitions) }
Note '<-- L-idle5-cap30'
Start-Sleep -Seconds 10

# M: the proof it comes back. Same settings, but a client joins - the loop must return to full rate
# and the player must spawn. Standing something down is only safe if you prove it comes back.
Note '--> M-idle5-then-join'
& C:\profile.ps1 -Tag 'M-idle5-then-join' -Instances 1 -Fps 30 -IdleFps 5 -Warmup 180 -Window 300 -Clients 1
$j = Readiness
if ($j) { Note ("    players=" + $j.players + " actualFps=" + [math]::Round($j.actualFrameRate,1) + " loopIdling=" + $j.loopIdling + " idleTransitions=" + $j.idleTransitions) }
$bl = 'C:\driftbench\c1\BepInEx\LogOutput.log'
if (Test-Path $bl) { (Get-Content $bl | Select-String -SimpleMatch 'BENCH_SPAWNED','BENCH alive' | Select-Object -Last 3) | ForEach-Object { Note ("    c1: " + $_.Line) } }
Note '<-- M-idle5-then-join'

Add-Content -Path $m -Value "MATRIX5_DONE" -Encoding UTF8
