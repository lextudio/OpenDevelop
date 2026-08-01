// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.UnitTesting.Frameworks;

namespace ICSharpCode.UnitTesting
{
	sealed class SDTestService : ITestService
	{
		public SDTestService()
		{
			SD.ProjectService.SolutionOpened += ProjectServiceSolutionChanged;
			SD.ProjectService.SolutionClosed += ProjectServiceSolutionChanged;
		}

		#region Test Framework Management
		const string AddInPath = "/SharpDevelop/UnitTesting/TestFrameworks";
		IReadOnlyList<TestFrameworkDescriptor> testFrameworkDescriptors = SD.AddInTree.BuildItems<TestFrameworkDescriptor>(AddInPath, null);
		
		public ITestFramework GetTestFrameworkForProject(IProject project)
		{
			if (project != null) {
				foreach (TestFrameworkDescriptor descriptor in testFrameworkDescriptors) {
					if (descriptor.IsSupportedProject(project)) {
						return descriptor.TestFramework;
					}
				}
			}
			return null;
		}
		#endregion
		
		#region UnitTestMessageView
		MessageViewCategory unitTestMessageView;
		
		public IOutputCategory UnitTestMessageView {
			get {
				if (unitTestMessageView == null) {
					MessageViewCategory.Create(ref unitTestMessageView,
					                           "UnitTesting",
					                           "${res:ICSharpCode.NUnitPad.NUnitPadContent.PadName}");
				}
				return unitTestMessageView;
			}
		}
		#endregion
		
		#region OpenSolution
		TestSolution solution;
		
		public ITestSolution OpenSolution {
			get {
				SD.MainThread.VerifyAccess();
				if (solution == null)
					solution = new TestSolution(this, SD.ResourceService);
				return solution;
			}
		}
		
		public event EventHandler OpenSolutionChanged = delegate { };

		void ProjectServiceSolutionChanged(object sender, EventArgs e)
		{
			SD.MainThread.VerifyAccess();
			solution = null;
			OpenSolutionChanged(this, EventArgs.Empty);
		}
		#endregion
		
		#region RunTests
		TestOperation activeOperation;

		public event EventHandler RunningTestsChanged = delegate { };
		
		public bool IsRunningTests {
			get { return activeOperation != null; }
		}

		public TestOperationKind? CurrentOperation {
			get { return activeOperation != null ? activeOperation.Kind : (TestOperationKind?)null; }
		}

		public bool TryBeginOperation(TestOperationKind kind, out ITestOperation operation)
		{
			SD.MainThread.VerifyAccess();
			if (activeOperation != null) {
				operation = null;
				return false;
			}

			activeOperation = new TestOperation(this, kind);
			operation = activeOperation;
			OnRunningTestsChanged();
			return true;
		}
		
		public async Task RunTestsAsync(IEnumerable<ITest> selectedTests, TestExecutionOptions options)
		{
			if (SD.MainThread.InvokeRequired) {
				var selectedTestsSnapshot = selectedTests.ToArray();
				Task uiTask = await SD.MainThread.InvokeAsync(() => RunTestsAsync(selectedTestsSnapshot, options));
				await uiTask;
				return;
			}
			
			ITestOperation operation;
			var kind = options.UseDebugger ? TestOperationKind.Debug : TestOperationKind.Run;
			if (!TryBeginOperation(kind, out operation))
				return;

			var executionManager = new TestExecutionManager();
			using (operation) {
				await executionManager.RunTestsAsync(selectedTests, options, operation.CancellationToken);
			}
		}
		
		public void CancelRunningTests()
		{
			SD.MainThread.VerifyAccess();
			activeOperation?.Cancel();
		}

		void EndOperation(TestOperation operation)
		{
			void EndOnMainThread()
			{
				if (!ReferenceEquals(activeOperation, operation))
					return;
				activeOperation = null;
				operation.DisposeCancellationTokenSource();
				OnRunningTestsChanged();
			}

			SD.MainThread.InvokeIfRequired(EndOnMainThread);
		}

		void OnRunningTestsChanged()
		{
			RunningTestsChanged(this, EventArgs.Empty);
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
		}

		sealed class TestOperation : ITestOperation
		{
			readonly SDTestService owner;
			readonly CancellationTokenSource cancellation = new CancellationTokenSource();
			bool disposed;

			public TestOperation(SDTestService owner, TestOperationKind kind)
			{
				this.owner = owner;
				Kind = kind;
			}

			public TestOperationKind Kind { get; }
			public CancellationToken CancellationToken { get { return cancellation.Token; } }

			public void Cancel()
			{
				if (!disposed)
					cancellation.Cancel();
			}

			public void Dispose()
			{
				if (disposed)
					return;
				disposed = true;
				owner.EndOperation(this);
			}

			public void DisposeCancellationTokenSource()
			{
				cancellation.Dispose();
			}
		}
		#endregion
	}
}
