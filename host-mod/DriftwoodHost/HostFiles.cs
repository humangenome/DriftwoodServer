using System;
using System.IO;
using System.Threading;

namespace DriftwoodHost
{
	// Puts a finished temporary file in place of the real one. No Unity or BepInEx dependency,
	// so the test project links it.
	//
	// Every state file this mod keeps used "delete the old file, then move the new one in". Two
	// things went wrong with that (audit 2026-10-30): a crash between the two steps left NO file,
	// which is an empty leaderboard, an empty block list or a missing status file; and on Windows
	// the delete throws while any reader holds the old file open (the self-host supervisor reads
	// the status file every 3 s), and an exception from a status write ended the coroutine that
	// called it for good. File.Replace swaps in one step, and a short retry rides out a reader.
	internal static class HostFiles
	{
		private const int Attempts = 3;

		// Throws only after the last attempt. The temporary file is the caller's to clean up.
		internal static void ReplaceWith(string temporary, string path)
		{
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					if (File.Exists(path)) File.Replace(temporary, path, null, true);
					else File.Move(temporary, path);
					return;
				}
				catch (IOException) when (attempt < Attempts)
				{
					Thread.Sleep(20 * attempt);
				}
				catch (UnauthorizedAccessException) when (attempt < Attempts)
				{
					Thread.Sleep(20 * attempt);
				}
			}
		}
	}
}
