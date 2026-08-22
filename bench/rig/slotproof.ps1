param([int]$Slots = 2, [int]$Clients = 3, [int]$Port = 22003)
$ErrorActionPreference = 'Continue'
$log = "C:\driftbench\samples\slotproof.log"
New-Item -ItemType Directory -Force -Path 'C:\driftbench\samples' | Out-Null
function Say($m) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Output $line; Add-Content -Path $log -Value $line -Encoding UTF8 }

Set-Content -Path $log -Value ("=== SLOT PROOF slots=$Slots clients=$Clients port=$Port started " + (Get-Date -Format o) + " ===") -Encoding UTF8
& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 3

& C:\deploy.ps1 -Instance 1 -Port $Port -Slots $Slots -Fps 30 -World SlotProof -Suppress true | Out-Null
& C:\launch.ps1 -From 1 -To 1 | ForEach-Object { Say $_ }

# Wait for the world to be genuinely running before any client dials.
$ready = $false
for ($i = 0; $i -lt 40; $i++) {
  Start-Sleep -Seconds 5
  $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'
  if (Test-Path $f) {
    $j = Get-Content $f -Raw | ConvertFrom-Json
    if ($j.worldRunning) { Say ("server ready: phase=" + $j.phase + " slots=" + $j.slots + " maxClients=" + $j.transportMaxClients); $ready = $true; break }
  }
}
if (-not $ready) { Say "SERVER NEVER REACHED worldRunning - aborting"; exit 1 }

& C:\deployclient.ps1 -From 1 -To $Clients -ServerPort $Port | Out-Null

# Admit exactly $Slots, one at a time, then send one MORE than was sold.
for ($c = 1; $c -le $Clients; $c++) {
  Say "--- launching client c$c ---"
  & C:\launchclient.ps1 -From $c -To $c | ForEach-Object { Say $_ }
  Start-Sleep -Seconds 45
  $j = Get-Content 'C:\driftbench\i1\driftwood-state\host-ready.json' -Raw | ConvertFrom-Json
  Say ("after c$c : players=" + $j.players + " transportClients=" + $j.connectedTransportClients + " maxClients=" + $j.transportMaxClients + " slots=" + $j.slots)
  $bl = "C:\driftbench\c$c\BepInEx\LogOutput.log"
  if (Test-Path $bl) { Get-Content $bl | Select-String 'BENCH' | Select-Object -Last 2 | ForEach-Object { Say ("  c$c : " + $_.Line) } }
}

Say "=== over-limit check complete; now freeing a slot ==="
$victim = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'DriftBenchClient.exe' -and $_.ExecutablePath -like 'C:\driftbench\c1\*' }
if ($victim) { Stop-Process -Id $victim.ProcessId -Force; Say ("stopped c1 pid=" + $victim.ProcessId) }
Start-Sleep -Seconds 30
$j = Get-Content 'C:\driftbench\i1\driftwood-state\host-ready.json' -Raw | ConvertFrom-Json
Say ("after freeing one: players=" + $j.players + " transportClients=" + $j.connectedTransportClients)

# The last client should now be able to get in - a full server must not be a one-way door.
Say "--- relaunching the refused client c$Clients ---"
& C:\launchclient.ps1 -From $Clients -To $Clients | ForEach-Object { Say $_ }
Start-Sleep -Seconds 60
$j = Get-Content 'C:\driftbench\i1\driftwood-state\host-ready.json' -Raw | ConvertFrom-Json
Say ("after reopen: players=" + $j.players + " transportClients=" + $j.connectedTransportClients)
$bl = "C:\driftbench\c$Clients\BepInEx\LogOutput.log"
if (Test-Path $bl) { Get-Content $bl | Select-String 'BENCH' | Select-Object -Last 3 | ForEach-Object { Say ("  c$Clients : " + $_.Line) } }

Say "=== server-side refusal lines ==="
$sl = 'C:\driftbench\i1\BepInEx\LogOutput.log'
if (Test-Path $sl) { Get-Content $sl | Select-String 'Refused a join|Connection limit' | ForEach-Object { Say ("  " + $_.Line) } }
$ul = 'C:\driftbench\i1\unity.log'
if (Test-Path $ul) { Get-Content $ul | Select-String 'Connection limit reached' | ForEach-Object { Say ("  unity: " + $_.Line) } }

& C:\stopall.ps1 | Out-Null
Say "=== SLOTPROOF_DONE ==="
