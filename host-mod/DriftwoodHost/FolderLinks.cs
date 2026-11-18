using System;
using System.IO;

namespace DriftwoodHost
{
	// Is a folder that a world restore writes into, moves files through or deletes a real folder,
	// or a link to somewhere else? No Unity or BepInEx dependency, so the test project links it.
	//
	// A symbolic link, a junction or any other reparse point at one of those places sends the
	// restore's writes, moves and deletes into whatever folder it points at, and a recursive
	// delete that followed one would empty that folder. So each of those places is checked
	// before the restore acts and again right before each move.
	//
	// What is checked: the folder itself, and every folder between the server's instance folder
	// and it. What is NOT checked: the instance folder and everything above it. A hosting panel
	// may keep a whole server folder on a second drive behind one link, and that must keep working.
	//
	// The host mod targets netstandard2.1 on Unity's Mono, which has no FileSystemInfo.LinkTarget,
	// so the ReparsePoint attribute is the test. .NET on Linux reports a symbolic link with it too
	// (the tests check that before relying on it).
	internal static class FolderLinks
	{
		// A path that is there and cannot be read counts as a link: the answer to "is it safe to
		// write through?" is then no. A path that is not there is not a link.
		internal static bool IsLink(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			FileAttributes attributes;
			try
			{
				// No trailing separator: on Windows "C:\x\link\" reads the attributes of the folder
				// the link points to, not of the link.
				FileInfo info = new FileInfo(Trimmed(path));
				info.Refresh();
				attributes = info.Attributes;
			}
			catch (FileNotFoundException) { return false; }
			catch (DirectoryNotFoundException) { return false; }
			catch { return true; }
			if ((int)attributes == -1) return false;
			return (attributes & FileAttributes.ReparsePoint) != 0;
		}

		// Null when the folder, and every folder between instanceRoot and it, is a real folder or
		// is not there yet; otherwise one line naming the first link. A folder outside instanceRoot
		// (or with no instanceRoot) is checked on its own.
		internal static string Problem(string instanceRoot, string folder)
		{
			if (string.IsNullOrEmpty(folder)) return null;
			string full;
			try { full = Trimmed(Path.GetFullPath(folder)); }
			catch { return "the folder " + folder + " is not a valid path"; }

			string top = null;
			if (!string.IsNullOrEmpty(instanceRoot))
			{
				try { top = Trimmed(Path.GetFullPath(instanceRoot)); } catch { top = null; }
			}
			if (top != null && full.StartsWith(top + Path.DirectorySeparatorChar, PathCase))
			{
				string current = top;
				string[] parts = full.Substring(top.Length + 1).Split(
					new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string part in parts)
				{
					current = Path.Combine(current, part);
					if (IsLink(current)) return Describe(current);
					if (!Directory.Exists(current)) return null; // nothing below a level that is not there
				}
				return null;
			}
			return IsLink(full) ? Describe(full) : null;
		}

		// The first problem among the folders, or null.
		internal static string FirstProblem(string instanceRoot, params string[] folders)
		{
			foreach (string folder in folders)
			{
				string problem = Problem(instanceRoot, folder);
				if (problem != null) return problem;
			}
			return null;
		}

		// Deletes a folder this mod made (a staged restore, a swap's side folder), never through a
		// link: a folder that is a link is left alone and the answer is false.
		internal static bool DeleteIfReal(string folder)
		{
			if (IsLink(folder)) return false;
			if (Directory.Exists(folder)) Directory.Delete(Trimmed(folder), true);
			return true;
		}

		private static string Describe(string path)
		{
			return "the folder " + path + " is a link or junction";
		}

		private static string Trimmed(string path)
		{
			string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			// "C:\" and "/" stay as they are.
			return trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal) ? path : trimmed;
		}

		private static StringComparison PathCase
		{
			get { return Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; }
		}
	}
}
