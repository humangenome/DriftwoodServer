$ErrorActionPreference = 'Continue'
$m = 'C:\driftbench\samples\matrix4.log'
Set-Content -Path $m -Value ("MATRIX4 started " + (Get-Date -Format o)) -Encoding UTF8
function Note($t) { Add-Content -Path $m -Value ($t + "  " + (Get-Date -Format o)) -Encoding UTF8 }

# E3: ONE player, moving. E1 measured a spawned player standing still, because a first-time joiner
# is held by the intro and the tutorial - which is a real datum (the cost of a body in the world)
# but not the one that constrains a physics game. The bench client now overrides the input block and
# pushes the rigidbody directly, so this isolates MOTION from mere presence.
Note '--> E3-1player-moving'
& C:\profile.ps1 -Tag 'E3-1player-moving' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -Clients 1
Note '<-- E3-1player-moving'
Start-Sleep -Seconds 10

# The frame cap does nothing on this game (A 48.9% vs B 48.3%). These are the levers that CAN reach
# the wall-clock work: the physics step and the network tick rate.
Note '--> F-physics30'
& C:\profile.ps1 -Tag 'F-physics30' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -PhysicsStep 0.0333
Note '<-- F-physics30'
Start-Sleep -Seconds 10
Note '--> G-tick20'
& C:\profile.ps1 -Tag 'G-tick20' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -TickRate 20
Note '<-- G-tick20'
Start-Sleep -Seconds 10
Note '--> H-both'
& C:\profile.ps1 -Tag 'H-both' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -PhysicsStep 0.0333 -TickRate 20
Note '<-- H-both'
Start-Sleep -Seconds 10

# I: LEVER 3 - the world frozen while nobody is connected. The biggest possible win and the biggest
# possible risk, so it is measured AND its resumption is proven in the next run rather than assumed.
Note '--> I-pause-empty'
& C:\profile.ps1 -Tag 'I-pause-empty' -Instances 1 -Fps 30 -Warmup 180 -Window 420 -PauseEmpty true
Note '<-- I-pause-empty'
Start-Sleep -Seconds 10

# J: the proof that it comes back. Same pause setting, but a client joins partway through: the world
# must resume, the player must spawn, and it must be able to move.
Note '--> J-pause-then-join'
& C:\profile.ps1 -Tag 'J-pause-then-join' -Instances 1 -Fps 30 -Warmup 180 -Window 300 -Clients 1 -PauseEmpty true
Note '<-- J-pause-then-join'

Add-Content -Path $m -Value "MATRIX4_DONE" -Encoding UTF8
