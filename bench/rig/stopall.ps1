# Scope every kill by executable PATH. These servers and their clients are frequently the same
# executable name, and a bare taskkill /IM has destroyed a live test server mid-session before.
$ErrorActionPreference = 'SilentlyContinue'
Get-CimInstance Win32_Process | Where-Object {
  ($_.Name -eq 'DriftBench.exe' -or $_.Name -eq 'DriftBenchClient.exe') -and $_.ExecutablePath -like 'C:\driftbench\*'
} | ForEach-Object {
  Write-Output ("stopping pid=" + $_.ProcessId + " " + $_.ExecutablePath)
  Stop-Process -Id $_.ProcessId -Force
}
Start-Sleep -Seconds 2
