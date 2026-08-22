$a = @{}
foreach ($p in Get-Process) { $a[$p.Id] = @{P=$p; C=$p.TotalProcessorTime.TotalMilliseconds; N=$p.ProcessName} }
$sw=[System.Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Seconds 20; $dt=$sw.Elapsed.TotalMilliseconds
$rows=@()
foreach ($id in $a.Keys) {
  $e=$a[$id]
  try { $e.P.Refresh(); if ($e.P.HasExited) { continue } } catch { continue }
  $pct = (($e.P.TotalProcessorTime.TotalMilliseconds - $e.C)/$dt)*100
  if ($pct -gt 0.5) { $rows += [pscustomobject]@{Name=$e.N; Id=$id; Pct=[math]::Round($pct,1); WSMB=[math]::Round($e.P.WorkingSet64/1MB,0)} }
}
$rows | Sort-Object Pct -Descending | Select-Object -First 12 | ForEach-Object { Write-Output ("  " + $_.Name + " pid=" + $_.Id + " " + $_.Pct + "% ws=" + $_.WSMB + "MB") }
Write-Output ("mem: " + [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB,1) + " GB free")
