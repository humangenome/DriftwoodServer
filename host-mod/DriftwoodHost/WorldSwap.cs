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
	//
	// LINKS. The staged folder, the save folder and the two side folders must be real folders,
	// and a staged file must be a real file, checked before anything happens and again right
	// before every copy and move. A link at any of them would send the world's files somewhere
	// else, or bring files from somewhere else into the world.
	internal static class WorldSwap
	{
		// Tests only: runs right before the check that guards each copy or move, with that file's
		// destination, so a test can swap a folder for a link at exactly that moment. Null otherwise.
		internal static Action<string> TestBeforeMove { get; set; }

		internal static string IncomingFolder(string saveDirectory)
		{
			return Trimmed(saveDirectory) + ".restore-incoming";
		}

		internal static string ReplacedFolder(string saveDirectory)
		{
			return Trimmed(saveDirectory) + ".restore-replaced";
		}

		internal static WorldSwapResult Apply(string staging, string saveDirectory, out string failure)
		{
			failure = null;
			string incoming = IncomingFolder(saveDirectory);
			string replaced = ReplacedFolder(saveDirectory);

			string link = FolderLinks.FirstProblem(null, staging, saveDirectory, incoming, replaced);
			if (link != null)
			{
				failure = link + ", so nothing was changed";
				return WorldSwapResult.Untouched;
			}

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
					string destination = Path.Combine(incoming, Path.GetFileName(file));
					TestBeforeMove?.Invoke(destination);
					link = LinkProblem(file, staging, saveDirectory, incoming, replaced);
					if (link != null)
					{
						failure = link + ", so nothing was changed";
						TryDelete(incoming);
						return WorldSwapResult.Untouched;
					}
					File.Copy(file, destination, true);
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
					string destination = Path.Combine(replaced, name);
					TestBeforeMove?.Invoke(destination);
					link = LinkProblem(null, saveDirectory, incoming, replaced);
					if (link != null) throw new IOException(link);
					File.Move(file, destination);
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
					string destination = Path.Combine(saveDirectory, name);
					TestBeforeMove?.Invoke(destination);
					link = LinkProblem(file, saveDirectory, incoming, replaced);
					if (link != null) throw new IOException(link);
					File.Move(file, destination);
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
			// Putting back goes through the same two folders, so they are checked the same way: a
			// link that appeared there stops the put-back and the swap counts as broken.
			foreach (string name in movedIn)
			{
				if (LinkProblem(null, saveDirectory, replaced) != null) { clean = false; break; }
				try { File.Delete(Path.Combine(saveDirectory, name)); }
				catch { clean = false; }
			}
			foreach (string name in movedAside)
			{
				if (LinkProblem(null, saveDirectory, replaced) != null) { clean = false; break; }
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

		// Null, or the first link among the folders and the one file (which may be null).
		private static string LinkProblem(string file, params string[] folders)
		{
			if (file != null && FolderLinks.IsLink(file)) return "the staged file " + file + " is a link";
			return FolderLinks.FirstProblem(null, folders);
		}

		private static void ResetDirectory(string directory)
		{
			if (!FolderLinks.DeleteIfReal(directory)) throw new IOException("the folder " + directory + " is a link or junction");
			Directory.CreateDirectory(directory);
		}

		// Never through a link.
		private static void TryDelete(string directory)
		{
			try { FolderLinks.DeleteIfReal(directory); } catch { }
		}

		private static string Trimmed(string path)
		{
			return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}

		private static string Describe(Exception exception)
		{
			return exception.GetType().Name + ": " + exception.Message;
		}
	}
}
