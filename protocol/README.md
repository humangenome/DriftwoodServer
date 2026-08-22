# Driftwood wire formats

Three interfaces cross a process boundary in this product, so all three are specified rather than
described. Family policy: protocol specs are **per product**, written against this implementation.
A `PORT FROM <sibling>` stub is a release blocker, because it points a reader at a different
product's wire format that is only accidentally the same.

| Spec | Between |
|---|---|
| [`readiness.md`](readiness.md) | the host mod (inside the game) and the supervisor / panel |
| [`http-api.md`](http-api.md) | the host mod and the panel's query companion |
| [`boot-markers.md`](boot-markers.md) | the host mod and the panel's health check |

Keep each of these in lockstep with its source. `HostHttpApi.cs`, `Readiness.cs` and
`BootMarkers.cs` reference these files by path in comments for exactly that reason.
