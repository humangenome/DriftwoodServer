using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DriftwoodHost
{
	// Playbook 1d requirement 1: resolve every patch target BEFORE patching anything, report
	// every miss in ONE block, patch each target under its own try/catch, and then ASSERT that
	// the patch library actually patched what we asked for. A registration that quietly patches
	// nothing is the shape that hides, and neither of the other two checks catches it.

	internal enum PatchNecessity
	{
		// The server refuses to host if this one is missing.
		Required,
		// The feature turns off, hosting continues.
		Optional
	}

	internal enum ResolveOutcome
	{
		Resolved,
		// A clean "no such type" / "no such method". Only this counts as missing.
		Missing,
		// The lookup itself threw. For an OPTIONAL target this is patched anyway, so a fault in
		// the checker can never stand a feature down that would have worked. For a REQUIRED one
		// it is a refusal - see UnknownRequired in PatchReport for why "patched anyway" was never
		// implementable.
		Unknown
	}

	internal enum PatchKind
	{
		// Prefix returning false: the original body never runs.
		Skip,
		// Prefix returning false and setting __result = false.
		SkipReturningFalse,
		// Finalizer that swallows. The body still runs; only the escape is stopped.
		Swallow,
		// A named prefix/postfix supplied by the caller.
		Custom
	}

	internal sealed class PatchTarget
	{
		public string TypeName;
		public string MethodName;
		// When set, the method is found by NAME PREFIX instead of exact name - for the game's
		// codegen methods whose names carry a hash that moves on every rebuild
		// (RpcReader___SpawnPlayer___596900633 on 1.0.4 vs ___1871804056 on 1.0.6). Exactly one
		// declared method may match; two matches mean the shape drifted, and the target
		// resolves as missing rather than guessing.
		public string MethodNamePrefix;
		public Type[] Parameters;
		public PatchKind Kind = PatchKind.Skip;
		public PatchNecessity Necessity = PatchNecessity.Optional;
		// Members of one group stand or fall together. Half-applying a coupled feature is
		// usually worse than not applying it at all.
		public string Group;
		public string Why = string.Empty;
		public MethodInfo Prefix;
		public MethodInfo Postfix = null;
		public MethodInfo Finalizer = null;

		public string Id => TypeName + "." + MethodName;

		internal ResolveOutcome Outcome { get; set; } = ResolveOutcome.Missing;
		internal MethodBase Resolved { get; set; }
		internal bool Applied { get; set; }
		internal string Failure { get; set; }
	}

	internal sealed class PatchReport
	{
		public readonly List<string> Applied = new List<string>();
		public readonly List<string> MissingRequired = new List<string>();
		public readonly List<string> MissingOptional = new List<string>();
		public readonly List<string> FailedToApply = new List<string>();
		public readonly List<string> StoodDownGroups = new List<string>();
		public readonly List<string> Unknown = new List<string>();
		// A REQUIRED target whose resolution threw. Kept apart from Unknown because the two have
		// opposite consequences.
		public readonly List<string> UnknownRequired = new List<string>();

		public bool CanHost => MissingRequired.Count == 0 && FailedToApply.Count == 0 && UnknownRequired.Count == 0;

		// One plain sentence a support person who has never seen the code can read.
		public string Reason()
		{
			if (MissingRequired.Count > 0)
			{
				return "This server will not host because the game build no longer contains " +
					string.Join(", ", MissingRequired) +
					", which Driftwood must modify to run a dedicated server. The game has almost certainly been updated; the host mod needs rebuilding against the new build.";
			}
			if (FailedToApply.Count > 0)
			{
				return "This server will not host because Driftwood could not modify " +
					string.Join(", ", FailedToApply) +
					". The gameplay port stays shut, so this server reports as down rather than as a healthy server with nothing behind it.";
			}
			if (UnknownRequired.Count > 0)
			{
				return "This server will not host because Driftwood could not even determine whether " +
					string.Join(", ", UnknownRequired) +
					" still exists in this game build. That guard sits on the player-spawn path, so continuing would present a port nobody could spawn into.";
			}
			return "All required game modifications applied.";
		}
	}

	internal static class PatchPlan
	{
		// Comma-separated "Type.Method" ids that are FORCED to resolve as missing, so the
		// fail-closed path can be exercised on demand. Off unless a config says otherwise.
		internal static string[] SimulatedMissing = new string[0];

		public static PatchReport Apply(Harmony harmony, IReadOnlyList<PatchTarget> targets, Action<string> log, Action<string> warn)
		{
			PatchReport report = new PatchReport();

			// Pass 1 - resolve everything, touch nothing.
			foreach (PatchTarget target in targets)
			{
				if (SimulatedMissing.Length > 0 &&
					Array.Exists(SimulatedMissing, id => string.Equals(id, target.Id, StringComparison.OrdinalIgnoreCase)))
				{
					warn("FAULT INJECTION: treating " + target.Id + " as missing because SimulateMissingPatch says so.");
					target.Outcome = ResolveOutcome.Missing;
					continue;
				}
				try
				{
					Type type = AccessTools.TypeByName(target.TypeName);
					if (type == null)
					{
						target.Outcome = ResolveOutcome.Missing;
						continue;
					}
					MethodInfo method;
					if (!string.IsNullOrEmpty(target.MethodNamePrefix))
					{
						method = null;
						bool ambiguous = false;
						foreach (MethodInfo candidate in type.GetMethods(
							BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
							BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
						{
							if (!candidate.Name.StartsWith(target.MethodNamePrefix, StringComparison.Ordinal)) continue;
							if (method != null) { ambiguous = true; break; }
							method = candidate;
						}
						if (ambiguous) method = null;
					}
					else
					{
						method = target.Parameters == null
							? AccessTools.Method(type, target.MethodName)
							: AccessTools.Method(type, target.MethodName, target.Parameters);
					}
					if (method == null)
					{
						target.Outcome = ResolveOutcome.Missing;
						continue;
					}
					target.Resolved = method;
					target.Outcome = ResolveOutcome.Resolved;
				}
				catch (Exception exception)
				{
					// "Conservative in the safe direction" is only true for an OPTIONAL target.
					//
					// For a REQUIRED one this was FAIL-OPEN, and the comment describing it as
					// "patched anyway" was never implementable: the catch leaves target.Resolved
					// null, so pass 4 skips it (`if (target.Resolved == null) continue;`), pass 3
					// does not count it missing, and CanHost stayed true. The server came up
					// presenting a port with a spawn-path guard that was never installed - the
					// exact silent-failure shape the whole plan exists to refuse - and only the
					// panel's guard-marker net caught it, afterwards.
					//
					// A required guard whose existence we cannot even determine is not a guard.
					target.Outcome = ResolveOutcome.Unknown;
					report.Unknown.Add(target.Id + " (" + exception.GetType().Name + ")");
					if (target.Necessity == PatchNecessity.Required)
					{
						report.UnknownRequired.Add(target.Id + " (" + exception.GetType().Name + ": " + exception.Message + ")");
						warn("REQUIRED patch target could not be resolved: " + target.Id + " - " +
							exception.GetType().Name + ": " + exception.Message);
					}
				}
			}

			// Pass 2 - coupled sets. If any member of a group is missing, stand the whole group
			// down rather than running it half-applied.
			HashSet<string> brokenGroups = new HashSet<string>(
				targets.Where(t => t.Group != null && t.Outcome == ResolveOutcome.Missing)
					.Select(t => t.Group));
			foreach (string group in brokenGroups)
			{
				bool required = targets.Any(t => t.Group == group && t.Necessity == PatchNecessity.Required);
				if (!required) report.StoodDownGroups.Add(group);
			}

			// Pass 3 - report every miss at once, so a rebuilt game costs ONE boot rather than
			// nine boot-fix-boot cycles.
			foreach (PatchTarget target in targets)
			{
				if (target.Outcome != ResolveOutcome.Missing) continue;
				if (target.Necessity == PatchNecessity.Required) report.MissingRequired.Add(target.Id);
				else report.MissingOptional.Add(target.Id);
			}

			if (report.MissingRequired.Count > 0 || report.UnknownRequired.Count > 0)
			{
				// Do not patch anything at all. A half-patched game is harder to diagnose than
				// an unpatched one, and we are refusing to host either way.
				if (report.MissingRequired.Count > 0) warn("REQUIRED PATCH TARGETS MISSING: " + string.Join(", ", report.MissingRequired));
				if (report.UnknownRequired.Count > 0) warn("REQUIRED PATCH TARGETS UNRESOLVABLE: " + string.Join(", ", report.UnknownRequired));
				foreach (string id in report.MissingOptional) warn("optional patch target missing: " + id);
				return report;
			}

			// Pass 4 - apply, one try/catch and one named outcome per target.
			foreach (PatchTarget target in targets)
			{
				if (target.Outcome == ResolveOutcome.Missing) continue;
				if (target.Group != null && brokenGroups.Contains(target.Group)) continue;
				if (target.Resolved == null) continue;
				try
				{
					harmony.Patch(
						target.Resolved,
						prefix: Wrap(target.Prefix ?? DefaultPrefix(target)),
						postfix: Wrap(target.Postfix),
						finalizer: Wrap(target.Finalizer ?? DefaultFinalizer(target)));
					target.Applied = true;
				}
				catch (Exception exception)
				{
					target.Failure = exception.GetType().Name + ": " + exception.Message;
					if (target.Necessity == PatchNecessity.Required) report.FailedToApply.Add(target.Id);
					else warn("optional patch failed to apply: " + target.Id + " - " + target.Failure);
				}
			}

			// Pass 5 - assert the count. Ask the patch library what it actually patched rather
			// than trusting that Patch() returning cleanly means anything happened.
			HashSet<MethodBase> patched = new HashSet<MethodBase>(Harmony.GetAllPatchedMethods());
			foreach (PatchTarget target in targets)
			{
				if (!target.Applied || target.Resolved == null) continue;
				if (patched.Contains(target.Resolved))
				{
					report.Applied.Add(target.Id);
					continue;
				}
				target.Applied = false;
				target.Failure = "Harmony reported no patch on this method after a clean Patch() call";
				if (target.Necessity == PatchNecessity.Required) report.FailedToApply.Add(target.Id);
				else warn("optional patch silently patched nothing: " + target.Id);
			}

			log("Patch plan: " + report.Applied.Count + " applied, " +
				report.MissingOptional.Count + " optional missing, " +
				report.FailedToApply.Count + " failed, " +
				report.UnknownRequired.Count + " required-unresolvable, " +
				report.Unknown.Count + " unresolvable.");
			foreach (string id in report.Applied) log("  applied  " + id);
			foreach (string id in report.MissingOptional) warn("  MISSING  " + id + " (optional)");
			foreach (string id in report.FailedToApply) warn("  FAILED   " + id);
			foreach (string group in report.StoodDownGroups) warn("  STOOD DOWN feature group: " + group);
			return report;
		}

		private static HarmonyMethod Wrap(MethodInfo method) => method == null ? null : new HarmonyMethod(method);

		private static MethodInfo DefaultPrefix(PatchTarget target)
		{
			switch (target.Kind)
			{
				case PatchKind.Skip:
					return AccessTools.Method(typeof(PatchPlan), nameof(PrefixSkip));
				case PatchKind.SkipReturningFalse:
					return AccessTools.Method(typeof(PatchPlan), nameof(PrefixFalseResult));
				default:
					return null;
			}
		}

		private static MethodInfo DefaultFinalizer(PatchTarget target) =>
			target.Kind == PatchKind.Swallow
				? AccessTools.Method(typeof(PatchPlan), nameof(FinalizerSwallow))
				: null;

		private static bool PrefixSkip() => false;

		private static bool PrefixFalseResult(ref bool __result)
		{
			__result = false;
			return false;
		}

		// Catching is not fixing (1d mechanism 3). Every swallow is counted and the rate is
		// alarmed on, because a handler firing thousands of times a second is a broken feature
		// wearing a seatbelt.
		private static Exception FinalizerSwallow(Exception __exception, MethodBase __originalMethod)
		{
			if (__exception != null) SwallowCounter.Record(__originalMethod, __exception);
			return null;
		}
	}
}
