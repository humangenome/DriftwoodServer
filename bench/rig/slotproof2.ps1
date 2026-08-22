param([int]$Slots = 2, [int]$Clients = 3, [int]$Port = 22003)
# SLOT PROOF, second attempt. The first was inconclusive for a rig reason, not a product reason:
# it judged each client 45 seconds after launch, and this game needs ~40 s to boot before the mod's
# 12 s start delay even begins - so the third client had not dialled yet when the harness decided it
# had been refused. An inconclusive result read as a pass is exactly the failure this gate exists to
# prevent, so the harness now waits for a DEFINITE outcome per client instead of a fixed delay.
$ErrorActionPreference = 'Continue'
$log = 'C:\driftbench\samples\slotproof2.log'
function Say($m) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Output $l; Add-Content -Path $log -Value $l -Encoding UTF8 }
Set-Content -Path $log -Value ("=== SLOT PROOF 2  slots=$Slots clients=$Clients port=$Port  " + (Get-Date -Format o) + " ===") -Encoding UTF8

function ServerState {
  $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'
  if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json) }
  return $null
}
function ClientLog($i) {
  $p = "C:\driftbench\c$i\BepInEx\LogOutput.log"
  if (Test-Path $p) { return (Get-Content $p -EA SilentlyContinue) }
  return @()
}
function StopClient($i) {
  Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'DriftBenchClient.exe' -and $_.ExecutablePath -like "C:\driftbench\c$i\*" } | ForEach-Object {
    Say ("stopping c$i pid=" + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue
  }
  Start-Sleep -Seconds 4
}
# Waits for a DEFINITE outcome: spawned, or refused by the server, or a timeout that is reported as
# a timeout rather than quietly counted as a refusal.
function WaitOutcome($i, $timeoutSeconds) {
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $lines = ClientLog $i
    if ($lines | Select-String -SimpleMatch 'BENCH_SPAWNED') { return 'ADMITTED' }
    $srv = Get-Content 'C:\driftbench\i1\BepInEx\LogOutput.log' -EA SilentlyContinue
    if ($srv | Select-String -SimpleMatch 'Refused a join') { return 'REFUSED' }
    $unity = Get-Content 'C:\driftbench\i1\unity.log' -EA SilentlyContinue
    if ($unity | Select-String -SimpleMatch 'Connection limit reached') { return 'REFUSED' }
  }
  $lines = ClientLog $i
  if ($lines | Select-String -SimpleMatch 'BENCH dialling') { return 'DIALLED-BUT-NEVER-SPAWNED' }
  return 'TIMEOUT-NEVER-DIALLED'
}

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 4
& C:\deploy.ps1 -Instance 1 -Port $Port -Slots $Slots -Fps 30 -World SlotProof | Out-Null
& C:\launch.ps1 -From 1 -To 1 | ForEach-Object { Say $_ }

$ready = $false
for ($i = 0; $i -lt 48; $i++) {
  Start-Sleep -Seconds 5
  $j = ServerState
  if ($j -and $j.worldRunning) { Say ("server ready: slots=" + $j.slots + " maxClients=" + $j.transportMaxClients); $ready = $true; break }
}
if (-not $ready) { Say 'SERVER NEVER REACHED worldRunning - aborting'; Add-Content -Path $log -Value 'SLOTPROOF2_DONE' -Encoding UTF8; exit }

& C:\deployclient.ps1 -From 1 -To $Clients -ServerPort $Port | Out-Null

for ($c = 1; $c -le $Clients; $c++) {
  Say "--- client c$c ---"
  & C:\launchclient.ps1 -From $c -To $c | ForEach-Object { Say $_ }
  $outcome = WaitOutcome $c 180
  $j = ServerState
  Say ("c$c outcome=$outcome  players=" + $j.players + " transportClients=" + $j.connectedTransportClients + " maxClients=" + $j.transportMaxClients + " slots=" + $j.slots)
}

Say '=== freeing one slot, then giving the refused client another go ==='
StopClient 1
# A hard-killed client does not send a disconnect, so the server holds its slot until the netcode
# times it out. Measure how long that actually takes instead of assuming it is instant - a player
# who alt-F4s holds a paid slot for exactly this long.
$freedAt = $null
$t0 = Get-Date
for ($i = 0; $i -lt 36; $i++) {
  Start-Sleep -Seconds 5
  $j = ServerState
  if ($j.connectedTransportClients -lt ($Slots + 1)) { $freedAt = ((Get-Date) - $t0).TotalSeconds; break }
}
if ($freedAt) { Say ("slot freed after " + [math]::Round($freedAt,0) + "s of an abrupt client kill") }
else { Say 'slot NOT freed within 180s of an abrupt client kill' }

StopClient $Clients
& C:\launchclient.ps1 -From $Clients -To $Clients | ForEach-Object { Say $_ }
$outcome = WaitOutcome $Clients 180
$j = ServerState
Say ("reopen: c$Clients outcome=$outcome  players=" + $j.players + " transportClients=" + $j.connectedTransportClients)

Say '=== server-side refusal evidence ==='
(Get-Content 'C:\driftbench\i1\BepInEx\LogOutput.log' -EA SilentlyContinue | Select-String -SimpleMatch 'Refused a join') | ForEach-Object { Say ("  " + $_.Line) }
(Get-Content 'C:\driftbench\i1\unity.log' -EA SilentlyContinue | Select-String -SimpleMatch 'Connection limit reached') | Select-Object -First 5 | ForEach-Object { Say ("  unity: " + $_.Line) }

& C:\stopall.ps1 | Out-Null
Add-Content -Path $log -Value 'SLOTPROOF2_DONE' -Encoding UTF8
