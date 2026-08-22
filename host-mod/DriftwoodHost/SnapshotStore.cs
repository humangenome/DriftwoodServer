using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DriftwoodHost
{
	// World snapshots: the launcher's "World backups" dialog.
	//
	// WHY IT LIVES IN THE MOD AND NOT IN A SUPERVISOR. Every sibling puts snapshots in its
	// .NET supervisor. Driftwood's supervisor question is OPEN (review item S1: the .NET
	// DriftwoodServer is unshipped, production is an inline PowerShell loop that cannot serve
	// HTTP), so building on it would make this feature's existence depend on a decision
	// nobody has taken. The host mod is the one component that is definitely on the fleet -
	// the firewall rule itself is derived from whether DriftwoodHost.dll is installed - so
	// the API lives here and the S1 decision cannot invalidate it.
	//
	// THE ONE THING IT DOES DEPEND ON: restoring a world requires the process to end and be
	// brought back. Both candidate supervisors relaunch a host that exits - that is what a
	// supervisor is - so the dependency is on the property, not on which one wins.
	internal static class SnapshotStore
	{
		// A How to Fish world is a text file, so this bound is enormous relative to a real
		// save. It is here to stop an upload from filling a customer's drive, not to be a
		// realistic size.
		internal const long MaxImportBytes = 64L * 1024L * 1024L;

		// Newest N kept. A snapshot per restore plus a manual one now and then is a slow
		// drip, but "slow" and "bounded" are different words and only one of them is safe on
		// a shared box.
		private const int KeepNewest = 20;

		private static string _saveDirectory = string.Empty;
		private static string _snapshotDirectory = string.Empty;
		private static string _worldName = string.Empty;
		private static readonly object Sync = new object();

		// path -> (size, mtime ticks, sha256). Hashing a small text zip is cheap, but the
		// launcher lists snapshots on every dialog open and every refresh, and rehashing the
		// whole store each time is work nobody asked for.
		private static readonly Dictionary<string, string[]> HashCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

		internal sealed class Summary
		{
			internal string Id = string.Empty;
			internal long TakenUnix;
			internal long SizeBytes;
			internal string Sha256 = string.Empty;
		}

		internal static bool Ready => _saveDirectory.Length > 0 && _snapshotDirectory.Length > 0;

		internal static string Directory_ => _snapshotDirectory;

		internal static void Initialise(string saveDirectory, string instanceRoot, string worldName)
		{
			_saveDirectory = (saveDirectory ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
			_worldName = (worldName ?? string.Empty).Trim();
			string root = (instanceRoot ?? string.Empty).Trim();
			if (root.Length == 0 || _saveDirectory.Length == 0)
			{
				// Not fatal, and not silent. Without a resolved pair the snapshot routes answer
				// "not available on this server" instead of pretending to work.
				Plugin.Log?.LogWarning("World snapshots are unavailable: the save directory or the instance root could not be resolved.");
				_snapshotDirectory = string.Empty;
				return;
			}
			_snapshotDirectory = Path.Combine(root, "Snapshots");
			try { System.IO.Directory.CreateDirectory(_snapshotDirectory); }
			catch (Exception exception)
			{
				Plugin.Log?.LogWarning("World snapshots are unavailable: " + _snapshotDirectory + " could not be created (" + exception.Message + ").");
				_snapshotDirectory = string.Empty;
			}
		}

		// ------------------------------------------------------------------
		// Listing
		// ------------------------------------------------------------------
		internal static List<Summary> List()
		{
			List<Summary> list = new List<Summary>();
			if (!Ready) return list;
			string[] files;
			try { files = System.IO.Directory.GetFiles(_snapshotDirectory, "*.zip"); }
			catch { return list; }

			foreach (string file in files)
			{
				try
				{
					FileInfo info = new FileInfo(file);
					list.Add(new Summary
					{
						Id = Path.GetFileNameWithoutExtension(file),
						TakenUnix = ToUnix(info.LastWriteTimeUtc),
						SizeBytes = info.Length,
						Sha256 = CachedHash(info)
					});
				}
				catch { }
			}
			list.Sort((a, b) => b.TakenUnix.CompareTo(a.TakenUnix));
			return list;
		}

		// ------------------------------------------------------------------
		// Creating
		// ------------------------------------------------------------------
		// FLUSH FIRST, ALWAYS. A snapshot taken before the world is flushed captures the file
		// the flush was about to replace - a valid-looking archive of stale data, which is the
		// worst possible failure for a backup because it only shows up when it is restored.
		internal static bool Create(string reason, out string id, out string failure)
		{
			id = string.Empty;
			failure = null;
			if (!Ready)
			{
				failure = "this server has no snapshot storage configured";
				return false;
			}

			string saveFailure;
			if (!MainThread.Run(WorldLifecycle.SaveNow, 15000, out saveFailure))
			{
				// Refuse rather than snapshot anyway. An unflushed snapshot is the stale-archive
				// trap above, and a customer who is told "could not save" will retry; one handed
				// a silently stale backup will not.
				failure = "the world could not be saved before the snapshot (" + saveFailure + ")";
				return false;
			}

			lock (Sync)
			{
				string candidate = NewId(reason);
				string path = Path.Combine(_snapshotDirectory, candidate + ".zip");
				try
				{
					if (!System.IO.Directory.Exists(_saveDirectory))
					{
						failure = "this server's save directory does not exist yet";
						return false;
					}
					if (File.Exists(path)) File.Delete(path);
					ZipDirectory(_saveDirectory, path);
					id = candidate;
				}
				catch (Exception exception)
				{
					try { if (File.Exists(path)) File.Delete(path); } catch { }
					failure = exception.GetType().Name + ": " + exception.Message;
					return false;
				}
				Prune();
			}
			Plugin.Log?.LogInfo("World snapshot " + id + " taken (" + reason + ").");
			return true;
		}

		// ------------------------------------------------------------------
		// Resolving an id from a URL. Path traversal lives here or nowhere.
		// ------------------------------------------------------------------
		internal static string ResolvePath(string id)
		{
			if (!Ready || string.IsNullOrEmpty(id)) return null;
			foreach (char c in id)
			{
				bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
					(c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
				if (!allowed) return null;
			}
			// ".." is composed entirely of allowed characters, so the character filter above is
			// not on its own enough. Both checks, deliberately.
			if (id.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
			string path = Path.Combine(_snapshotDirectory, id + ".zip");
			string full = Path.GetFullPath(path);
			if (!full.StartsWith(Path.GetFullPath(_snapshotDirectory) + Path.DirectorySeparatorChar.ToString(), StringComparison.OrdinalIgnoreCase)) return null;
			return File.Exists(full) ? full : null;
		}

		internal static string HashOf(string path)
		{
			try { return CachedHash(new FileInfo(path)); }
			catch { return string.Empty; }
		}

		// ------------------------------------------------------------------
		// Importing an uploaded zip
		// ------------------------------------------------------------------
		// The uploaded file has already been written to a temp path by the HTTP layer (which
		// streams it and never buffers a body in memory). This validates it, files it in the
		// store as a real snapshot, and hands back its id.
		internal static bool Adopt(string temporaryZipPath, out string id, out string failure)
		{
			id = string.Empty;
			failure = null;
			if (!Ready)
			{
				failure = "this server has no snapshot storage configured";
				return false;
			}
			try
			{
				using (ZipArchive archive = ZipFile.OpenRead(temporaryZipPath))
				{
					if (archive.Entries.Count == 0)
					{
						failure = "that file is empty";
						return false;
					}
				}
			}
			catch (Exception)
			{
				failure = "that file is not a readable zip archive";
				return false;
			}

			lock (Sync)
			{
				string candidate = NewId("imported");
				string path = Path.Combine(_snapshotDirectory, candidate + ".zip");
				try
				{
					File.Copy(temporaryZipPath, path, true);
					id = candidate;
				}
				catch (Exception exception)
				{
					failure = exception.GetType().Name + ": " + exception.Message;
					return false;
				}
				Prune();
			}
			return true;
		}

		// ------------------------------------------------------------------
		// Restoring
		// ------------------------------------------------------------------
		// Order, and every step of it is load-bearing:
		//   1. snapshot what is there now, so a restore is never a one-way door;
		//   2. extract into a staging directory, so a corrupt archive cannot half-replace a
		//      live world;
		//   3. reconcile the world NAME (see below);
		//   4. swap the staged tree into place;
		//   5. end the process, so the supervisor brings the world back on the restored save.
		internal static bool Restore(string id, out string failure)
		{
			failure = null;
			string archivePath = ResolvePath(id);
			if (archivePath == null)
			{
				failure = "that snapshot does not exist on this server";
				return false;
			}

			lock (Sync)
			{
				string preId;
				string preFailure;
				if (!Create("pre-restore", out preId, out preFailure))
				{
					// A world that cannot be saved is a world we cannot safely replace: without
					// the pre-restore copy the customer has no way back if the archive turns out
					// to be the wrong one.
					failure = "the current world could not be backed up first (" + preFailure + ")";
					return false;
				}

				string staging = Path.Combine(_snapshotDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
				try
				{
					System.IO.Directory.CreateDirectory(staging);
					if (!Extract(archivePath, staging, out failure)) return false;
					if (!ReconcileWorldName(staging, out failure)) return false;
					SwapIn(staging);
				}
				catch (Exception exception)
				{
					failure = exception.GetType().Name + ": " + exception.Message;
					return false;
				}
				finally
				{
					try { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); } catch { }
				}
			}

			Plugin.Log?.LogWarning("World restored from snapshot " + id + ". Ending the process so the supervisor brings the server back on the restored world.");
			return true;
		}

		// Called by the HTTP layer AFTER the response has been written, so the launcher sees
		// its 200 rather than a dropped connection.
		internal static void EndProcessForRestore()
		{
			Thread thread = new Thread(() =>
			{
				try { Thread.Sleep(750); } catch { }
				string ignored;
				MainThread.Run(() => UnityEngine.Application.Quit(0), 5000, out ignored);
				try { Thread.Sleep(15000); } catch { }
				// Application.Quit is a request, and a batch-mode player with a stuck scene has
				// been seen to ignore it. A restore that leaves the old world running is worse
				// than an abrupt exit, because the next autosave writes the world we just
				// replaced back over the restored one.
				try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
			});
			thread.IsBackground = true;
			thread.Name = "Driftwood.RestoreExit";
			thread.Start();
		}

		// THE TRAP THIS FUNCTION EXISTS FOR. The world the server loads is Saves\<WorldName>.txt
		// and WorldName comes from the panel, not from the archive. Restore somebody's
		// "MyIsland.txt" onto a server configured for "Driftwood" and the files land, the
		// server starts, finds no Driftwood.txt, CREATES AN EMPTY ONE - and the customer is
		// looking at a brand new world having just been told the restore succeeded.
		private static bool ReconcileWorldName(string staging, out string failure)
		{
			failure = null;
			if (_worldName.Length == 0) return true;
			string target = _worldName + ".txt";
			string targetPath = Path.Combine(staging, target);
			if (File.Exists(targetPath)) return true;

			List<string> worlds = new List<string>();
			foreach (string file in System.IO.Directory.GetFiles(staging, "*.txt"))
			{
				// local.txt is the single-player/client save the game keeps beside the server
				// worlds. It is never the world a dedicated server loads.
				if (string.Equals(Path.GetFileName(file), "local.txt", StringComparison.OrdinalIgnoreCase)) continue;
				worlds.Add(file);
			}

			if (worlds.Count == 1)
			{
				string from = Path.GetFileName(worlds[0]);
				File.Move(worlds[0], targetPath);
				Plugin.Log?.LogWarning("The imported world was named \"" + from +
					"\" and this server loads \"" + target + "\"; it has been renamed so the restore actually takes effect.");
				return true;
			}

			failure = worlds.Count == 0
				? "that archive contains no world file"
				: "that archive contains " + worlds.Count + " worlds and none of them is \"" + target +
					"\", so this server cannot tell which one to load";
			return false;
		}

		private static void SwapIn(string staging)
		{
			System.IO.Directory.CreateDirectory(_saveDirectory);
			foreach (string file in System.IO.Directory.GetFiles(_saveDirectory))
			{
				try { File.Delete(file); } catch { }
			}
			foreach (string file in System.IO.Directory.GetFiles(staging))
			{
				File.Copy(file, Path.Combine(_saveDirectory, Path.GetFileName(file)), true);
			}
		}

		// ------------------------------------------------------------------
		// Zip helpers
		// ------------------------------------------------------------------
		private static void ZipDirectory(string sourceDirectory, string destinationZip)
		{
			using (FileStream stream = new FileStream(destinationZip, FileMode.Create, FileAccess.Write, FileShare.None))
			using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
			{
				foreach (string file in System.IO.Directory.GetFiles(sourceDirectory))
				{
					archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
				}
			}
		}

		// ZIP SLIP. An archive entry name is attacker-controlled - the launcher lets a player
		// upload any zip they like - and "..\..\How to Fish\BepInEx\plugins\evil.dll" is a
		// perfectly legal entry name. Every entry is resolved against the destination and
		// refused if it lands outside it.
		//
		// A single common leading directory is stripped, because a customer zipping their own
		// Saves folder produces "Saves/world.txt" and refusing that would be pedantry.
		private static bool Extract(string archivePath, string destination, out string failure)
		{
			failure = null;
			string destinationFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			using (ZipArchive archive = ZipFile.OpenRead(archivePath))
			{
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
					entry.ExtractToFile(target, true);
				}
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

		// ------------------------------------------------------------------
		// Bookkeeping
		// ------------------------------------------------------------------
		private static void Prune()
		{
			try
			{
				List<Summary> all = List();
				for (int i = KeepNewest; i < all.Count; i++)
				{
					string path = Path.Combine(_snapshotDirectory, all[i].Id + ".zip");
					try { File.Delete(path); } catch { }
				}
			}
			catch { }
		}

		private static string NewId(string reason)
		{
			string safe = string.Empty;
			foreach (char c in reason ?? string.Empty)
			{
				if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') safe += c;
				else if (c >= 'A' && c <= 'Z') safe += char.ToLowerInvariant(c);
			}
			if (safe.Length == 0) safe = "manual";
			if (safe.Length > 16) safe = safe.Substring(0, 16);
			return "driftwood-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
				"-" + safe + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
		}

		private static string CachedHash(FileInfo info)
		{
			string key = info.FullName;
			string size = info.Length.ToString(CultureInfo.InvariantCulture);
			string stamp = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
			lock (HashCache)
			{
				string[] cached;
				if (HashCache.TryGetValue(key, out cached) && cached[0] == size && cached[1] == stamp)
				{
					return cached[2];
				}
			}
			string hash = FileHash(info.FullName);
			lock (HashCache)
			{
				if (HashCache.Count > 256) HashCache.Clear();
				HashCache[key] = new[] { size, stamp, hash };
			}
			return hash;
		}

		private static string FileHash(string path)
		{
			try
			{
				using (SHA256 sha = SHA256.Create())
				using (FileStream stream = File.OpenRead(path))
				{
					return ApiSignature.HexEncode(sha.ComputeHash(stream));
				}
			}
			catch { return string.Empty; }
		}

		private static long ToUnix(DateTime utc)
		{
			return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		}
	}

}
