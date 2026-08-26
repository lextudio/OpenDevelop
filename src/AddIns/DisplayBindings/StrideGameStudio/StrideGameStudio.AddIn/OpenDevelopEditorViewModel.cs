// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The EditorViewModel for this addin's Stride session (see doc/technotes/stride-game-studio.md
// "Real-content integration plan").
//
// Derives from GameStudio's own GameStudioViewModel rather than from EditorViewModel directly.
// That looks like more shell than this addin needs - GameStudioViewModel carries app-shell state
// (EditionPanelViewModel, IDE launcher lists, restart-into-new-session commands) that OpenDevelop's
// own workbench makes redundant - but Stride's editor plugins reach the editor through the static
// GameStudioViewModel.GameStudio, which hard-casts EditorViewModel.Instance to GameStudioViewModel.
// StrideEditorPlugin.InitializeSession does exactly that, so an EditorViewModel that is not a
// GameStudioViewModel makes plugin session initialization throw InvalidCastException. Inheriting is
// the cheaper half of the trade: the unused shell state costs a few idle objects, whereas avoiding
// it would mean patching that coupling out of every plugin that assumes it.
//
// Only the two restart-based session commands are overridden. They exist for GameStudio's "switch
// project" flow, which relaunches the process (necessary there because plugin assemblies load into
// the same process and cannot be unloaded); OpenDevelop's one-session-per-process model never
// invokes them.

using System;
using System.Threading.Tasks;
using Stride.Core.IO;
using Stride.Core.Presentation.ViewModels;
using Stride.GameStudio.ViewModels;

namespace ICSharpCode.StrideGameStudio
{
	sealed class OpenDevelopEditorViewModel : GameStudioViewModel
	{
		public OpenDevelopEditorViewModel(IViewModelServiceProvider serviceProvider, Stride.Core.MostRecentlyUsedFiles.MostRecentlyUsedFileCollection mru)
			: base(serviceProvider, mru)
		{
		}

		protected override void RestartAndCreateNewSession()
			=> throw new NotSupportedException("OpenDevelop hosts one Stride session per process; 'new session' is not offered here.");

		protected override Task RestartAndOpenSession(UFile sessionPath)
			=> throw new NotSupportedException("OpenDevelop hosts one Stride session per process; switching sessions is not offered here.");
	}
}
