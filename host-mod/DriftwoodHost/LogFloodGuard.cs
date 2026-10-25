using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DriftwoodHost
{
	// Sits in front of Unity's own log handler and stops one repeating error from filling the
	// disk (LogRepeatLimiter has the why and the numbers). Ordinary log lines pass untouched;
	// errors, asserts, exceptions and warnings go through the limiter.
	//
	// It reaches the errors that matter: an exception that escapes a MonoBehaviour callback is
	// logged by the engine through UnityEngine.Debug.CallOverridenDebugHandler, which hands it to
	// Debug.unityLogger.logHandler whenever that is not Unity's default handler (read in the
	// game's own UnityEngine.CoreModule, 2026-10-25). BepInEx copies Unity's log into
	// LogOutput.log from Application.logMessageReceived, which only fires for lines that were
	// written, so a held-back line stays out of both files. BepInEx's own lines (Plugin.Log, the
	// readiness markers, the swallow alarm) do not go through Unity's handler and are never held.
	//
	// What it does not reach: errors the engine writes from native code (for example "Instancing
	// is not supported"), which never enter managed code. HeadlessPatches skips their sources.
	internal sealed class LogFloodGuard : ILogHandler
	{
		private static LogFloodGuard _installed;
		private static bool _warnedReplaced;

		private readonly ILogHandler _inner;
		private readonly LogRepeatLimiter _limiter;

		private LogFloodGuard(ILogHandler inner, LogRepeatLimiter limiter)
		{
			_inner = inner;
			_limiter = limiter;
		}

		// Lines held back since the process started; published in the readiness file.
		internal static long HeldBack => _installed == null ? 0 : _installed._limiter.TotalHeldBack;

		internal static bool Installed => _installed != null;

		internal static void Install()
		{
			if (_installed != null) return;
			ILogHandler current = Debug.unityLogger.logHandler;
			if (current == null || current is LogFloodGuard) return;
			_installed = new LogFloodGuard(current, new LogRepeatLimiter());
			Debug.unityLogger.logHandler = _installed;
		}

		// Called on a timer by the plugin: writes the summaries of windows that ended while the log
		// was quiet, and says so once if something replaced the handler after us.
		internal static void Flush(Action<string> warn)
		{
			LogFloodGuard guard = _installed;
			if (guard == null) return;
			guard.WriteSummaries();
			if (!_warnedReplaced && !ReferenceEquals(Debug.unityLogger.logHandler, guard))
			{
				_warnedReplaced = true;
				warn?.Invoke("The log guard is no longer in front of Unity's log (something replaced the handler), so a repeating error can fill the log again.");
			}
		}

		public void LogFormat(LogType logType, Object context, string format, params object[] args)
		{
			if (logType == LogType.Log)
			{
				_inner.LogFormat(logType, context, format, args);
				return;
			}
			bool write = true;
			try
			{
				WriteSummaries();
				string text = args == null || args.Length == 0 ? format : string.Format(format, args);
				write = _limiter.Admit(logType + ": " + text);
			}
			catch
			{
				// A guard that throws would lose the line it guards. Write it.
				write = true;
			}
			if (write) _inner.LogFormat(logType, context, format, args);
		}

		public void LogException(Exception exception, Object context)
		{
			bool write = true;
			try
			{
				WriteSummaries();
				if (exception != null)
				{
					write = _limiter.Admit(LogRepeatLimiter.ExceptionLine(
						exception.GetType().FullName, exception.Message, exception.StackTrace));
				}
			}
			catch
			{
				write = true;
			}
			if (write) _inner.LogException(exception, context);
		}

		private void WriteSummaries()
		{
			List<LogRepeatLimiter.Summary> summaries = _limiter.TakeSummaries();
			for (int i = 0; i < summaries.Count; i++)
			{
				// Straight to Unity's handler: through Debug.LogWarning it would come back here.
				_inner.LogFormat(LogType.Warning, null, "{0}", _limiter.SummaryLine(summaries[i]));
			}
		}
	}
}
