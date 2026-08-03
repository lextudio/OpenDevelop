using System;
using System.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="DefinitionViewPad"/>: shows the
/// definition of whatever expression is under the caret in the active editor, refreshed on a
/// timer, same behavior as before, just as a <see cref="ToolPaneModel"/>.
/// </summary>
[Export(typeof(DefinitionViewViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class DefinitionViewViewModel : ToolPaneModel, IDisposable
{
    readonly AvalonEdit.TextEditor ctl;
    DispatcherTimer timer;
    bool subscribed;

    NavigationTarget oldPosition;
    FileName currentFileName;

    public DefinitionViewViewModel()
    {
        Title = "Definition View";
        ContentId = "DefinitionView";
        IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
        IsCloseable = true;
        LegacyPadClass = typeof(DefinitionViewPad).FullName;

        ctl = Editor.AvalonEditTextEditorAdapter.CreateAvalonEditInstance();
        ctl.IsReadOnly = true;
        ctl.MouseDoubleClick += OnDoubleClick;
        ctl.IsVisibleChanged += delegate { UpdateTick(null); };
        Content = ctl;
    }

    /// <summary>
    /// Subscribes to <c>SD.ParserService</c> events on first real use rather than in the
    /// constructor - same early-startup hazard already found and fixed for
    /// <see cref="OutlineViewModel"/> (this model is constructed eagerly by MEF, before some
    /// services are registered) - deferred here defensively even though IParserService starts
    /// before workbench initialization, since the failure mode (a whole pane set silently
    /// vanishing) is expensive enough to guard against on any external service touch.
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IParserService)) == null)
            return;
        subscribed = true;
        SD.ParserService.ParseInformationUpdated += OnParserUpdateStep;
        SD.ParserService.LoadSolutionProjectsThread.Finished += LoadThreadFinished;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += delegate { UpdateTick(null); };
        timer.IsEnabled = !SD.ParserService.LoadSolutionProjectsThread.IsRunning;
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    public void Dispose()
    {
        if (!subscribed)
            return;
        SD.ParserService.ParseInformationUpdated -= OnParserUpdateStep;
        SD.ParserService.LoadSolutionProjectsThread.Finished -= LoadThreadFinished;
        ctl.Document = null;
    }

    void OnDoubleClick(object sender, EventArgs e)
    {
        FileName fileName = currentFileName;
        if (fileName != null) {
            var caret = ctl.TextArea.Caret;
            SD.FileService.JumpToFilePosition(fileName, caret.Line, caret.Column);
            UpdateTick(null);
        }
    }

    void LoadThreadFinished(object sender, EventArgs e)
    {
        timer.IsEnabled = true;
        UpdateTick(null);
    }

    void OnParserUpdateStep(object sender, ParseInformationEventArgs e)
    {
        UpdateTick(e);
    }

    async void UpdateTick(ParseInformationEventArgs e)
    {
        bool isActive = ctl.IsVisible && !SD.ParserService.LoadSolutionProjectsThread.IsRunning;
        timer.IsEnabled = isActive;
        if (!isActive)
            return;
        LoggingService.Debug("DefinitionViewViewModel.Update");

        NavigationTarget target = await ResolveDefinitionAtCaretAsync(e);
        if (target == null)
            return;
        OpenFile(target);
    }

    async Task<NavigationTarget> ResolveDefinitionAtCaretAsync(ParseInformationEventArgs e)
    {
        IWorkbenchWindow window = SD.Workbench.ActiveWorkbenchWindow;
        if (window == null)
            return null;
        IViewContent viewContent = window.ActiveViewContent;
        if (viewContent == null)
            return null;
        ITextEditor editor = viewContent.GetService<ITextEditor>();
        if (editor == null || editor.FileName == null)
            return null;

        if (e != null && editor.FileName != e.FileName)
            return null;

        var registry = SD.GetService<LanguageServiceRegistry>();
        if (registry == null || !registry.TryGetService(editor.FileName, out var service))
            return null;

        try {
            var id = new DocumentId(editor.FileName.ToString());
            await service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None);
            var targets = await service.GoToDefinitionAsync(id, editor.Caret.Offset, CancellationToken.None);
            return targets.Count > 0 ? targets[0] : null;
        } catch {
            return null;
        }
    }

    void OpenFile(NavigationTarget target)
    {
        if (target.FileName == oldPosition?.FileName && target.Position.Equals(oldPosition?.Position))
            return;
        oldPosition = target;
        var fileName = new FileName(target.FileName);
        if (fileName != currentFileName)
            LoadFile(fileName);
        ctl.TextArea.Caret.Location = new TextLocation(target.Position.Line, target.Position.Column);
        Rect r = ctl.TextArea.Caret.CalculateCaretRectangle();
        if (!r.IsEmpty)
            ctl.ScrollToVerticalOffset(r.Top - 4);
    }

    void LoadFile(FileName fileName)
    {
        ctl.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(SD.FileService.GetFileContent(fileName));
        ctl.Document.FileName = fileName;
        currentFileName = fileName;
        ctl.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
    }
}
