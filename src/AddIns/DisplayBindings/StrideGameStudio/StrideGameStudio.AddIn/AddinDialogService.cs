// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Minimal IEditorDialogService/IDialogService2/IDialogService implementation, needed to
// construct a real Stride EditorViewModel/SessionViewModel outside the discarded GameStudio app
// shell (see doc/technotes/stride-game-studio.md "Real-content integration plan"). No existing
// stub for this interface exists anywhere in the Stride tree - only the full WPF
// EditorDialogService/DialogService chain - so this has to be written from scratch.
//
// Scope: on the traced single-file-open path, only ShowProgressWindow and
// RegisterDefaultTemplateProviders are actually hit. Everything else either no-ops or answers
// with a safe default so an unexpected call degrades gracefully instead of crashing the view.

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MessageBoxButton = Stride.Core.Presentation.Services.MessageBoxButton;
using MessageBoxImage = Stride.Core.Presentation.Services.MessageBoxImage;
using MessageBoxResult = Stride.Core.Presentation.Services.MessageBoxResult;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Assets.Editor.ViewModel.Progress;
using Stride.Core.Assets.Templates;
using Stride.Core.IO;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.View;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Presentation.Windows;

namespace ICSharpCode.StrideGameStudio
{
	sealed class AddinDialogService : IEditorDialogService
	{
		public bool HasMainWindow => false;
		public void Exit(int exitCode = 0) { }

		public Task<CheckedMessageBoxResult> CheckedMessageBoxAsync(string message, bool? isChecked, string checkboxMessage, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
			=> Task.FromResult(new CheckedMessageBoxResult(MessageBoxResult.OK, isChecked ?? false));
		public Task<CheckedMessageBoxResult> CheckedMessageBoxAsync(string message, bool? isChecked, string checkboxMessage, IReadOnlyCollection<DialogButtonInfo> buttons, MessageBoxImage image = MessageBoxImage.None)
			=> Task.FromResult(new CheckedMessageBoxResult((int)MessageBoxResult.OK, isChecked ?? false));
		public Task<int> CheckedMessageBoxAsync(string message, IReadOnlyCollection<DialogCheckBoxInfo> checkBoxes, IReadOnlyCollection<DialogButtonInfo> buttons, MessageBoxImage image = MessageBoxImage.None)
			=> Task.FromResult((int)MessageBoxResult.OK);
		public Task<MessageBoxResult> MessageBoxAsync(string message, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
			=> Task.FromResult(MessageBoxResult.OK);
		public Task<int> MessageBoxAsync(string message, IReadOnlyCollection<DialogButtonInfo> buttons, MessageBoxImage image = MessageBoxImage.None)
			=> Task.FromResult((int)MessageBoxResult.OK);
		public Task<UFile?> OpenFilePickerAsync(UDirectory? initialPath = null, IReadOnlyList<FilePickerFilter>? filters = null) => Task.FromResult<UFile?>(null);
		public Task<IReadOnlyList<UFile>> OpenMultipleFilesPickerAsync(UDirectory? initialPath = null, IReadOnlyList<FilePickerFilter>? filters = null) => Task.FromResult<IReadOnlyList<UFile>>([]);
		public Task<UDirectory?> OpenFolderPickerAsync(UDirectory? initialPath = null) => Task.FromResult<UDirectory?>(null);
		public Task<UFile?> SaveFilePickerAsync(UDirectory? initialPath = null, IReadOnlyList<FilePickerFilter>? filters = null, string? defaultExtension = null, string? defaultFileName = null) => Task.FromResult<UFile?>(null);

		public MessageBoxResult BlockingMessageBox(string message, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) => MessageBoxResult.OK;
		public int BlockingMessageBox(string message, IEnumerable<DialogButtonInfo> buttons, MessageBoxImage image = MessageBoxImage.None) => (int)MessageBoxResult.OK;
		public CheckedMessageBoxResult BlockingCheckedMessageBox(string message, bool? isChecked, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) => new(MessageBoxResult.OK, isChecked ?? false);
		public CheckedMessageBoxResult BlockingCheckedMessageBox(string message, bool? isChecked, string checkboxMessage, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) => new(MessageBoxResult.OK, isChecked ?? false);
		public CheckedMessageBoxResult BlockingCheckedMessageBox(string message, bool? isChecked, string checkboxMessage, IEnumerable<DialogButtonInfo> buttons, MessageBoxImage image = MessageBoxImage.None) => new((int)MessageBoxResult.OK, isChecked ?? false);
		public Task CloseMainWindow(Action onClosed) { onClosed(); return Task.CompletedTask; }

		public void ShowNotificationWindow(string title, string message, ICommandBase command, object commandParameter) { }
		public void CloseAllNotificationWindows() { }
		public void AddDelayedNotification(Stride.Core.Settings.SettingsKey<bool> confirmationSettingsKey, string message, string yesCaption, string noCaption, Action? yesAction = null, Action? noAction = null, Stride.Core.Settings.SettingsKey<bool>? yesNoSettingsKey = null) { }
		public void ShowDelayedNotifications() { }
		public void ShowSettingsWindow(IViewModelServiceProvider serviceProvider) { }
		public void ShowProgressWindow(WorkProgressViewModel workProgress, int minDelay) { }
		public void ClearKeyboardFocus() { }
		public void RegisterDefaultTemplateProviders() { }
		public void RegisterDefaultTemplateProvider(ITemplateProvider provider) { }
		public void RegisterAdditionalTemplateProvider(ITemplateProvider provider) { }
		public void UnregisterAdditionalTemplateProviders() { }

		public INewProjectDialog CreateNewProjectDialog(SessionViewModel session) => throw new NotSupportedException();
		public IItemTemplateDialog CreateAddAssetDialog(SessionViewModel session, DirectoryBaseViewModel directory) => throw new NotSupportedException();
		public IItemTemplateDialog CreateAssetTemplatesDialog(SessionViewModel session, DirectoryBaseViewModel directory, IEnumerable<TemplateAssetDescription> templates) => throw new NotSupportedException();
		public IItemTemplateDialog CreateAssetTemplatesDialog(SessionViewModel session, DirectoryBaseViewModel directory, int fileCount, IEnumerable<KeyValuePair<TemplateAssetDescription, int>> templates) => throw new NotSupportedException();
		public IAssetPickerDialog CreateAssetPickerDialog(SessionViewModel session) => throw new NotSupportedException();
		public IPackagePickerDialog CreatePackagePickerDialog(SessionViewModel session) => throw new NotSupportedException();
		public IFixReferencesDialog CreateFixAssetReferencesDialog(IViewModelServiceProvider serviceProvider, IReadOnlyCollection<AssetViewModel> assets, Stride.Core.Assets.Analysis.IAssetDependencyManager dependencyManager) => throw new NotSupportedException();
	}
}
