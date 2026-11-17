using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop;

public static class ProjectServiceCompat
{
	public static IProject CurrentProject {
		get {
			return Project.ProjectService.CurrentProject;
		}
	}
	
	public static ISolution OpenSolution {
		get {
			return Project.ProjectService.OpenSolution;
		}
	}
	
	public static IEnumerable<IProject> Projects {
		get {
			return Project.ProjectService.OpenSolution?.Projects ?? Enumerable.Empty<IProject>();
		}
	}
	
	public static IReadOnlyList<FileFilterDescriptor> GetFileFilters()
	{
		return Project.ProjectService.GetFileFilters();
	}
	
	public static string GetAllFilesFilter()
	{
		return Project.ProjectService.GetAllFilesFilter();
	}
	
	public static void AddProjectItem(IProject project, ProjectItem item)
	{
		Project.ProjectService.AddProjectItem(project, item);
	}
	
	public static event EventHandler<ProjectItemEventArgs> ProjectItemRemoved {
		add { SD.ProjectService.ProjectItemRemoved += value; }
		remove { SD.ProjectService.ProjectItemRemoved -= value; }
	}
	
	// This type is the compatibility facade for the deprecated project-service events: its whole
	// job is to keep the legacy EventHandler<SolutionEventArgs> shape available. The documented
	// replacement (MessageBus<SolutionClosedMessageEventArgs>) has different event arguments, so
	// moving it would change the facade's own contract. Revisit when the deprecated events go.
#pragma warning disable CS0618
	public static event EventHandler<SolutionEventArgs> SolutionClosed {
		add { SD.ProjectService.SolutionClosed += value; }
		remove { SD.ProjectService.SolutionClosed -= value; }
	}
	
	public static event EventHandler<SolutionEventArgs> SolutionOpened {
		add { SD.ProjectService.SolutionOpened += value; }
		remove { SD.ProjectService.SolutionOpened -= value; }
	}
	
	public static event EventHandler<SolutionEventArgs> SolutionLoaded {
		add { SD.ProjectService.SolutionOpened += value; }
		remove { SD.ProjectService.SolutionOpened -= value; }
	}
#pragma warning restore CS0618
}
