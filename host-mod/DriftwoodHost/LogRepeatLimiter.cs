using System;
using System.Collections.Generic;
using System.Text;

namespace DriftwoodHost
{
	// Decides which error and warning lines reach the game's log. No Unity, BepInEx or game
	// dependency, so the xunit suite links this file; LogFloodGuard is the Unity side.
	//
	// Why it exists: on game 1.1.3 one lab session (2026-10-23) had ProjectileManager.
	// UpdateProjectileScan throw the same NullReferenceException 16,571 times in about five
	// minutes, 55 a second and 5 MB of log, while three other sessions on the same build never
	// threw it once. A server that stays up for days with that running writes about a gigabyte a
	// day into one file, and every line is the same line.
	//
	// The rule: the same line (digits blanked, so "slot 3" and "slot 4" are one line) is written
	// at most PerKey times a window, and no more than TotalPerWindow error and warning lines are
	// written a window in all. Everything held back is counted, and when the window ends the
	// caller writes one summary line per held-back line saying how many times it came. Nothing is
	// dropped without a count, and the first occurrences are always written in full.
	internal sealed class LogRepeatLimiter
	{
		internal sealed class Summary
		{
			public string Sample;
			public long HeldBack;
			public double WindowSeconds;
			public bool OverTotal;
		}

		private sealed class Counter
		{
			public string Sample;
			public int Seen;
			public long HeldBack;
		}

		internal const int DefaultPerKey = 5;
		internal const int DefaultTotalPerWindow = 300;
		internal const double DefaultWindowSeconds = 60.0;
		// Distinct lines tracked per window. Past this, a new line is still written (it cannot be
		// a repeat of anything tracked) but only the total cap applies to it.
		internal const int MaxKeys = 512;
		// Summary lines written for one window. A window with more held-back lines than this
		// gets one extra line saying how many were not listed.
		internal const int MaxSummaries = 20;
		private const int MaxKeyLength = 300;

		private readonly object _sync = new object();
		private readonly Dictionary<string, Counter> _counts = new Dictionary<string, Counter>(StringComparer.Ordinal);
		private readonly List<Summary> _pending = new List<Summary>();
		private readonly Func<DateTime> _clock;
		private DateTime _windowStart;
		private int _writtenThisWindow;
		private long _overTotalThisWindow;
		private long _totalHeldBack;

		internal int PerKey { get; }
		internal int TotalPerWindow { get; }
		internal double WindowSeconds { get; }

		internal LogRepeatLimiter(Func<DateTime> clock = null, int perKey = DefaultPerKey,
			int totalPerWindow = DefaultTotalPerWindow, double windowSeconds = DefaultWindowSeconds)
		{
			_clock = clock ?? (() => DateTime.UtcNow);
			PerKey = Math.Max(1, perKey);
			TotalPerWindow = Math.Max(PerKey, totalPerWindow);
			WindowSeconds = Math.Max(1.0, windowSeconds);
			_windowStart = _clock();
		}

		internal long TotalHeldBack
		{
			get { lock (_sync) return _totalHeldBack; }
		}

		// True = write this line. False = it was counted and will be in the window's summary.
		internal bool Admit(string line)
		{
			string text = line ?? "";
			string key = Normalise(text);
			lock (_sync)
			{
				RollIfDue();
				if (!_counts.TryGetValue(key, out Counter counter) && _counts.Count < MaxKeys)
				{
					counter = new Counter { Sample = Truncate(text) };
					_counts[key] = counter;
				}
				if (counter != null)
				{
					counter.Seen++;
					if (counter.Seen > PerKey)
					{
						counter.HeldBack++;
						_totalHeldBack++;
						return false;
					}
				}
				if (_writtenThisWindow >= TotalPerWindow)
				{
					// A first occurrence that arrives over the total cap is still counted against
					// its own line, so the summary names it rather than folding it into "others".
					if (counter != null) counter.HeldBack++;
					else _overTotalThisWindow++;
					_totalHeldBack++;
					return false;
				}
				_writtenThisWindow++;
				return true;
			}
		}

		// Summaries for windows that have ended, oldest first. The caller writes them; calling
		// this also ends the current window when it is due, so a quiet log still gets its summary.
		internal List<Summary> TakeSummaries()
		{
			lock (_sync)
			{
				RollIfDue();
				List<Summary> taken = new List<Summary>(_pending);
				_pending.Clear();
				return taken;
			}
		}

		private void RollIfDue()
		{
			DateTime now = _clock();
			double elapsed = (now - _windowStart).TotalSeconds;
			if (elapsed < WindowSeconds) return;

			List<Counter> heldBack = new List<Counter>();
			foreach (Counter counter in _counts.Values)
			{
				if (counter.HeldBack > 0) heldBack.Add(counter);
			}
			heldBack.Sort((a, b) => b.HeldBack.CompareTo(a.HeldBack));
			int listed = Math.Min(heldBack.Count, MaxSummaries);
			for (int i = 0; i < listed; i++)
			{
				_pending.Add(new Summary { Sample = heldBack[i].Sample, HeldBack = heldBack[i].HeldBack, WindowSeconds = elapsed });
			}
			long unlisted = _overTotalThisWindow;
			for (int i = listed; i < heldBack.Count; i++) unlisted += heldBack[i].HeldBack;
			if (unlisted > 0)
			{
				_pending.Add(new Summary { Sample = null, HeldBack = unlisted, WindowSeconds = elapsed, OverTotal = true });
			}
			// Bound what waits for a caller that never comes back (no log lines and no flush).
			if (_pending.Count > MaxSummaries * 4) _pending.RemoveRange(0, _pending.Count - MaxSummaries * 4);

			_counts.Clear();
			_writtenThisWindow = 0;
			_overTotalThisWindow = 0;
			_windowStart = now;
		}

		// The line an owner reads in the log in place of the repeats.
		internal string SummaryLine(Summary summary)
		{
			string seconds = Math.Round(summary.WindowSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (summary.OverTotal)
			{
				return "Driftwood log guard: " + summary.HeldBack + " more error and warning lines in the last " + seconds +
					" s were not written (at most " + TotalPerWindow + " a minute are).";
			}
			return "Driftwood log guard: the line below came " + summary.HeldBack + " more times in the last " + seconds +
				" s and was not written again (the same line is written " + PerKey + " times a minute at most): " + summary.Sample;
		}

		// One key per exception: its type, message and the frame it was thrown from, so two
		// different NullReferenceExceptions stay two lines.
		internal static string ExceptionLine(string typeName, string message, string stackTrace)
		{
			string top = "";
			if (!string.IsNullOrEmpty(stackTrace))
			{
				int end = stackTrace.IndexOf('\n');
				top = (end < 0 ? stackTrace : stackTrace.Substring(0, end)).Trim();
			}
			return (typeName ?? "Exception") + ": " + (message ?? "") + (top.Length > 0 ? " @ " + top : "");
		}

		// Digits blanked, whitespace collapsed, length capped.
		internal static string Normalise(string text)
		{
			StringBuilder builder = new StringBuilder(Math.Min(text.Length, MaxKeyLength));
			bool inDigits = false;
			bool inSpace = false;
			foreach (char c in text)
			{
				if (builder.Length >= MaxKeyLength) break;
				if (char.IsDigit(c))
				{
					if (!inDigits) builder.Append('#');
					inDigits = true;
					inSpace = false;
					continue;
				}
				inDigits = false;
				if (char.IsWhiteSpace(c))
				{
					if (!inSpace && builder.Length > 0) builder.Append(' ');
					inSpace = true;
					continue;
				}
				inSpace = false;
				builder.Append(c);
			}
			return builder.ToString().TrimEnd();
		}

		private static string Truncate(string text) =>
			text.Length <= MaxKeyLength ? text : text.Substring(0, MaxKeyLength) + "...";
	}
}
