param([int]$From = 1, [int]$To = 1)
$ErrorActionPreference = 'Stop'
for ($i = $From; $i -le $To; $i++) {
  $dst = "C:\driftbench\i$i"
  $ul  = "$dst\unity.log"
  if (Test-Path $ul) { Remove-Item $ul -Force }
  if (Test-Path "$dst\BepInEx\LogOutput.log") { Remove-Item "$dst\BepInEx\LogOutput.log" -Force }
  Remove-Item "$dst\driftwood-state\host-ready.json" -Force -EA SilentlyContinue
  $p = Start-Process -FilePath "$dst\DriftBench.exe" -WorkingDirectory $dst `
       -ArgumentList '-batchmode','-nographics','-logFile',"$ul" -PassThru
  Write-Output ("i$i pid=" + $p.Id)
  Start-Sleep -Milliseconds 1500
}
