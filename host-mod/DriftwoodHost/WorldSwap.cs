using System;
using System.Collections.Generic;
using System.IO;

namespace DriftwoodHost
{
	internal enum WorldSwapResult
	{
		// The staged world is now the world in the save folder.
		Applied,
		// The swap failed part way and the world that was there before is back in place.
		Untouched,
		// The swap failed and the old world could not be put back either. Hosting now would make
		// a brand-new empty world and autosave it over whatever is left, so the caller refuses.
		Broken
	}

	// Swaps a staged world into the save folder so that a failure part way leaves either the old
	// world or the new one, never an empty folder. No Unity or BepInEx dependency, so the test
	// project links it.
	//
	// Until 2026-10-30 a restore deleted every file in the save folder and then copied the staged
	// files in. A copy that failed (antivirus lock, disk, a process killed mid-copy) left the
	// folder empty, the boot carried on, found no world and created a fresh one: "restore OK"
	// followed by an empty island. Now, in order:
	//   1. copy the staged files into a sibling "incoming" folder (the old world is untouched);
	//   2. move the current files into a sibling "replaced" folder;
	//   3. move the incoming files into the save folder.
	// A failure in 1 changes nothing. A failure in 2 or 3 moves everything back. Moves within one
	// folder tree are renames, so 2 and 3 do not copy any data.
	internal static class WorldSwap
	{
		internal static WorldSwapResult Apply(string staging, string saveDirectory, out string failure)
		{
			failure = null;
			string trimmed = saveDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string incoming = trimmed + ".restore-incoming";
			string replaced = trimmed + ".restore-replaced";

			// A "replaced" folder that still holds files is a previous swap that could not put the
			// old world back. It may be the only copy of that world, so it is never cleared here.
			if (Directory.Exists(replaced) && Directory.GetFiles(replaced).Length > 0)
			{
				failure = "a previous restore left the old world's files in " + replaced +
					"; nothing was changed. Put them back in the save folder (or delete that folder) and start again";
				return WorldSwapResult.Broken;
			}

			// 1. Stage beside the save folder. Nothing in the save folder has changed yet.
			try
			{
				Directory.CreateDirectory(saveDirectory);
				ResetDirectory(incoming);
				ResetDirectory(replaced);
				foreach (string file in Directory.GetFiles(staging))
				{
					File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)), true);
				}
			}
			catch (Exception exception)
			{
				failure = "the staged files could not be copied next to the save folder (" + Describe(exception) + ")";
				TryDelete(incoming);
				return WorldSwapResult.Untouched;
			}

			// 2. Move the current world aside.
			List<string> movedAside = new List<string>();
			try
			{
				foreach (string file in Directory.GetFiles(saveDirectory))
				{
					string name = Path.GetFileName(file);
					File.Move(file, Path.Combine(replaced, name));
					movedAside.Add(name);
				}
			}
			catch (Exception exception)
			{
				failure = "the current world could not be moved aside (" + Describe(exception) + ")";
				return PutBack(replaced, saveDirectory, movedAside, new List<string>(), incoming, ref failure);
			}

			// 3. Move the staged world in.
			List<string> movedIn = new List<string>();
			try
			{
				foreach (string file in Directory.GetFiles(incoming))
				{
					string name = Path.GetFileName(file);
					File.Move(file, Path.Combine(saveDirectory, name));
					movedIn.Add(name);
				}
			}
			catch (Exception exception)
			{
				failure = "the restored world could not be moved into the save folder (" + Describe(exception) + ")";
				return PutBack(replaced, saveDirectory, movedAside, movedIn, incoming, ref failure);
			}

			TryDelete(incoming);
			TryDelete(replaced);
			return WorldSwapResult.Applied;
		}

		// Undo 3, then undo 2. Untouched when every old file is back and no new file is left.
		private static WorldSwapResult PutBack(string replaced, string saveDirectory, List<string> movedAside,
			List<string> movedIn, string incoming, ref string failure)
		{
			bool clean = true;
			foreach (string name in movedIn)
			{
				try { File.Delete(Path.Combine(saveDirectory, name)); }
				catch { clean = false; }
			}
			foreach (string name in movedAside)
			{
				try { File.Move(Path.Combine(replaced, name), Path.Combine(saveDirectory, name)); }
				catch { clean = false; }
			}
			if (!clean)
			{
				failure += "; the previous world could not be fully put back either, and what was moved aside is in " + replaced;
				return WorldSwapResult.Broken;
			}
			TryDelete(incoming);
			TryDelete(replaced);
			return WorldSwapResult.Untouched;
		}

		private static void ResetDirectory(string directory)
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, true);
			Directory.CreateDirectory(directory);
		}

		private static void TryDelete(string directory)
		{
			try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
		}

		private static string Describe(Exception exception)
		{
			return exception.GetType().Name + ": " + exception.Message;
		}
	}
}
