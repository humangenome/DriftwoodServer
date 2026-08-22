# Contributing to DriftwoodServer

Thanks for taking the time. This repo holds the dedicated-server supervisor and the host mod for
How to Fish.

## Before you open a PR

- `dotnet test src/DriftwoodServer.Tests/DriftwoodServer.Tests.csproj -c Release` must pass.
- `dotnet build -c Release` must be warning-free. Warnings are errors in the supervisor.
- The host mod compiles against the game's own assemblies. Game binaries are never committed; point
  `-p:ManagedDir=` at your own install.

## Things this codebase will not accept

- **A check that passes when it cannot make its decision.** If a verifier cannot read what it needs,
  it fails. Write down which direction is safe for a new check and why.
- **A caught exception treated as a handled one.** If you add a swallow, it goes through
  `SwallowCounter` so its rate is visible and alarmed on.
- **A patch applied without being resolved first.** Add the target to the patch plan with a
  necessity and a reason; do not call `Harmony.Patch` directly.
- **A player count invented out of nothing.** Unknown is `-1`. Zero means the world is running and
  genuinely empty.
- **A game binary, a host IP, a panel path or a customer identifier** anywhere in the tree.

## Reporting a security issue

See `.github/SECURITY.md`. Please do not open a public issue for a vulnerability.
