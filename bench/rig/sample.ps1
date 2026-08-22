param(
  [string]$Tag = 'run',
  [int]$Seconds = 600,
  [int]$WarmupSeconds = 75,
  [int]$IntervalMs = 2000
)
$ErrorActionPreference = 'Stop'
$outDir = 'C:\driftbench\samples'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$csv = Join-Path $outDir "$Tag.csv"

# Discard the warm-up window so world generation and the join burst are never counted
# (playbook 2b measurement discipline).
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }

$procs = @{}
foreach ($p in Get-Process -Name 'DriftBench' -ErrorAction SilentlyContinue) {
  $procs[$p.Id] = @{ Proc = $p; LastCpu = $p.TotalProcessorTime.TotalMilliseconds }
}
if ($procs.Count -eq 0) { Write-Output "NO_DRIFTBENCH_PROCESSES"; exit 1 }

$rows = New-Object System.Collections.Generic.List[object]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lastWall = $sw.Elapsed.TotalMilliseconds
Start-Sleep -Milliseconds $IntervalMs

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  $nowWall = $sw.Elapsed.TotalMilliseconds
  $dtMs = $nowWall - $lastWall
  $lastWall = $nowWall
  foreach ($id in @($procs.Keys)) {
    $entry = $procs[$id]
    try { $entry.Proc.Refresh() } catch { $procs.Remove($id); continue }
    if ($entry.Proc.HasExited) { $procs.Remove($id); continue }
    $cpuMs = $entry.Proc.TotalProcessorTime.TotalMilliseconds
    $pct = if ($dtMs -gt 0) { (($cpuMs - $entry.LastCpu) / $dtMs) * 100.0 } else { 0 }
    $entry.LastCpu = $cpuMs
    $rows.Add([pscustomobject]@{
      t        = [math]::Round($sw.Elapsed.TotalSeconds,1)
      pid      = $id
      cpuPct   = [math]::Round($pct,2)
      privMB   = [math]::Round($entry.Proc.PrivateMemorySize64/1MB,1)
      wsMB     = [math]::Round($entry.Proc.WorkingSet64/1MB,1)
      threads  = $entry.Proc.Threads.Count
    })
  }
  Start-Sleep -Milliseconds $IntervalMs
}
$rows | Export-Csv -Path $csv -NoTypeInformation

Write-Output "TAG=$Tag samples=$($rows.Count) instances=$(($rows | Select-Object -ExpandProperty pid -Unique).Count) window=${Seconds}s"
$byPid = $rows | Group-Object pid
foreach ($g in $byPid) {
  $c = $g.Group | Select-Object -ExpandProperty cpuPct | Sort-Object
  $mean = ($c | Measure-Object -Average).Average
  $p50 = $c[[int]([math]::Floor($c.Count*0.50))]
  $p95 = $c[[int]([math]::Min($c.Count-1,[math]::Floor($c.Count*0.95)))]
  $priv = ($g.Group | Select-Object -ExpandProperty privMB | Measure-Object -Average).Average
  $ws   = ($g.Group | Select-Object -ExpandProperty wsMB | Measure-Object -Average).Average
  $wsMax = ($g.Group | Select-Object -ExpandProperty wsMB | Measure-Object -Maximum).Maximum
  Write-Output ("PID=" + $g.Name + " cpuMean=" + [math]::Round($mean,2) + "% cpuP50=" + $p50 + "% cpuP95=" + $p95 + "% privMean=" + [math]::Round($priv,1) + "MB wsMean=" + [math]::Round($ws,1) + "MB wsMax=" + $wsMax + "MB")
}
$all = $rows | Select-Object -ExpandProperty cpuPct
Write-Output ("AGGREGATE perInstanceMean=" + [math]::Round((($all | Measure-Object -Average).Average),2) + "% totalMean=" + [math]::Round((($all | Measure-Object -Average).Average) * $byPid.Count,2) + "% ofOneCore")
