using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting
{
	class VsTestRunAdapter : VsTestAdapter
	{
		RunJob runJobInProgress;

		public static VsTestRunAdapter Instance { get; } = new VsTestRunAdapter();

		public event EventHandler<TestFinishedEventArgs> TestFinished;

		class RunJob
		{
			public IProject Project { get; }
			public Action<Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult> OnResult { get; }
			public Action OnComplete { get; }
			public CancellationToken CancellationToken { get; }

			public RunJob(IProject project,
				Action<Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult> onResult,
				Action onComplete,
				CancellationToken cancellationToken)
			{
				Project = project;
				OnResult = onResult;
				OnComplete = onComplete;
				CancellationToken = cancellationToken;
			}
		}

		protected override void ProcessMessage(Message message)
		{
			switch (message.MessageType) {
				case MessageType.TestMessage:
					OnTestMessage(message);
					break;
				case MessageType.TestRunStatsChange:
					OnTestRunChanged(message);
					break;
				case MessageType.ExecutionComplete:
					OnTestRunComplete(message);
					break;
				default:
					base.ProcessMessage(message);
					break;
			}
		}

		public async Task RunTestsAsync(
			IProject project,
			IEnumerable<Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase> testCases,
			Action<Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult> onResult,
			Action onComplete,
			CancellationToken cancellationToken)
		{
			await Start();
			runJobInProgress = new RunJob(project, onResult, onComplete, cancellationToken);

			SendExtensionList(Array.Empty<string>());

			var message = new TestRunRequestPayload {
				TestCases = testCases.ToList(),
				RunSettings = GetRunSettings(project)
			};
			communicationManager.SendMessage(MessageType.TestRunSelectedTestCasesDefaultHost, message);
		}

		protected override void OnStop()
		{
			try {
				communicationManager?.SendMessage(MessageType.CancelTestRun);
			} catch (Exception ex) {
				SD.Log.Warn("CancelTestRun error.", ex);
			}
		}

		void OnTestMessage(Message message)
		{
			var payload = dataSerializer.DeserializePayload<TestMessagePayload>(message);
			SD.Log.Info("VsTest run: " + payload.Message);
		}

		void OnTestRunChanged(Message message)
		{
			var eventArgs = dataSerializer.DeserializePayload<TestRunChangedEventArgs>(message);

			if (eventArgs.NewTestResults != null) {
				foreach (Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult result in eventArgs.NewTestResults) {
					runJobInProgress?.OnResult?.Invoke(result);
				}
			}
		}

		void OnTestRunComplete(Message message)
		{
			var testRunCompletePayload = dataSerializer.DeserializePayload<TestRunCompletePayload>(message);

			if (testRunCompletePayload.LastRunTests?.NewTestResults != null) {
				foreach (Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult result in testRunCompletePayload.LastRunTests.NewTestResults) {
					runJobInProgress?.OnResult?.Invoke(result);
				}
			}

			runJobInProgress?.OnComplete?.Invoke();
		}
	}
}
