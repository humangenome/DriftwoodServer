param([int]$From=1,[int]$To=1)
for ($i=$From; $i -le $To; $i++) {
  $f = "C:\driftbench\i$i\driftwood-state\host-ready.json"
  if (Test-Path $f) {
    $j = Get-Content $f -Raw | ConvertFrom-Json
    Write-Output ("i$i phase=" + $j.phase + " worldRunning=" + $j.worldRunning + " port=" + $j.port + " players=" + $j.players + " maxClients=" + $j.transportMaxClients + " swallowed=" + $j.swallowedTotal + " fps=" + $j.effectiveTargetFrameRate + " reason=" + $j.reason)
  } else { Write-Output "i$i (no readiness file)" }
}
