# Security Policy

## Reporting a vulnerability

Report privately through GitHub's private vulnerability reporting:

**https://github.com/HumanGenome/DriftwoodServer/security/advisories/new**

There is no security mailing address for this project. Please do not open a public issue for a
vulnerability.

## In scope

DriftwoodServer runs a game process on a shared host and exposes a small HTTP surface, so the
interesting surface is anything that crosses one of those boundaries:

- Authentication bypass on any mutating route of the host HTTP API (`POST /api/v1/save` and
  anything added beside it). `GET /api/v1/status` is unauthenticated **by design** and is scoped by
  firewall, not by the application.
- Path traversal or arbitrary write through a configured path (`SaveRoot`, `StateRoot`,
  `InstanceRoot`, `WorldName`).
- Command injection through any configuration value that reaches a process launch.
- Privilege escalation out of the supervised game process, or escaping the job object that contains
  it.
- Anything that lets one hosted server read or write another hosted server's world.

## Out of scope

- Denial of service by an authenticated operator against their own server.
- Cheating, item duplication or other in-game exploits in How to Fish itself. Report those to the
  game's developer.
- Vulnerabilities in the game's own code or in BepInEx. Report those upstream.
- The unauthenticated status endpoint returning status. That is its job; the firewall is the
  control.
