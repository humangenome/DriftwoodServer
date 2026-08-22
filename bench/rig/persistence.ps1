# Gate 2: the world survives a restart. Boot, change nothing but let the game save, stop cleanly,
# boot again, and prove the SAME save file is reloaded rather than a fresh world being generated.
$ErrorActionPreference = 'Continue'
$log = 'C:\driftbench\samples\persistence.log'
$dst = 'C:\driftbench\i1'
function Say($m) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; Write-Output $l; Add-Content -Path $log -Value $l -Encoding UTF8 }
Set-Content -Path $log -Value ("=== PERSISTENCE TEST " + (Get-Date -Format o) + " ===") -Encoding UTF8

& C:\stopall.ps1 | Out-Null
Start-Sleep -Seconds 3
& C:\deploy.ps1 -Instance 1 -Slots 8 -Fps 30 -World 'PersistProof' | Out-Null
Remove-Item "$dst\Saves\PersistProof.txt" -Force -EA SilentlyContinue

function BootAndSave($label) {
  $p = Start-Process -FilePath "$dst\DriftBench.exe" -WorkingDirectory $dst -ArgumentList '-batchmode','-nographics','-logFile',"$dst\unity.log" -PassThru
  Start-Sleep -Seconds 80
  try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:22004/api/v1/save' -Method POST -Headers @{'X-Driftwood-Auth'='benchtoken1'} -UseBasicParsing -TimeoutSec 25; Say ("$label save -> " + $r.Content) } catch { Say ("$label save FAILED: " + $_.Exception.Message) }
  Start-Sleep -Seconds 5
  $f = "$dst\Saves\PersistProof.txt"
  if (Test-Path $f) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash
    $j = Get-Content $f -Raw | ConvertFrom-Json
    Say ("$label save: bytes=" + (Get-Item $f).Length + " sha256=" + $h.Substring(0,16) + " name=" + $j.Name + " money=" + $j.Money + " island=" + $j.SpawnedIsland + " maxIsland=" + $j.MaxIsland + " playtime=" + [math]::Round($j.Playtime,1))
  } else { Say "$label save: FILE MISSING" }
  # Graceful stop through the same file the panel uses.
  Set-Content -Path "$dst\driftwood-state\stop.requested" -Value (Get-Date -Format o) -Encoding UTF8
  for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Seconds 2; if ($p.HasExited) { break } }
  if ($p.HasExited) { Say ("$label stopped gracefully, exit=" + $p.ExitCode) } else { Say "$label did NOT stop gracefully"; Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
  Start-Sleep -Seconds 3
}

BootAndSave 'boot1'
Say "--- restarting ---"
BootAndSave 'boot2'

Say "=== did boot2 CREATE or LOAD? ==="
$bl = "$dst\BepInEx\LogOutput.log"
if (Test-Path $bl) { Get-Content $bl | Select-String 'Created world|Loaded world' | ForEach-Object { Say ("  " + $_.Line) } }
Say "=== PERSISTENCE_DONE ==="
