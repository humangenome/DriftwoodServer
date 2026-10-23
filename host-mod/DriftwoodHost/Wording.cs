using System;

namespace DriftwoodHost
{
	// Text an owner reads in the console and the log. No Unity, BepInEx or game dependency, so
	// the xunit suite links this file.
	internal static class Wording
	{
		// "1 player", "3 players": the console used to print "player(s)".
		internal static string Count(int count, string noun) =>
			count + " " + (count == 1 ? noun : noun + "s");

		// The status headline. The reason usually starts with the phase already ("Hosting
		// \"Gull Rock\" on port 22043"), which printed as "Hosting - Hosting ...". Say it once.
		internal static string StatusHeadline(string phase, string reason)
		{
			phase = Words(phase ?? "");
			reason = reason ?? "";
			if (reason.Length == 0) return phase;
			if (phase.Length == 0 || reason.StartsWith(phase, StringComparison.OrdinalIgnoreCase)) return reason;
			return phase + ": " + reason;
		}

		// "WillNotHost" -> "Will not host": the phase names are enum members.
		private static string Words(string name)
		{
			var builder = new System.Text.StringBuilder(name.Length + 4);
			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (i > 0 && char.IsUpper(c)) builder.Append(' ').Append(char.ToLowerInvariant(c));
				else builder.Append(c);
			}
			return builder.ToString();
		}
	}
}
