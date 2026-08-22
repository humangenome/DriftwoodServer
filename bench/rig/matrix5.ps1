$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix5.log'
Set-Content -Path $m -Value ("MATRIX5 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }

# Wait for the slot proof so nothing overlaps a CPU window.
for ($i = 0; $i -lt 500; $i++) {
  if ((Get-Content 'C:\driftbench\samples\slotproof2.log' -EA SilentlyContinue) -match 'SLOTPROOF2_DONE') { break }
  Start-Sleep -Seconds 20
}
Note 'slot proof done'

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 5
Copy-Item 'C:\driftbench\next\DriftwoodHost.dll' 'C:\driftbench\DriftwoodHost.dll' -Force
Note 'swapped in the build with the real frame limiter'

# THE FRAME CAP, MEASURED FOR REAL. The earlier A/B compared two uncapped runs because
# Application.targetFrameRate is ignored in batch mode; this build pads the frame by hand, and
# actualFrameRate in the readiness file proves whether the cap is in force.
foreach ($fps in @(0, 30, 15)) {
  $tag = if ($fps -eq 0) { 'K-uncapped' } else { "K-cap$fps" }
  Note ("--> " + $tag)
  & C:\profile.ps1 -Tag $tag -Instances 1 -Fps $fps -Warmup 180 -Window 420
  $j = Get-Content 'C:\driftbench\i1\driftwood-state\host-ready.json' -Raw -EA SilentlyContinue | ConvertFrom-Json
  if ($j) { Note ("    asked=" + $fps + " actualFps=" + [math]::Round($j.actualFrameRate,1) + " frameMs=" + [math]::Round($j.frameTimeMeanMs,2) + " p95=" + [math]::Round($j.frameTimeP95Ms,2)) }
  Note ("<-- " + $tag)
  Start-Sleep -Seconds 10
}
Add-Content -Path $m -Value "MATRIX5_DONE" -Encoding UTF8
