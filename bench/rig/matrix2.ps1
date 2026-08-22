$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix2.log'
Set-Content -Path $m -Value ("MATRIX2 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }

# D: the ghost-host A/B. Same everything, host player NOT suppressed. Playbook 2b found the host's
# own character was the single largest idle cost on the sibling (~4.5% of a core in pathfinding
# alone), so this isolates what suppressing it actually buys.
Note '--> D-1x-ghost'
& C:\profile.ps1 -Tag 'D-1x-ghost' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -Suppress false
Note '<-- D-1x-ghost'
Start-Sleep -Seconds 10

# Client rigs, provisioned HERE - after every idle window - so the copy never contaminates a CPU
# measurement.
Note '--> provision clients'
& C:\provisionclient.ps1 -Count 2 *>> $m
Note '<-- provision clients'

# E: CPU under real player load, the number that actually constrains this product, because the host
# simulates and replicates every rigidbody to every client.
#
# HEADROOM IS THE CONSTRAINT ON THIS RIG. The measurement box is a 2-core i3 and one idle server
# already wants ~49% of a core; each headless bench client wants about as much again. Three clients
# would put total demand near 200% and the server would be STARVED, which under-reports a hungry
# process (that is exactly how a contaminated window read 20% for a 49% server earlier today). So
# the slope is taken at 1 and 2 players, where the box still has headroom, and the box load is
# recorded with each window so saturation can be ruled out rather than assumed.
Note '--> E1-1player'
& C:\profile.ps1 -Tag 'E1-1player' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -Clients 1
Note ('box load after E1: ' + (Get-CimInstance Win32_Processor).LoadPercentage + '%')
Note '<-- E1-1player'
Start-Sleep -Seconds 10

Note '--> E2-2players'
& C:\profile.ps1 -Tag 'E2-2players' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -Clients 2
Note ('box load after E2: ' + (Get-CimInstance Win32_Processor).LoadPercentage + '%')
Note '<-- E2-2players'

Add-Content -Path $m -Value "MATRIX2_DONE" -Encoding UTF8
