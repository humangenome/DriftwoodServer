using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriftwoodHost
{
	// The record of what an owner DID: every kick, block, unblock and broadcast, who asked for
	// it, what it hit, and whether it worked. One line per action, append-only, in Logs\ under
	// the instance root - the directory that already holds the boot markers, outside the
	// customer's FTP jail and outside anything SteamCMD owns.
	//
	// WHY IT EXISTS: an owner action is a decision about a PERSON (somebody got removed,
	// somebody got banned), and the support ticket it generates arrives days later as "your
	// host kicked me for no reason". The audit line is the difference between an answer and a
	// shrug. It is also the honest record when the owner themselves is the confused party.
	//
	// Actor values are transport-derived, never claimed: "panel" is a loopback caller (the
	// hosting panel or the supervisor on this box), "console" is a remote caller that proved
	// itself with the server's signed API secret. A player without the secret cannot appear
	// here at all.
	//
	// Dependency-free so the xunit suite can link this file and prove the rotation and the
	// tail without a running game.
	internal static class OwnerAudit
	{
		private const long RotateBytes = 1024 * 1024;
		private const int TailCap = 200;

		private static readonly object Sync = new object();
		private static string _path = string.Empty;

		internal static void Initialise(string logsDirectory)
		{
			lock (Sync)
			{
				_path = string.IsNullOrWhiteSpace(logsDirectory)
					? string.Empty
					: Path.Combine(logsDirectory.Trim(), "owner-actions.log");
			}
		}

		// Best-effort by contract: the ACTION matters more than its record, so a full disk can
		// never turn a working kick into a failed one. A write failure is reported back so the
		// caller can append a warning to the command output instead of losing it silently.
		internal static string Record(string actor, string verb, string target, bool ok, string detail)
		{
			string line =
				DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\t" +
				Clean(actor) + "\t" + Clean(verb) + "\t" + Clean(target) + "\t" +
				(ok ? "ok" : "refused") + "\t" + Clean(detail);
			lock (Sync)
			{
				if (_path.Length == 0) return "the audit log has nowhere to live";
				try
				{
					string directory = Path.GetDirectoryName(_path);
					if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
					FileInfo info = new FileInfo(_path);
					if (info.Exists && info.Length > RotateBytes)
					{
						string previous = _path + ".1";
						if (File.Exists(previous)) File.Delete(previous);
						File.Move(_path, previous);
					}
					File.AppendAllText(_path, line + "\n", new UTF8Encoding(false));
					return null;
				}
				catch (Exception exception)
				{
					return "the audit line could not be written (" + exception.GetType().Name + ")";
				}
			}
		}

		// The newest lines, newest last, for the console's `audit` command. Bounded read: the
		// rotation above caps the file, and the tail caps the walk, so a huge history can never
		// stall the request thread that asked.
		internal static List<string> Tail(int count)
		{
			count = Math.Max(1, Math.Min(TailCap, count));
			lock (Sync)
			{
				if (_path.Length == 0 || !File.Exists(_path)) return new List<string>();
				try
				{
					string[] lines = File.ReadAllLines(_path);
					int start = Math.Max(0, lines.Length - count);
					List<string> tail = new List<string>(Math.Min(count, lines.Length));
					for (int i = start; i < lines.Length; i++)
					{
						if (lines[i].Length > 0) tail.Add(lines[i]);
					}
					return tail;
				}
				catch
				{
					return new List<string>();
				}
			}
		}

		// Tabs and newlines are this file's structure; a persona name or a broadcast body must
		// not be able to forge a column or a row.
		private static string Clean(string value)
		{
			if (string.IsNullOrEmpty(value)) return "-";
			StringBuilder builder = new StringBuilder(value.Length);
			foreach (char c in value)
			{
				builder.Append(c < ' ' || c == (char)0x7f ? ' ' : c);
				if (builder.Length >= 300) break;
			}
			string clean = builder.ToString().Trim();
			return clean.Length == 0 ? "-" : clean;
		}
	}
}
