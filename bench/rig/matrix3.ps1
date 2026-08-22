$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix3.log'
Set-Content -Path $m -Value ("MATRIX3 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }
Note '--> failclosed';   & C:\failclosed.ps1  *>> $m; Note '<-- failclosed'
Note '--> persistence';  & C:\persistence.ps1 *>> $m; Note '<-- persistence'
Note '--> slotproof';    & C:\slotproof.ps1 -Slots 2 -Clients 3 -Port 22003 *>> $m; Note '<-- slotproof'
Add-Content -Path $m -Value "MATRIX3_DONE" -Encoding UTF8
