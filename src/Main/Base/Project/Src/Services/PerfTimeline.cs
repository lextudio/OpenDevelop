// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Wall-clock milestones of the operations doc/technotes/fast-mode.md sets out to speed up
	/// (solution open, Roslyn project push, build). Each scope restarts its clock on
	/// <see cref="Begin"/>, so a reading always describes the most recent run of that operation.
	/// Every milestone is also written to the app log as "perf: scope/name +Nms".
	/// </summary>
	public static class PerfTimeline
	{
		public sealed class Milestone
		{
			public string Name { get; internal set; }
			public long ElapsedMilliseconds { get; internal set; }
			public string Detail { get; internal set; }
		}

		sealed class Scope
		{
			public long StartTimestamp;
			public long ElapsedMilliseconds => (Stopwatch.GetTimestamp() - StartTimestamp) * 1000 / Stopwatch.Frequency;
			public readonly List<Milestone> Milestones = new List<Milestone>();
			public long Generation;
		}

		public const string SolutionOpen = "solution-open";
		public const string Build = "build";
		/// <summary>Main to a usable main window. Begun once logging is safe, with its clock backdated
		/// to Main's entry; its first milestone's detail says how long the host took to reach Main.</summary>
		public const string Startup = "startup";

		static readonly object gate = new object();
		static readonly Dictionary<string, Scope> scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
		static long generationCounter;

		/// <summary>Restarts <paramref name="scope"/> and returns its generation, which
		/// <see cref="Mark(string, long, string, string)"/> uses to drop milestones of a superseded run.</summary>
		public static long Begin(string scope) => Begin(scope, Stopwatch.GetTimestamp());

		/// <summary>Restarts <paramref name="scope"/> with its clock starting at
		/// <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
		public static long Begin(string scope, long startTimestamp)
		{
			lock (gate) {
				var started = new Scope { Generation = ++generationCounter, StartTimestamp = startTimestamp };
				scopes[scope] = started;
				LoggingService.Info("perf: " + scope + " started");
				return started.Generation;
			}
		}

		/// <summary>Records a milestone in the current run of <paramref name="scope"/>.</summary>
		public static void Mark(string scope, string name, string detail = null) =>
			Mark(scope, 0, name, detail);

		/// <summary>Records a milestone only if <paramref name="generation"/> is still the current
		/// run of <paramref name="scope"/> (0 accepts any run).</summary>
		public static void Mark(string scope, long generation, string name, string detail = null)
		{
			lock (gate) {
				if (!scopes.TryGetValue(scope, out var current))
					return;
				if (generation != 0 && generation != current.Generation)
					return;
				var milestone = new Milestone {
					Name = name,
					ElapsedMilliseconds = current.ElapsedMilliseconds,
					Detail = detail
				};
				current.Milestones.Add(milestone);
				LoggingService.Info("perf: " + scope + "/" + name + " +" + milestone.ElapsedMilliseconds + "ms"
				                    + (detail != null ? " (" + detail + ")" : ""));
			}
		}

		/// <summary>The milestones of the latest run of every scope.</summary>
		public static IReadOnlyDictionary<string, Milestone[]> Snapshot()
		{
			lock (gate) {
				return scopes.ToDictionary(pair => pair.Key, pair => pair.Value.Milestones.ToArray(), StringComparer.Ordinal);
			}
		}
	}
}
