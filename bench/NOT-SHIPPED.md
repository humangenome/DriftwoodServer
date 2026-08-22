# bench/ — measurement rigs, never shipped

Nothing in this folder is part of the Driftwood product. It exists so the resource profile behind
the pricing decision is reproducible.

`DriftwoodBenchClient` is a headless load generator: it joins a Driftwood server, waits for its own
player to spawn, then drives that player's movement input so the host has to simulate and replicate
a genuinely moving rigidbody. **A headless test client is a LOWER BOUND** — it walks, it does not
build, fight or fish, so every per-player figure it produces understates the real cost. The number
it produces is a floor, and it is reported as one.

Playbook §7a row 13 requires every artefact a player or a server receives to be on a declared list
the build enforces. This folder is the explicit declaration that these binaries are on NO shipping
list: they are not in the server bundle, not in the client pack, and not in the installer.
