param([int]$Slots = 2, [int]$Port = 22003)
# THE REOPEN HALF of gate 1b: a full server must not be a one-way door.
#
# The previous attempt reported a false FAIL because its refusal detector asked "has this server
# ever refused a join?" - and the genuine refusal from two minutes earlier was still in the log. So
# this one BASELINES the refusal count before every attempt and requires it to INCREASE. A check
# whose input stopped meaning anything still returns an answer; counting fixes it, grepping does not.
$ErrorActionPreference = 'Continue'
$log = 'C:\driftbench\samples\reopen.log'
function Say($m) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Output $l; Add-Content -Path $log -Value $l -Encoding UTF8 }
Set-Content -Path $log -Value ("=== REOPEN TEST slots=$Slots port=$Port " + (Get-Date -Format o) + " ===") -Encoding UTF8

function ServerState { $f = 'C:\driftbench\i1\driftwood-state\host-ready.json'; if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json) } return $null }
function RefusalCount {
  $n = 0
  $srv = Get-Content 'C:\driftbench\i1\BepInEx\LogOutput.log' -EA SilentlyContinue
  if ($srv) { $n = @($srv | Select-String -SimpleMatch 'Refused a join').Count }
  return $n
}
function StopClient($i) {
  Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'DriftBenchClient.exe' -and $_.ExecutablePath -like "C:\driftbench\c$i\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
  Start-Sleep -Seconds 4
}
# Baseline the refusal count, then wait for either a SPAWN or a NEW refusal.
function Attempt($i, $timeoutSeconds) {
  $before = RefusalCount
  & C:\launchclient.ps1 -From $i -To $i | Out-Null
  $deadline = (Get-Date).AddSeconds($timeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $lines = Get-Content "C:\driftbench\c$i\BepInEx\LogOutput.log" -EA SilentlyContinue
    if ($lines | Select-String -SimpleMatch 'BENCH_SPAWNED') { return 'ADMITTED' }
    if ((RefusalCount) -gt $before) { return 'REFUSED' }
  }
  return 'TIMEOUT'
}

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 4
& C:\deploy.ps1 -Instance 1 -Port $Port -Slots $Slots -Fps 30 -World ReopenProof | Out-Null
& C:\launch.ps1 -From 1 -To 1 | ForEach-Object { Say $_ }
for ($i = 0; $i -lt 48; $i++) { Start-Sleep -Seconds 5; $j = ServerState; if ($j -and $j.worldRunning) { Say ("server ready: slots=" + $j.slots + " maxClients=" + $j.transportMaxClients); break } }

foreach ($c in 1..2) { $o = Attempt $c 180; $j = ServerState; Say ("c$c -> $o  players=" + $j.players + " transportClients=" + $j.connectedTransportClients) }

$o = Attempt 3 180; $j = ServerState
Say ("c3 (over the limit) -> $o  players=" + $j.players + " transportClients=" + $j.connectedTransportClients)
if ($o -ne 'REFUSED') { Say 'UNEXPECTED: the third client was not refused'; }

Say '--- freeing a slot ---'
StopClient 1
$t0 = Get-Date; $freed = $null
for ($i = 0; $i -lt 48; $i++) { Start-Sleep -Seconds 5; $j = ServerState; if ($j.connectedTransportClients -lt ($Slots + 1)) { $freed = ((Get-Date) - $t0).TotalSeconds; break } }
if ($freed) { Say ("slot freed after " + [math]::Round($freed,0) + "s") } else { Say 'slot never freed within 240s'; }

StopClient 3
$o = Attempt 3 180; $j = ServerState
Say ("REOPEN: c3 -> $o  players=" + $j.players + " transportClients=" + $j.connectedTransportClients)
if ($o -eq 'ADMITTED') { Say 'PASS: a full server is not a one-way door' } else { Say 'FAIL: the freed slot did not reopen' }

& C:\stopall.ps1 | Out-Null
Add-Content -Path $log -Value 'REOPEN_DONE' -Encoding UTF8
