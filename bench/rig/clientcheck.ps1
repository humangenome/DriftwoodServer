param([int]$From=1,[int]$To=1)
& C:\readystate.ps1 -From 1 -To 1
for ($i=$From; $i -le $To; $i++) {
  $bl = "C:\driftbench\c$i\BepInEx\LogOutput.log"
  Write-Output "--- c$i ---"
  if (Test-Path $bl) { Get-Content $bl | Select-String -Pattern 'BENCH' | Select-Object -Last 4 | ForEach-Object { Write-Output ("   " + $_.Line) } } else { Write-Output "   (no log)" }
  $ul = "C:\driftbench\c$i\unity.log"
  if (Test-Path $ul) { Write-Output ("   unityLogLines=" + (Get-Content $ul).Count) }
}
Get-Process DriftBench,DriftBenchClient -EA SilentlyContinue | ForEach-Object { Write-Output ("proc " + $_.ProcessName + " pid=" + $_.Id + " cpuSec=" + [math]::Round($_.TotalProcessorTime.TotalSeconds,1)) }
