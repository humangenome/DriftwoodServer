param([string]$Tag)
$f = "C:\driftbench\samples\$Tag.log"
if (-not (Test-Path $f)) { Write-Output "(no log)"; exit }
Get-Content $f | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Output $_ }
