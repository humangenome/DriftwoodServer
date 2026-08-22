param([int]$From = 1, [int]$To = 1)
$ErrorActionPreference = 'Stop'
for ($i = $From; $i -le $To; $i++) {
  $dst = "C:\driftbench\c$i"
  $ul = "$dst\unity.log"
  foreach ($f in @($ul, "$dst\BepInEx\LogOutput.log")) { if (Test-Path $f) { Remove-Item $f -Force } }
  $p = Start-Process -FilePath "$dst\DriftBenchClient.exe" -WorkingDirectory $dst `
       -ArgumentList '-batchmode','-nographics','-logFile',"$ul" -PassThru
  Write-Output ("c$i pid=" + $p.Id)
  Start-Sleep -Seconds 3
}
