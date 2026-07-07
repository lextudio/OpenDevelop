using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting
{
	class VsTestDiscoveryAdapter : VsTestAdapter
	{
		ConcurrentQueue<DiscoveryJob> discoveryQueue = new ConcurrentQueue<DiscoveryJob>();
		DiscoveryJob discoveryJobInProgress;

		public static VsTestDiscoveryAdapter Instance { get; } = new VsTestDiscoveryAdapter();

		class DiscoveryJob
		{
			public IProject project;
			public DiscoveredTests tests = new DiscoveredTests();
			public TaskCompletionSource<DiscoveredTests> taskSource = new TaskCompletionSource<DiscoveredTests>();
		}

		protected override void ProcessMessage(Message message)
		{
			switch (message.MessageType) {
				case MessageType.TestCasesFound:
					OnTestCasesFound(message);
					break;
				case MessageType.DiscoveryComplete:
					OnDiscoveryCompleted(message);
					break;
				case MessageType.TestMessage:
					OnTestMessage(message);
					break;
				default:
					base.ProcessMessage(message);
					break;
			}
		}

		public async Task<DiscoveredTests> DiscoverTestsAsync(IProject project)
		{
			await Start();
			var job = new DiscoveryJob() { project = project };
			discoveryQueue.Enqueue(job);
			ProcessDiscoveryQueue();
			return await job.taskSource.Task;
		}

		void ProcessDiscoveryQueue()
		{
			if (discoveryQueue.IsEmpty)
				return;
			if (discoveryJobInProgress != null)
				return;
			if (!discoveryQueue.TryDequeue(out var newJob))
				return;

			discoveryJobInProgress = newJob;
			var testAssemblyFile = GetAssemblyFileName(discoveryJobInProgress.project);

			if (!File.Exists(testAssemblyFile)) {
				discoveryJobInProgress.taskSource.SetResult(discoveryJobInProgress.tests);
				discoveryJobInProgress = null;
				ProcessDiscoveryQueue();
				return;
			}

			SendExtensionList(Array.Empty<string>());

			var message = new DiscoveryRequestPayload {
				Sources = new string[] { testAssemblyFile },
				RunSettings = GetRunSettings(discoveryJobInProgress.project)
			};
			communicationManager.SendMessage(MessageType.StartDiscovery, message);
		}

		void OnTestCasesFound(Message message)
		{
			var tests = dataSerializer.DeserializePayload<IEnumerable<TestCase>>(message);
			if (tests.Any()) {
				discoveryJobInProgress.tests.Add(tests);
			}
		}

		void OnDiscoveryCompleted(Message message)
		{
			var discoveryCompletePayload = dataSerializer.DeserializePayload<DiscoveryCompletePayload>(message);
			if (discoveryCompletePayload.LastDiscoveredTests != null && discoveryCompletePayload.LastDiscoveredTests.Any()) {
				discoveryJobInProgress.tests.Add(discoveryCompletePayload.LastDiscoveredTests);
			}
			discoveryJobInProgress.taskSource.SetResult(discoveryJobInProgress.tests);
			discoveryJobInProgress = null;
			ProcessDiscoveryQueue();
		}

		void OnTestMessage(Message message)
		{
			var payload = dataSerializer.DeserializePayload<TestMessagePayload>(message);
			SD.Log.Info("VsTest: " + payload.Message);
		}
	}
}
