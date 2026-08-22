$ErrorActionPreference = 'Continue'
$c = 'C:\driftbench\samples\chain.log'
Set-Content -Path $c -Value ("CHAIN started " + (Get-Date -Format o)) -Encoding UTF8
# Wait for the idle matrix that is already running, so nothing overlaps a CPU window.
for ($i = 0; $i -lt 240; $i++) {
  if ((Get-Content 'C:\driftbench\samples\matrix.log' -EA SilentlyContinue) -match 'MATRIX_DONE') { break }
  Start-Sleep -Seconds 15
}
Add-Content -Path $c -Value ("matrix1 done, starting matrix2 " + (Get-Date -Format o)) -Encoding UTF8
& C:\matrix2.ps1
Add-Content -Path $c -Value ("matrix2 done, starting matrix3 " + (Get-Date -Format o)) -Encoding UTF8
& C:\matrix3.ps1
# Core normalisation LAST, so its short single-threaded burst cannot land inside a CPU window.
Add-Content -Path $c -Value "corebench:" -Encoding UTF8
$bench = Join-Path 'C:\driftbench' ('core' + 'bench.' + 'exe')
& $bench *>> $c
Add-Content -Path $c -Value ("CHAIN_DONE " + (Get-Date -Format o)) -Encoding UTF8
