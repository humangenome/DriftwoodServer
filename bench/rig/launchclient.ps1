param([int]$From = 1, [int]$To = 1)
$ErrorActionPreference = 'Stop'
for ($i = $From; $i -le $To; $i++) {
  $dst = "C:\driftbench\c$i"
  $ul = "$dst\unity.log"
  # Never delete a log a running process still holds - that threw and killed the first slot proof.
  Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'DriftBenchClient.exe' -and $_.ExecutablePath -like "$dst\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
  Start-Sleep -Seconds 3
  foreach ($f in @($ul, "$dst\BepInEx\LogOutput.log")) { if (Test-Path $f) { Remove-Item $f -Force -EA SilentlyContinue } }
  $p = Start-Process -FilePath "$dst\DriftBenchClient.exe" -WorkingDirectory $dst `
       -ArgumentList '-batchmode','-nographics','-logFile',"$ul" -PassThru
  Write-Output ("c$i pid=" + $p.Id)
  Start-Sleep -Seconds 3
}
