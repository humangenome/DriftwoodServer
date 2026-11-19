using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DriftwoodHost
{
	// The file half of world snapshots: unpacking an archive into the staged-restore folder, the
	// link checks around it, and pruning the store. No Unity or BepInEx dependency, so the test
	// project links it. SnapshotStore keeps the game half (saving the world first, the world
	// name, ending the process).
	internal static class SnapshotFiles
	{
		// UNPACKED SIZE, COUNTED AS THE BYTES REALLY COME OUT. The upload cap
		// (SnapshotStore.MaxImportBytes, also 64 MB) limits the zip, not what is inside it: a
		// zip of zeros a few hundred KB long can unpack to many GB and fill the drive. The sizes
		// a zip states are only what it claims, so every byte written is counted and the restore
		// stops at this line. A How to Fish world is a small text file (real backups of whole
		// save folders are tens of KB compressed), so the same 64 MB as the upload cap is far
		// above any real world and keeps one number for both ends.
		internal const long MaxUnpackedBytes = 64L * 1024L * 1024L;

		// FILES IN ONE ARCHIVE. A save folder holds a handful of files: each world's .txt, the
		// game's local.txt and this mod's leaderboard per world. A thousand is far above that and
		// still stops an archive of a million empty entries from making a million files.
		internal const int MaxEntries = 1000;

		// WHAT THE OWNER IS TOLD, word for word as approved on 2026-11-18. The first two name the
		// caps above (64 MB, 1,000 files); a test holds them together. Which folder was a link
		// goes to the host log only (LastLinkProblem), never to the owner.
		internal const string TooBigWords = "Your world is safe. That zip holds more than 64 MB, far more than a How to Fish world. Zip only the Saves folder and try again.";
		internal const string DamagedWords = "Your world is safe. That zip is damaged. Make the zip again and upload it.";
		internal const string TooManyFilesWords = "Your world is safe. That zip has more than 1,000 files. Zip only the Saves folder and try again.";
		internal const string LinkWords = "Nothing was changed. A folder on this server points somewhere else, so the restore stopped. Contact your host.";
		// A backup or an upload refused because the snapshot store is a link (approved 2026-11-18).
		internal const string BackupLinkWords = "Nothing was changed. A folder on this server points somewhere else, so the backup stopped. Contact your host.";

		// The folder behind the last LinkWords or BackupLinkWords refusal, for the host log. Callers
		// hold the snapshot store's lock.
		internal static string LastLinkProblem;

		// Anything unexpected during a restore, upload or backup. The owner gets this fixed sentence;
		// the exception's own text goes to the host log only (LastFailureDetail), because it can carry
		// the server's paths (Security, 2026-11-19).
		internal const string UnexpectedWords = "Your world is safe and nothing was changed. The server could not finish this. Try again, and contact your host if it happens again.";

		// The exception behind the last UnexpectedWords from Stage, for the host log.
		internal static string LastFailureDetail;

		// Null when a backup or an upload may write into the snapshot store, else the owner's words
		// (BackupLinkWords); which folder it was is in LastLinkProblem.
		internal static string StoreLinkProblem(string instanceRoot, string snapshotDirectory)
		{
			string problem = FolderLinks.Problem(instanceRoot, snapshotDirectory);
			if (problem == null) return null;
			LastLinkProblem = problem;
			return BackupLinkWords;
		}

		// The staged-restore folder inside the snapshot store. The leading dot keeps it out of the
		// *.zip listing.
		internal const string PendingName = ".pending-restore";

		internal static string PendingDirectory(string snapshotDirectory)
		{
			return Path.Combine(snapshotDirectory, PendingName);
		}

		// Null when every folder a restore writes into, moves files through or deletes is a real
		// folder: the snapshot store, the staged-restore folder in it, the save folder, and the
		// save folder's two side folders the swap uses. Checked before a restore is staged, and
		// again at the next start before the staged world is swapped in.
		internal static string RestoreLinkProblem(string instanceRoot, string snapshotDirectory, string saveDirectory)
		{
			return FolderLinks.FirstProblem(instanceRoot,
				snapshotDirectory,
				PendingDirectory(snapshotDirectory),
				saveDirectory,
				WorldSwap.IncomingFolder(saveDirectory),
				WorldSwap.ReplacedFolder(saveDirectory));
		}

		// Unpacks the archive into a fresh staged-restore folder. On any failure the staged
		// folder is thrown away and nothing else has changed.
		internal static bool Stage(string archivePath, string instanceRoot, string snapshotDirectory, string saveDirectory, out string failure)
		{
			return Stage(archivePath, instanceRoot, snapshotDirectory, saveDirectory, MaxUnpackedBytes, MaxEntries, out failure);
		}

		internal static bool Stage(string archivePath, string instanceRoot, string snapshotDirectory, string saveDirectory,
			long maxBytes, int maxEntries, out string failure)
		{
			string pending = PendingDirectory(snapshotDirectory);
			// Before anything is deleted: a previous staged restore is removed below, and removing
			// it through a link would remove whatever the link points at.
			string link = RestoreLinkProblem(instanceRoot, snapshotDirectory, saveDirectory);
			if (link != null)
			{
				LastLinkProblem = link;
				failure = LinkWords;
				return false;
			}
			try
			{
				// A previous staged restore that never got applied is superseded by this one, not
				// merged with it.
				FolderLinks.DeleteIfReal(pending);
				Directory.CreateDirectory(pending);
				// Again, right before the unpack writes anything.
				link = RestoreLinkProblem(instanceRoot, snapshotDirectory, saveDirectory);
				if (link != null)
				{
					LastLinkProblem = link;
					failure = LinkWords;
					Discard(pending);
					return false;
				}
				if (!Extract(archivePath, pending, maxBytes, maxEntries, out failure))
				{
					Discard(pending);
					return false;
				}
			}
			catch (Exception exception)
			{
				LastFailureDetail = exception.GetType().Name + ": " + exception.Message;
				failure = UnexpectedWords;
				Discard(pending);
				return false;
			}
			return true;
		}

		// Never through a link.
		internal static void Discard(string directory)
		{
			try { FolderLinks.DeleteIfReal(directory); } catch { }
		}

		// ZIP SLIP. An archive entry name is attacker-controlled - the launcher lets a player
		// upload any zip they like - and "..\..\How to Fish\BepInEx\plugins\evil.dll" is a
		// perfectly legal entry name. Every entry is cut to its bare file name, resolved against
		// the destination and refused if it lands outside it.
		//
		// A single common leading directory is stripped, because a customer zipping their own
		// Saves folder produces "Saves/world.txt" and refusing that would be pedantry.
		//
		// The size checks count what really comes out: an entry that holds more than it states is
		// refused, and so is an archive whose files together pass maxBytes. The copy stops before
		// the chunk that would cross either line, so no more than maxBytes is ever written.
		internal static bool Extract(string archivePath, string destination, long maxBytes, int maxEntries, out string failure)
		{
			failure = null;
			string destinationFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			using (ZipArchive archive = ZipFile.OpenRead(archivePath))
			{
				if (archive.Entries.Count > maxEntries)
				{
					failure = TooManyFilesWords;
					return false;
				}
				long left = maxBytes;
				string prefix = CommonPrefix(archive);
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name)) continue; // a directory entry
					string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
					if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						relative = relative.Substring(prefix.Length);
					}
					// Flatten. This game keeps every save file directly in Saves\, so a nested
					// path in an archive is either noise or an attempt at something.
					relative = Path.GetFileName(relative);
					if (relative.Length == 0) continue;

					string target = Path.GetFullPath(Path.Combine(destination, relative));
					if (!target.StartsWith(destinationFull, StringComparison.OrdinalIgnoreCase))
					{
						failure = "that archive tries to write outside this server's save directory";
						return false;
					}
					using (Stream source = entry.Open())
					using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						if (!CopyCapped(source, output, entry.Length, ref left, maxBytes, out failure)) return false;
					}
					// What ExtractToFile did: the file keeps the time the archive gives it.
					try { File.SetLastWriteTime(target, entry.LastWriteTime.DateTime); } catch { }
				}
			}
			return true;
		}

		// Copies one entry, counting the bytes as they really come out. False, before the chunk
		// that crosses the line is written, when the entry holds more than it states or the
		// archive passes its cap. Some runtimes stop a compressed entry at its stated size and
		// some do not, and a stored entry is read for as long as its data runs, so this does not
		// rely on either.
		internal static bool CopyCapped(Stream source, Stream destination, long statedLength, ref long left, long maxBytes, out string failure)
		{
			failure = null;
			byte[] buffer = new byte[81920];
			long written = 0;
			int read;
			while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
			{
				if (written + read > statedLength)
				{
					failure = DamagedWords;
					return false;
				}
				if (read > left)
				{
					failure = TooBigWords;
					return false;
				}
				destination.Write(buffer, 0, read);
				written += read;
				left -= read;
			}
			return true;
		}

		private static string CommonPrefix(ZipArchive archive)
		{
			string prefix = null;
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				if (string.IsNullOrEmpty(entry.Name)) continue;
				string full = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				int slash = full.IndexOf(Path.DirectorySeparatorChar);
				if (slash <= 0) return string.Empty; // something sits at the root: no common prefix
				string head = full.Substring(0, slash + 1);
				if (prefix == null) prefix = head;
				else if (!string.Equals(prefix, head, StringComparison.OrdinalIgnoreCase)) return string.Empty;
			}
			return prefix ?? string.Empty;
		}

		// Newest keepNewest archives kept, plus keepId: a restore passes the archive it is
		// restoring from, because with the store full the pre-restore copy made one too many and
		// pruning deleted the oldest, so restoring the oldest snapshot deleted it and then failed
		// (audit 2026-10-30). Nothing is deleted when the store is a link.
		internal static void Prune(string instanceRoot, string snapshotDirectory, int keepNewest, string keepId)
		{
			try
			{
				if (FolderLinks.Problem(instanceRoot, snapshotDirectory) != null) return;
				List<FileInfo> all = new List<FileInfo>();
				foreach (string file in Directory.GetFiles(snapshotDirectory, "*.zip"))
				{
					try { all.Add(new FileInfo(file)); } catch { }
				}
				all.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
				int kept = 0;
				foreach (FileInfo info in all)
				{
					string id = Path.GetFileNameWithoutExtension(info.Name);
					if (!string.IsNullOrEmpty(keepId) && string.Equals(id, keepId, StringComparison.Ordinal)) continue;
					if (kept++ < keepNewest) continue;
					try { File.Delete(info.FullName); } catch { }
				}
			}
			catch { }
		}
	}
}
