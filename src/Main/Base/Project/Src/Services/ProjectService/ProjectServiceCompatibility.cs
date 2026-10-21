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
#if HAS_UNO
			return SD.ProjectService.CurrentProject;
#else
			return Project.ProjectService.CurrentProject;
#endif
		}
	}
	
	public static ISolution OpenSolution {
		get {
#if HAS_UNO
			return SD.ProjectService.CurrentSolution;
#else
			return Project.ProjectService.OpenSolution;
#endif
		}
	}
	
	public static IEnumerable<IProject> Projects {
		get {
#if HAS_UNO
			return SD.ProjectService.AllProjects ?? Enumerable.Empty<IProject>();
#else
			return Project.ProjectService.OpenSolution?.Projects ?? Enumerable.Empty<IProject>();
#endif
		}
	}
	
	public static IReadOnlyList<FileFilterDescriptor> GetFileFilters()
	{
#if HAS_UNO
		return AddInTree.BuildItems<FileFilterDescriptor>("/SharpDevelop/Workbench/FileFilter", null);
#else
		return Project.ProjectService.GetFileFilters();
#endif
	}
	
	public static string GetAllFilesFilter()
	{
#if HAS_UNO
		var filters = GetFileFilters();
		return string.Join("|", filters.Select(filter => filter.ToString()));
#else
		return Project.ProjectService.GetAllFilesFilter();
#endif
	}
	
	public static void AddProjectItem(IProject project, ProjectItem item)
	{
#if HAS_UNO
		if (project == null) throw new ArgumentNullException(nameof(project));
		if (item == null) throw new ArgumentNullException(nameof(item));
		project.Items.Add(item);
#else
		Project.ProjectService.AddProjectItem(project, item);
#endif
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
