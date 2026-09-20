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
