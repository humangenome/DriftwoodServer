using System;
using System.Collections.Generic;

namespace DriftwoodHost
{
	// What the launcher's Mods tab reads.
	//
	// PUBLIC AND UNAUTHENTICATED, on purpose and for the same reason the sibling's is: a
	// player needs to know what a server runs BEFORE they have any credential for it, and a
	// mod list is not a secret - it is the thing that decides whether their client will be
	// let in.
	//
	// It publishes the server's REAL loaded plugin set, read from BepInEx's own chainloader
	// rather than from a directory listing, so it names what is actually running rather than
	// what happens to be on disk.
	//
	// The curated lists (required / recommended / blocked) exist in the payload and are
	// empty on a hosted instance, because this product ships no mod picker for this
	// game yet. The launcher hides an empty curated section rather than rendering a card
	// that apologises for itself - so an empty list here is silence in the UI, not noise.
	internal static class ModManifest
	{
		internal sealed class Entry
		{
			internal string Id = string.Empty;
			internal string Name = string.Empty;
			internal string Version = string.Empty;
			// True for the Driftwood stack itself, so the tab can say "stock Driftwood server"
			// instead of listing our own plugins at a player as though they were third-party
			// content they need to match.
			internal bool Ours;
		}

		private const string OurPrefix = "com.humangenome.driftwood.";

		internal static List<Entry> LoadedPlugins()
		{
			List<Entry> entries = new List<Entry>();
			try
			{
				foreach (KeyValuePair<string, BepInEx.PluginInfo> pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
				{
					BepInEx.PluginInfo info = pair.Value;
					if (info == null || info.Metadata == null) continue;
					string id = info.Metadata.GUID ?? string.Empty;
					entries.Add(new Entry
					{
						Id = id,
						Name = info.Metadata.Name ?? id,
						Version = info.Metadata.Version == null ? string.Empty : info.Metadata.Version.ToString(),
						Ours = id.StartsWith(OurPrefix, StringComparison.OrdinalIgnoreCase)
					});
				}
			}
			catch (Exception exception)
			{
				// A scan that throws must not take the manifest route down with it: the tab
				// would then read "couldn't reach this server", which is a different and untrue
				// statement. An empty list plus a log line is the honest answer.
				Plugin.Log?.LogWarning("Could not read the loaded plugin list: " + exception.Message);
			}

			entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
			return entries;
		}
	}
}
