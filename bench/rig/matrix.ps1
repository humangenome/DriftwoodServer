$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix.log'
Set-Content -Path $m -Value ("MATRIX started " + (Get-Date -Format o)) -Encoding UTF8
function Run($tag,$inst,$fps,$warm,$win) {
  Add-Content -Path $m -Value ("--> $tag " + (Get-Date -Format o)) -Encoding UTF8
  & C:\profile.ps1 -Tag $tag -Instances $inst -Fps $fps -Warmup $warm -Window $win
  Add-Content -Path $m -Value ("<-- $tag " + (Get-Date -Format o)) -Encoding UTF8
  Start-Sleep -Seconds 10
}
Run 'A-1x-fps30'  1 30 180 420
Run 'B-1x-uncap'  1 0  180 420
Run 'C-6x-fps30'  6 30 240 420
Add-Content -Path $m -Value "MATRIX_DONE" -Encoding UTF8
