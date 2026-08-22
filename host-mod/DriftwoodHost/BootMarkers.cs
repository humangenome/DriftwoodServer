using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DriftwoodHost
{
	// The two boot markers the panel asserts on.
	//
	// They live in <instance root>\Logs\, deliberately NOT in Saves\ - Saves\ is the customer's
	// FTP jail, and a marker a customer can write or delete proves nothing.
	//
	// The panel reads them back and reports the server STOPPED on a mismatch or an absence, so a
	// half-done save redirect or a missing required guard fails CLOSED instead of running as a
	// healthy-looking server that pools every customer's world into one directory.
	//
	// "OR AN ABSENCE" was only half true until 2026-08-22: the panel's guard check failed on a
	// missing marker and its save-root check PASSED on one. So a host whose save-root write threw
	// - which Write() catches and continues from, below - kept hosting with that assertion
	// silently downgraded to unverified, which is the one state it exists to catch. Both sides now
	// fail closed on absence, and the panel only asks the question once the host is listening,
	// which is what makes "absent" unambiguous rather than merely "not booted yet".
	internal static class BootMarkers
	{
		public const string SaveRootMarker = ".driftwood-saveroot";
		public const string GuardsMarker = ".driftwood-guards";

		internal static string LogsDirectory { get; private set; } = string.Empty;

		public static void Prepare(string logsDirectory)
		{
			LogsDirectory = logsDirectory;
			Directory.CreateDirectory(logsDirectory);
			// Clear both up front. A stale marker from a previous boot that then fails to write
			// would otherwise read as a pass, which is the exact failure these exist to catch.
			Delete(SaveRootMarker);
			Delete(GuardsMarker);
		}

		// The absolute save path the mod ACTUALLY resolved - not the one it was asked for. The
		// panel compares this against what it wrote.
		public static void WriteSaveRoot(string resolvedAbsolutePath)
		{
			Write(SaveRootMarker, resolvedAbsolutePath.TrimEnd('/', '\\'));
		}

		// One installed guard per line. A patch whose target was NOT FOUND must never appear
		// here: the panel treats the list as the truth about what is actually in force.
		public static void WriteGuards(IEnumerable<string> installedGuards)
		{
			StringBuilder builder = new StringBuilder();
			foreach (string guard in installedGuards) builder.Append(guard).Append('\n');
			Write(GuardsMarker, builder.ToString());
		}

		private static void Write(string name, string content)
		{
			if (string.IsNullOrEmpty(LogsDirectory)) return;
			string path = Path.Combine(LogsDirectory, name);
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				Directory.CreateDirectory(LogsDirectory);
				File.WriteAllText(temporary, content, new UTF8Encoding(false));
				if (File.Exists(path)) File.Delete(path);
				File.Move(temporary, path);
			}
			catch (Exception exception)
			{
				// Catch and continue is deliberate, and it is only defensible because the panel
				// treats an ABSENT marker as a failure. It does now, for both markers. If that
				// ever stops being true, this must throw instead.
				Plugin.Log?.LogError("Could not write the boot marker " + name + ": " + exception.Message +
					". The panel reads an absent marker as a failed start, so this server will report as down - which is correct.");
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
			}
		}

		private static void Delete(string name)
		{
			try
			{
				string path = Path.Combine(LogsDirectory, name);
				if (File.Exists(path)) File.Delete(path);
			}
			catch
			{
			}
		}
	}
}
