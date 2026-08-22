# Changelog

All notable changes to DriftwoodServer are recorded here.

## 0.1.0

### Added

- `DriftwoodHost`, the BepInEx 5 plugin that runs the game as a dedicated server: it selects the
  game's own direct-UDP transport, binds an explicit address, loads or creates the world, applies
  the slot limit before the server starts listening, and publishes a readiness signal.
- A fail-closed patch plan: every target is resolved before any patch is applied, every miss is
  reported in one block, and the number of methods actually patched is asserted against the patch
  library rather than inferred from a call that returned cleanly. A missing required patch refuses
  to host.
- Steam guards at the SDK boundary rather than at call sites. Without them a headless server cannot
  complete a player spawn: the throw escapes into the netcode's shared spawn loop and every object
  queued behind it is left registered but never initialised.
- Per-instance save redirection, so servers sharing a machine cannot overwrite each other's world,
  plus two boot markers the panel asserts on.
- Suppression of the host's own placeholder player, so an empty server genuinely reports zero
  players and no phantom appears in the world or on any roster.
- A status and control API on the gameplay port plus one. Unknown player counts are reported as
  unknown and never as zero.
- `DriftwoodServer`, the .NET 8 supervisor: build pin verification against the game's own code and
  Steam's install record, process and log ownership, readiness consumption, a health endpoint, and
  save-then-snapshot backups.
