$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix7.log'
Set-Content -Path $m -Value ("MATRIX7 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }
function Readiness { $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'; if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json) } return $null }

for ($i = 0; $i -lt 900; $i++) {
  if ((Get-Content 'C:\driftbench\samples\matrix6.log' -EA SilentlyContinue) -match 'MATRIX6_DONE') { break }
  Start-Sleep -Seconds 20
}
Note 'matrix6 done'

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 5
Copy-Item 'C:\driftbench\next\DriftwoodHost.dll' 'C:\driftbench\DriftwoodHost.dll' -Force
Note 'swapped in the build where the limiter is ACTUALLY wired up'

# THE FRAME CAP, for real this time. frameLimiterActive and actualFrameRate in the readiness
# document are the proof; the engine's own targetFrameRate is not evidence of anything.
foreach ($fps in @(0, 30, 15, 5)) {
  $tag = if ($fps -eq 0) { 'P-uncapped' } else { "P-cap$fps" }
  Note ("--> " + $tag)
  & C:\profile.ps1 -Tag $tag -Instances 1 -Fps $fps -Warmup 180 -Window 420
  $j = Readiness
  if ($j) { Note ("    asked=" + $fps + " limiterActive=" + $j.frameLimiterActive + " actualFps=" + [math]::Round($j.actualFrameRate,1) + " frameMs=" + [math]::Round($j.frameTimeMeanMs,2) + " p95Ms=" + [math]::Round($j.frameTimeP95Ms,2)) }
  Note ("<-- " + $tag)
  Start-Sleep -Seconds 10
}
Add-Content -Path $m -Value "MATRIX7_DONE" -Encoding UTF8
