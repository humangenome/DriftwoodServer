# Playbook 1d requirement 2, tested by BREAKING IT ON PURPOSE. A gate nobody has seen fail is not
# a gate. Fault injection makes one REQUIRED patch target resolve as missing; the server must then
# refuse to host, say so in one plain sentence, and - the part that actually matters - NEVER BIND
# THE GAMEPLAY PORT.
$ErrorActionPreference = 'Continue'
$log = 'C:\driftbench\samples\failclosed.log'
$dst = 'C:\driftbench\i1'
function Say($m) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Output $l; Add-Content -Path $log -Value $l -Encoding UTF8 }
Set-Content -Path $log -Value ("=== FAIL-CLOSED TEST " + (Get-Date -Format o) + " ===") -Encoding UTF8

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 3
& C:\deploy.ps1 -Instance 1 -Slots 8 -Fps 30 | Out-Null

# NPC.Update is Required: without it the NPC loop dies on the first null nearest-player, which on a
# Driftwood host with the ghost suppressed is the normal state of an empty server.
Add-Content -Path "$dst\BepInEx\config\com.humangenome.driftwood.host.cfg" -Value "SimulateMissingPatch = NPC.Update" -Encoding UTF8
Remove-Item "$dst\Logs\.driftwood-saveroot","$dst\Logs\.driftwood-guards" -Force -EA SilentlyContinue
Remove-Item "$dst\driftwood-state\host-ready.json" -Force -EA SilentlyContinue

$p = Start-Process -FilePath "$dst\DriftBench.exe" -WorkingDirectory $dst -ArgumentList '-batchmode','-nographics','-logFile',"$dst\unity.log" -PassThru
Say ("started pid=" + $p.Id + " with NPC.Update forced missing")
Start-Sleep -Seconds 90

Say "=== does it hold the gameplay port? (it must NOT) ==="
$udp = @(Get-NetUDPEndpoint -EA SilentlyContinue | Where-Object { $_.OwningProcess -eq $p.Id })
if ($udp.Count -eq 0) { Say "  PASS: the process owns NO UDP endpoint - the gameplay port was never presented" }
else { $udp | ForEach-Object { Say ("  FAIL: bound " + $_.LocalAddress + ":" + $_.LocalPort) } }
$listen = @(Get-NetUDPEndpoint -LocalPort 22003 -EA SilentlyContinue)
Say ("  anything on 22003 at all: " + $listen.Count)

Say "=== readiness ==="
$f = "$dst\driftwood-state\host-ready.json"
if (Test-Path $f) { $j = Get-Content $f -Raw | ConvertFrom-Json; Say ("  phase=" + $j.phase); Say ("  bootAssertionsPassed=" + $j.bootAssertionsPassed); Say ("  reason=" + $j.reason) } else { Say "  (no readiness file)" }

Say "=== boot markers (guards marker must NOT list NPC.Update) ==="
$g = "$dst\Logs\.driftwood-guards"
if (Test-Path $g) {
  $lines = Get-Content $g
  Say ("  guards listed: " + $lines.Count)
  if ($lines -contains 'NPC.Update') { Say "  FAIL: NPC.Update is listed as installed and it is not" } else { Say "  PASS: NPC.Update is absent from the installed-guards marker" }
} else { Say "  (no guards marker)" }

Say "=== status API (should refuse to claim health) ==="
try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:22004/api/v1/status' -UseBasicParsing -TimeoutSec 8; Say ("  " + $r.Content) } catch { Say ("  no status endpoint: " + $_.Exception.Message) }

Say "=== the sentence a support person reads ==="
$bl = "$dst\BepInEx\LogOutput.log"
if (Test-Path $bl) { Get-Content $bl | Select-String 'WILL NOT HOST|REQUIRED PATCH|FAULT INJECTION' | ForEach-Object { Say ("  " + $_.Line) } }

Stop-Process -Id $p.Id -Force -EA SilentlyContinue
# Wait for the process to actually go before re-deploying: copying the plugin over a DLL the dying
# process still has mapped fails with "a file with a user-mapped section open", which killed the
# first chained run at exactly this line.
for ($w = 0; $w -lt 20; $w++) { Start-Sleep -Seconds 1; if ($p.HasExited) { break } }
Start-Sleep -Seconds 3
# Put the instance back the way it was so nothing downstream inherits fault injection.
& C:\deploy.ps1 -Instance 1 -Slots 8 -Fps 30 | Out-Null
Say "=== FAILCLOSED_DONE ==="
