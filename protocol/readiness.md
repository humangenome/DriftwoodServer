# `host-ready.json`

Written by the host mod into its configured state directory, atomically (temp file plus rename), on
every state change and at least every two seconds while running.

## Why it exists

The gameplay port binds before the world exists. Process-alive, port-listening and "the HTTP
endpoint answered" are therefore all equally true of a server whose entire mod stack failed to
apply, which is why the default failure mode of this class of product is a fleet that passes every
check and cannot be joined. This document is the only signal that distinguishes them, and something
must actually consume it — a correct signal that nothing reads is worth zero.

## Fields

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | Currently `1`. Increment on any breaking change; readers must reject an unknown schema rather than best-effort parse it. |
| `product` | string | Always `Driftwood`. |
| `pluginVersion` / `gameVersion` | string | The host mod's version and the game's `Application.version`. |
| `timestampUtc` | ISO-8601 | When this document was written. **A stale document is not a healthy server** — a wedged game process leaves its last document on disk saying `Hosting` forever. Readers treat anything older than their staleness window as unknown. |
| `phase` | enum | `Starting` \| `Hosting` \| `WillNotHost` \| `Stopping` \| `Stopped`. |
| `reason` | string | One plain sentence, readable by a support person who has never seen the code. On `WillNotHost` this is the whole diagnosis. |
| **`worldRunning`** | bool | **The only field a consumer should branch on for health.** True only when the server is started, the world object exists, the island is loaded and no island load is in flight. |
| `serverStarted`, `localClientStarted`, `worldObjectPresent`, `islandLoaded`, `islandLoading` | bool | The components of `worldRunning`, published so a failure is diagnosable without the log. |
| `port`, `slots` | int | The gameplay port and the number of **sold** slots. |
| `transportMaxClients` | int | What the transport is actually enforcing. Normally `slots + 1`: the host's own loopback connection occupies a transport slot and is never sold. A value equal to `slots` means the reservation is missing and the server would admit one player fewer than it sold; the transport default of `4095` means the limit never took effect at all. |
| `connectedTransportClients` | int | Raw transport connections, including the host's own. |
| `players` | int | Connected players as a customer would count them, i.e. `connectedTransportClients` minus the host's own. |
| `worldName`, `saveDirectory` | string | The loaded world and the directory it actually resolved to — not the one that was requested. |
| `ghostHostSuppressed` | bool | True when the host's own placeholder player was not spawned. |
| `bootAssertionsPassed` | bool | Every required guard installed, config validated, save root redirected and slot limit read back in force. |
| `displayNamesResolved` | bool | False once any connected player's display name came from a placeholder rather than a real source. |
| `effectiveBindAddress`, `effectiveTargetFrameRate` | string, int | Read back from the engine, not echoed from the config. |
| `swallowedTotal` | int | Total exceptions swallowed by the two deliberate swallow patches since start. **Expected to be 0.** A rising number is a broken feature wearing a seatbelt, not a handled edge case. |
| `swallowed` | array | Per-method `{method, total, peakPerSecond, lastException}`. |
| `patchesApplied` / `patchesMissing` / `patchesFailed` / `featuresStoodDown` | string[] | The boot report. `patchesApplied` is what the patch library confirmed it patched, not what was requested. |
| `roster` | string[] | `"<steamid>:<displayname>"` per connected player, excluding the host. |
| `unrecognisedConfigKeys` | string[] | Every key in the config file that matched no setting. Non-empty means the panel and the mod disagree about a name, and something the panel wrote is not in force. |

## Reader rules

1. Branch on `worldRunning`, never on `phase == "Hosting"` alone and never on the port.
2. Check `timestampUtc` freshness before trusting anything else.
3. `patchesFailed` non-empty, or `bootAssertionsPassed` false, is a failed start.
4. `players` is only meaningful when `worldRunning` is true. See `http-api.md` for the
   unknown-versus-zero rule, which is a safety property, not a formatting choice.
