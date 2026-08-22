param([int]$Seconds = 60)
$procs = @{}
foreach ($p in Get-Process DriftBench,DriftBenchClient -EA SilentlyContinue) { $procs[$p.Id] = @{P=$p; C=$p.TotalProcessorTime.TotalMilliseconds; N=$p.ProcessName} }
if ($procs.Count -eq 0) { Write-Output "no processes"; exit }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$dt = $sw.Elapsed.TotalMilliseconds
Write-Output ("window=" + [math]::Round($dt/1000,1) + "s")
foreach ($id in $procs.Keys) {
  $e = $procs[$id]; $e.P.Refresh()
  $pct = (($e.P.TotalProcessorTime.TotalMilliseconds - $e.C) / $dt) * 100
  Write-Output ("  " + $e.N + " pid=" + $id + " cpu=" + [math]::Round($pct,2) + "% ofOneCore  privMB=" + [math]::Round($e.P.PrivateMemorySize64/1MB,1) + " wsMB=" + [math]::Round($e.P.WorkingSet64/1MB,1))
}
$os = Get-CimInstance Win32_Processor | Select-Object -First 1
Write-Output ("  boxLoadPct=" + $os.LoadPercentage)
