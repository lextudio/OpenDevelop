// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	/// <summary>
	/// Measures how responsive the UI thread is to input, from inside the process: a background thread posts
	/// a no-op at <see cref="DispatcherPriority.Input"/> every 50 ms and records how long it waited. That is the
	/// delay a key press or click would see. A DevFlow UI action is not a substitute: DevFlow queues it on the
	/// dispatcher behind lower-priority work, so its latency overstates a freeze.
	///
	/// Deliberately not [DevFlowUIThread]: these actions run on DevFlow's own thread, so reading the numbers
	/// never waits for the UI thread being measured.
	/// </summary>
	public static class UiLatencyDevFlowActions
	{
		const int IntervalMilliseconds = 50;
		const double StallThresholdMilliseconds = 100;

		static readonly object gate = new object();
		static List<double> samples = new List<double>();
		static CancellationTokenSource running;

		[DevFlowAction("od.ui-latency.start", Description = "Start measuring UI-thread input latency (a no-op posted at Input priority every 50 ms); clears earlier samples")]
		public static string Start()
		{
			var dispatcher = Application.Current?.Dispatcher;
			if (dispatcher == null)
				return JsonSerializer.Serialize(new { success = false, error = "No WPF dispatcher." });
			var cancellation = new CancellationTokenSource();
			Interlocked.Exchange(ref running, cancellation)?.Cancel();
			lock (gate)
				samples = new List<double>();
			_ = Task.Run(() => MeasureAsync(dispatcher, cancellation.Token));
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.ui-latency.read", Description = "Statistics of the UI-thread input latency samples since start (or since the last read with reset=true): count, max, p50/p95/p99 and the summed time of stalls over 100 ms")]
		public static string Read(bool reset = false)
		{
			double[] values;
			lock (gate) {
				values = samples.ToArray();
				if (reset)
					samples = new List<double>();
			}
			Array.Sort(values);
			double Percentile(double p) => values.Length == 0 ? 0 : values[(int)Math.Round(p * (values.Length - 1))];
			return JsonSerializer.Serialize(new {
				success = true,
				count = values.Length,
				maxMs = values.Length == 0 ? 0 : Math.Round(values[^1]),
				p50Ms = Math.Round(Percentile(0.50)),
				p95Ms = Math.Round(Percentile(0.95)),
				p99Ms = Math.Round(Percentile(0.99)),
				stalledMs = Math.Round(values.Where(v => v > StallThresholdMilliseconds).Sum())
			});
		}

		[DevFlowAction("od.ui-latency.stop", Description = "Stop measuring UI-thread input latency")]
		public static string Stop()
		{
			Interlocked.Exchange(ref running, null)?.Cancel();
			return JsonSerializer.Serialize(new { success = true });
		}

		static async Task MeasureAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
		{
			var watch = new Stopwatch();
			while (!cancellationToken.IsCancellationRequested) {
				watch.Restart();
				try {
					await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input, cancellationToken);
				} catch (OperationCanceledException) {
					return;
				}
				var latency = watch.Elapsed.TotalMilliseconds;
				lock (gate)
					samples.Add(latency);
				try {
					await Task.Delay(IntervalMilliseconds, cancellationToken);
				} catch (OperationCanceledException) {
					return;
				}
			}
		}
	}
}
