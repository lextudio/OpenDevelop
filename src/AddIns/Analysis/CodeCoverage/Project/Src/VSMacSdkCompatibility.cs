using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.UnitTesting;

namespace MonoDevelop.Projects
{
	/// <summary>
	/// Minimal VS for Mac project-model compatibility used by the linked, platform-neutral
	/// coverage repository. All state remains owned by SharpDevelop's IProject.
	/// </summary>
	public sealed class Project
	{
		public Project(IProject project)
		{
			SharpDevelopProject = project ?? throw new ArgumentNullException(nameof(project));
		}

		public IProject SharpDevelopProject { get; }
		public string Name => SharpDevelopProject.Name;
		public Solution ParentSolution => new Solution(SharpDevelopProject.ParentSolution);

		public FilePath GetOutputFileName(ConfigurationSelector configuration)
		{
			return new FilePath(ICSharpCode.CodeCoverage.CodeCoverageProjectOutput.GetAssembly(SharpDevelopProject));
		}

		public override bool Equals(object obj)
		{
			return obj is Project other && ReferenceEquals(SharpDevelopProject, other.SharpDevelopProject);
		}

		public override int GetHashCode() => SharpDevelopProject.GetHashCode();
	}

	public sealed class Solution
	{
		readonly ISolution solution;
		public Solution(ISolution solution) => this.solution = solution;
		public FilePath BaseDirectory => new FilePath(solution?.Directory?.ToString() ?? string.Empty);
	}

	public readonly struct ConfigurationSelector : IEquatable<ConfigurationSelector>
	{
		public ConfigurationSelector(string configuration) => Configuration = configuration ?? string.Empty;
		public string Configuration { get; }
		public bool Equals(ConfigurationSelector other) => StringComparer.OrdinalIgnoreCase.Equals(Configuration, other.Configuration);
		public override bool Equals(object obj) => obj is ConfigurationSelector other && Equals(other);
		public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Configuration);
	}

	public readonly struct FilePath
	{
		readonly string path;
		public FilePath(string path) => this.path = path ?? string.Empty;
		public FilePath ParentDirectory => new FilePath(Path.GetDirectoryName(path));
		public FilePath Combine(string child) => new FilePath(Path.Combine(path, child));
		public override string ToString() => path;
		public static implicit operator string(FilePath path) => path.path;
	}
}

namespace MonoDevelop.Core.Execution
{
	public interface IExecutionHandler { }

	public sealed class ExecutionContext
	{
		public ExecutionContext(IExecutionHandler handler, object consoleFactory, object executionTarget) { }
	}
}

namespace MonoDevelop.Ide
{
	using MonoDevelop.Projects;

	public static class IdeApp
	{
		public static WorkspaceAdapter Workspace { get; } = new WorkspaceAdapter();
		public static WorkbenchAdapter Workbench { get; } = new WorkbenchAdapter();
	}

	public sealed class WorkspaceAdapter
	{
		public ConfigurationSelector ActiveConfiguration {
			get {
				var solution = SD.ProjectService.CurrentSolution;
				return new ConfigurationSelector(solution?.ActiveConfiguration.Configuration ?? string.Empty);
			}
		}

		public IEnumerable<Project> GetAllProjects()
		{
			return SD.ProjectService.CurrentSolution?.Projects.Select(project => new Project(project))
				?? Enumerable.Empty<Project>();
		}
	}

	public sealed class WorkbenchAdapter
	{
		public ProgressMonitorAdapter ProgressMonitors { get; } = new ProgressMonitorAdapter();
	}

	public sealed class ProgressMonitorAdapter
	{
		public object ConsoleFactory { get; } = new object();
	}
}

namespace MonoDevelop.UnitTesting
{
	using MonoDevelop.Core.Execution;
	using MonoDevelop.Projects;

	public sealed class RootTest
	{
		internal RootTest(Project project, ITest test)
		{
			OwnerObject = project;
			SharpDevelopTest = test;
		}

		public object OwnerObject { get; }
		internal ITest SharpDevelopTest { get; }
		public bool CanRun(IExecutionHandler mode) => SharpDevelopTest != null;
	}

	public sealed class TestSession
	{
		internal TestSession(Task task) => Task = task;
		public Task Task { get; }
	}

	public sealed class TestSessionEventArgs : EventArgs
	{
		internal TestSessionEventArgs(RootTest test, TestSession session)
		{
			Test = test;
			Session = session;
		}

		public RootTest Test { get; }
		public TestSession Session { get; }
	}

	public sealed class TestRunOperation
	{
		internal TestRunOperation(Task task) => Task = task;
		public Task Task { get; }
	}

	/// <summary>
	/// VS for Mac UnitTestService facade backed by OpenDevelop's ITestService. Its session event
	/// ordering matches the upstream coverage service: subscribers prepare instrumentation before
	/// the selected test tree starts, then await the same session task before collecting results.
	/// </summary>
	public static class UnitTestService
	{
		static ITestService Service => SD.GetRequiredService<ITestService>();
		static bool subscribed;

		public static event EventHandler<TestSessionEventArgs> TestSessionStarting;
		public static event EventHandler TestSuiteChanged;

		static void EnsureSubscribed()
		{
			if (subscribed)
				return;
			subscribed = true;
			Service.OpenSolutionChanged += (sender, args) => TestSuiteChanged?.Invoke(sender, args);
		}

		public static RootTest FindRootTest(Project project)
		{
			EnsureSubscribed();
			ITest found = FindProject(Service.OpenSolution?.NestedTests, project.SharpDevelopProject);
			return found == null ? null : new RootTest(project, found);
		}

		static ITest FindProject(IEnumerable<ITest> tests, IProject project)
		{
			if (tests == null)
				return null;
			foreach (ITest test in tests) {
				if (test is ITestProject testProject && ReferenceEquals(testProject.Project, project))
					return test;
				ITest nested = FindProject(test.NestedTests, project);
				if (nested != null)
					return nested;
			}
			return null;
		}

		public static TestRunOperation RunTest(RootTest test, ExecutionContext context, bool buildOwnerObject)
		{
			EnsureSubscribed();
			var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var session = new TestSession(completion.Task);
			TestSessionStarting?.Invoke(null, new TestSessionEventArgs(test, session));

			Task runTask = Service.RunTestsAsync(new[] { test.SharpDevelopTest }, new TestExecutionOptions());
			_ = runTask.ContinueWith(task => {
				if (task.IsCanceled)
					completion.TrySetCanceled();
				else if (task.IsFaulted)
					completion.TrySetException(task.Exception.InnerExceptions);
				else
					completion.TrySetResult(true);
			}, TaskScheduler.Default);
			return new TestRunOperation(completion.Task);
		}
	}
}
