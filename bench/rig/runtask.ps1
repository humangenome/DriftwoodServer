param(
  [string]$Tag,
  [int]$Instances = 1,
  [int]$Fps = 30,
  [int]$Warmup = 180,
  [int]$Window = 420,
  [string]$Suppress = 'true',
  [int]$Clients = 0
)
$ErrorActionPreference = "Continue"
$cmd = "C:\driftbench\task-$Tag.cmd"
# /tr truncates at 261 characters, so the task runs a .cmd wrapper rather than a long command line.
@"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\profile.ps1 -Tag $Tag -Instances $Instances -Fps $Fps -Warmup $Warmup -Window $Window -Suppress $Suppress -Clients $Clients
"@ | Set-Content -Path $cmd -Encoding ASCII

$name = "driftbench-$Tag"
$env:NAME = $name
cmd /c "schtasks /delete /tn %NAME% /f >nul 2>&1"
# Run under SYSTEM via the Task Scheduler service so the work survives the SSH session closing.
$create = schtasks /create /tn $name /tr $cmd /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1
Write-Output ("create: " + ($create -join " "))
$run = schtasks /run /tn $name 2>&1
Write-Output ("run: " + ($run -join " "))
Write-Output "task $name started"
