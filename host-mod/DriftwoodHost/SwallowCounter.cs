using System;
using System.Collections.Generic;
using System.Reflection;

namespace DriftwoodHost
{
	// Playbook 1d mechanism 3: "an exception caught and counted as handled". Lodestone swallowed
	// 250,000 exceptions in 47 minutes - 2,860 a second - and nothing looked broken, while the
	// garbage from the handler froze the world for 100-180 ms every second.
	//
	// So every swallow this mod performs is counted per target, the rate is computed, and the
	// counts are published in the readiness file where the supervisor and the panel can see them.
	// A swallow rate above the alarm threshold is reported as a fault, not as health.
	internal static class SwallowCounter
	{
		internal sealed class Entry
		{
			public string Method;
			public long Total;
			public long SinceLastWindow;
			public double PeakPerSecond;
			public string LastExceptionType;
			public string LastMessage;
		}

		// Above this, a swallow is not a rare edge case - it is a broken feature.
		internal const double AlarmPerSecond = 5.0;

		private static readonly object Sync = new object();
		private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
		private static DateTime _windowStart = DateTime.UtcNow;

		internal static void Record(MethodBase method, Exception exception)
		{
			string key = method == null
				? "unknown"
				: (method.DeclaringType == null ? "?" : method.DeclaringType.Name) + "." + method.Name;
			lock (Sync)
			{
				if (!Entries.TryGetValue(key, out Entry entry))
				{
					entry = new Entry { Method = key };
					Entries[key] = entry;
				}
				entry.Total++;
				entry.SinceLastWindow++;
				entry.LastExceptionType = exception.GetType().Name;
				entry.LastMessage = Truncate(exception.Message, 200);
			}
		}

		// Called on a timer by the plugin. Rolls the window and returns anything alarming.
		internal static List<Entry> Roll(out double windowSeconds)
		{
			List<Entry> alarming = new List<Entry>();
			lock (Sync)
			{
				DateTime now = DateTime.UtcNow;
				windowSeconds = Math.Max(0.001, (now - _windowStart).TotalSeconds);
				_windowStart = now;
				foreach (Entry entry in Entries.Values)
				{
					double perSecond = entry.SinceLastWindow / windowSeconds;
					if (perSecond > entry.PeakPerSecond) entry.PeakPerSecond = perSecond;
					if (perSecond >= AlarmPerSecond) alarming.Add(entry);
					entry.SinceLastWindow = 0;
				}
			}
			return alarming;
		}

		internal static List<Entry> Snapshot()
		{
			lock (Sync) return new List<Entry>(Entries.Values);
		}

		internal static long TotalSwallowed()
		{
			long total = 0;
			lock (Sync)
			{
				foreach (Entry entry in Entries.Values) total += entry.Total;
			}
			return total;
		}

		private static string Truncate(string value, int max) =>
			string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
	}
}
