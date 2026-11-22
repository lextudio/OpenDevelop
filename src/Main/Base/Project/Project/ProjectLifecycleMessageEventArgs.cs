// Copyright (c) 2026 SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to do so, subject to the following conditions:

using System;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>Completed lifecycle facts published by <see cref="IProjectService"/>.</summary>
	public abstract class ProjectLifecycleMessageEventArgs : EventArgs
	{
		protected ProjectLifecycleMessageEventArgs(long revision) => Revision = revision;

		/// <summary>Monotonically increasing sequence number assigned by the project-service owner.</summary>
		public long Revision { get; }
	}

	public sealed class SolutionOpenedMessageEventArgs : ProjectLifecycleMessageEventArgs
	{
		public SolutionOpenedMessageEventArgs(ISolution solution, long revision) : base(revision) => Solution = solution ?? throw new ArgumentNullException(nameof(solution));
		public ISolution Solution { get; }
	}

	/// <summary>
	/// One project of the open solution was re-read from disk (its project file changed outside the
	/// IDE) without reloading the solution: open documents stayed open, and listeners that keep a
	/// per-project model (the language service) refresh just this project.
	/// </summary>
	public sealed class ProjectReloadedMessageEventArgs : ProjectLifecycleMessageEventArgs
	{
		public ProjectReloadedMessageEventArgs(IProject project, long revision) : base(revision) => Project = project ?? throw new ArgumentNullException(nameof(project));
		public IProject Project { get; }
	}

	public sealed class SolutionClosedMessageEventArgs : ProjectLifecycleMessageEventArgs
	{
		public SolutionClosedMessageEventArgs(ISolution solution, long revision) : base(revision) => Solution = solution ?? throw new ArgumentNullException(nameof(solution));
		public ISolution Solution { get; }
	}

	public sealed class ActiveProjectChangedMessageEventArgs : ProjectLifecycleMessageEventArgs
	{
		public ActiveProjectChangedMessageEventArgs(IProject oldProject, IProject newProject, long revision) : base(revision)
		{
			OldProject = oldProject;
			NewProject = newProject;
		}

		public IProject OldProject { get; }
		public IProject NewProject { get; }
	}

	/// <summary>
	/// The project model changed in a way views of the project tree should re-read (items re-created from
	/// MSBuild, a project added to a loaded solution, a generated file added). Sent by the model on the main
	/// thread; the model does not know who displays it (Solution Explorer subscribes).
	/// </summary>
	public sealed class ProjectTreeChangedMessageEventArgs : EventArgs
	{
		public ProjectTreeChangedMessageEventArgs(IProject project) => Project = project;
		/// <summary>The project that changed, or null when it is not specific to one.</summary>
		public IProject Project { get; }
	}

	public sealed class SolutionConfigurationChangedMessageEventArgs : ProjectLifecycleMessageEventArgs
	{
		public SolutionConfigurationChangedMessageEventArgs(ISolution solution, ConfigurationAndPlatform configuration, long revision) : base(revision)
		{
			Solution = solution ?? throw new ArgumentNullException(nameof(solution));
			Configuration = configuration;
		}

		public ISolution Solution { get; }
		public ConfigurationAndPlatform Configuration { get; }
	}
}
