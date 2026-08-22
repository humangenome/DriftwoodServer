using System.Security.Cryptography;

namespace DriftwoodServer;

internal sealed record BuildPinResult(bool Ok, string Reason, string ActualAssemblyHash, string ActualBuildId);

// THE BUILD-PIN GATE.
//
// Steam updates the game under us. This game is days old and will patch fast, so the realistic
// failure is not "we shipped a bad build", it is "Steam quietly moved a live customer onto a build
// nobody has validated", and the host mod's patch targets are resolved by name against that build.
//
// Two independent checks, and both are needed:
//
//   1. THE ASSEMBLY HASH. Only Assembly-CSharp.dll identifies a build. The Unity launcher stub
//      (the .exe) does NOT change between versions - that was the Stormforge correction and it
//      applies here too, so pinning the exe would pass happily across a game update. This is the
//      authoritative check.
//
//   2. THE STEAM MANIFEST. appmanifest_<appid>.acf tells us what Steam THINKS is installed:
//      buildid, TargetBuildID, StateFlags and UpdateResult. It catches the states a hash cannot
//      describe - a half-applied update, a queued update, a failed update - including the case
//      where the files still hash correctly because the download has not landed yet.
//
// Playbook 1d requirement 5: a check that cannot make its decision FAILS, it does not pass. A
// missing manifest, an unreadable manifest or an unparseable StateFlags is a refusal, not a skip.
internal static class BuildPin
{
    public static BuildPinResult Verify(
        string gameRoot,
        string assemblyRelativePath,
        string expectedAssemblySha256,
        int appId,
        string? expectedBuildId,
        string? steamAppsDirectory)
    {
        string assemblyPath = Path.Combine(gameRoot, assemblyRelativePath);
        if (!File.Exists(assemblyPath))
        {
            return new BuildPinResult(false,
                $"This server will not start because {assemblyRelativePath} is missing from the game folder, so the installed build cannot be identified.",
                string.Empty, string.Empty);
        }

        string actualHash;
        using (FileStream stream = new(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        // Manifest first when we have one, because "Steam is mid-update" explains a hash mismatch
        // and is a more useful sentence for a support person than "the hash is wrong".
        SteamAppManifest? manifest = null;
        string manifestPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(steamAppsDirectory))
        {
            manifestPath = Path.Combine(steamAppsDirectory, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
            {
                return new BuildPinResult(false,
                    $"This server will not start because Steam's install record for app {appId} is missing, so there is no way to tell whether the game files are complete.",
                    actualHash, string.Empty);
            }
            try
            {
                manifest = SteamAppManifest.Parse(File.ReadAllText(manifestPath));
            }
            catch (IOException exception)
            {
                return new BuildPinResult(false,
                    $"This server will not start because Steam's install record could not be read ({exception.GetType().Name}), so there is no way to tell whether the game files are complete.",
                    actualHash, string.Empty);
            }

            if (!manifest.TryGetStateFlags(out int stateFlags))
            {
                return new BuildPinResult(false,
                    "This server will not start because Steam's install record does not say whether the game is fully installed.",
                    actualHash, manifest.BuildId);
            }
            if ((stateFlags & SteamAppManifest.StateFullyInstalled) == 0)
            {
                return new BuildPinResult(false,
                    $"This server will not start because Steam reports the game is not fully installed (state {stateFlags}). An install or update is probably still in progress.",
                    actualHash, manifest.BuildId);
            }
            if (!string.IsNullOrEmpty(manifest.UpdateResult) && manifest.UpdateResult != "0")
            {
                return new BuildPinResult(false,
                    $"This server will not start because Steam's last update of the game failed (result {manifest.UpdateResult}). The game files may be incomplete.",
                    actualHash, manifest.BuildId);
            }
            if (!string.IsNullOrEmpty(manifest.TargetBuildId) &&
                manifest.TargetBuildId != "0" &&
                manifest.TargetBuildId != manifest.BuildId)
            {
                return new BuildPinResult(false,
                    $"This server will not start because Steam has queued an update from build {manifest.BuildId} to build {manifest.TargetBuildId}. Driftwood is pinned to a validated build and will not run a half-updated game.",
                    actualHash, manifest.BuildId);
            }
            if (!string.IsNullOrWhiteSpace(expectedBuildId) && manifest.BuildId != expectedBuildId)
            {
                return new BuildPinResult(false,
                    $"This server will not start because the game has been updated: Steam reports build {manifest.BuildId} and Driftwood is validated against build {expectedBuildId}.",
                    actualHash, manifest.BuildId);
            }
        }

        if (!string.Equals(actualHash, expectedAssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            return new BuildPinResult(false,
                "This server will not start because the game's code has changed from the version Driftwood was validated against. The game has almost certainly been updated.",
                actualHash, manifest?.BuildId ?? string.Empty);
        }

        return new BuildPinResult(true, "Pinned build verified.", actualHash, manifest?.BuildId ?? string.Empty);
    }
}
