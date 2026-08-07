using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui.OptionPanels;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-04)
/// replacement for the legacy AddInTree-registered <see cref="CompilerMessageView"/> (AddInTree pad
/// id "OutputPad"): shows build/tool output categories, same behavior as before, just as a
/// <see cref="ToolPaneModel"/>. Implements both <see cref="IOutputPad"/> (already an established,
/// documented-thread-safe cross-AddIn service contract, registered as <c>SD.OutputPad</c>) and the
/// new <see cref="IOutputPadHost"/> (the extra <see cref="MessageViewCategory"/>-typed/toolbar
/// surface a handful of Base/AddIn callers need beyond <see cref="IOutputPad"/>).
/// </summary>
/// <remarks>
/// Unlike the other migrated panes, this one builds its whole control tree and subscribes to
/// <c>SD.ProjectService.CurrentSolutionChanged</c> directly in the constructor rather than
/// deferring to a lazy <c>EnsureSubscribed()</c> on first touch: the original
/// <see cref="CompilerMessageView"/> already did exactly this (eagerly, well before
/// "Starting workbench..." in the startup log) with no reported issue, its dependencies
/// (<c>ToolBarService</c>, <c>MenuService</c>, <c>PropertyService</c>, <c>SD.ProjectService</c>)
/// don't touch <c>SD.Workbench</c> the way <c>ErrorListViewModel</c>'s build-finished handler does,
/// and - most importantly - <see cref="IOutputPad"/> is documented thread-safe and routinely driven
/// from background build/restore threads, so a lazy first-touch construction risked building WPF
/// controls off the UI thread if a background thread reached this pad before anything on the UI
/// thread ever did.
/// </remarks>
[Export(typeof(CompilerMessageViewViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class CompilerMessageViewViewModel : ToolPaneModel, IOutputPad, IOutputPadHost, IClipboardHandler
{
    #region IOutputPad implementation

    void IOutputPad.BringToFront() =>
        // The model's own Show() only flips IsVisible/IsActive - a no-op when the layout restore's
        // "keep exactly the panes a layout file lists" reconciliation removed this pane from
        // ToolPanes (every saved layout except ProjectBrowser-only ones does). Route through the
        // pad descriptor instead, like SearchResultsPadViewModel.BringToFront does: GetPad reaches
        // AvalonDockLayout.ActivatePad, whose legacy branch (re-)docks a real anchorable and
        // re-applies the default-position sizing, so the pad is actually rendered.
        SD.Workbench.GetPad(typeof(CompilerMessageView))?.BringPadToFront();

    IOutputCategory IOutputPad.CreateCategory(string displayName)
    {
        var cat = new MessageViewCategory(displayName, displayName);
        AddCategory(cat);
        return cat;
    }

    IOutputCategory IOutputPad.GetOrCreateCategory(string displayName)
    {
        return SD.MainThread.InvokeIfRequired(() => GetOrCreateCategory(displayName));
    }

    IOutputCategory GetOrCreateCategory(string displayName)
    {
        foreach (var cat in messageCategories) {
            if (cat.DisplayCategory == displayName)
                return cat;
        }
        var newcat = new MessageViewCategory(displayName, displayName);
        AddCategory(newcat);
        return newcat;
    }

    void IOutputPad.RemoveCategory(IOutputCategory category)
    {
        throw new NotImplementedException();
    }

    IOutputCategory IOutputPad.CurrentCategory {
        get {
            return this.SelectedMessageViewCategory;
        }
        set {
            int index = messageCategories.IndexOf(value as MessageViewCategory);
            if (index >= 0)
                SelectedCategoryIndex = index;
        }
    }

    IOutputCategory IOutputPad.BuildCategory {
        get {
            return TaskService.BuildMessageViewCategory;
        }
    }

    #endregion

    #region MessageViewLinkElementGenerator
    class MessageViewLinkElementGenerator : LinkElementGenerator
    {
        public MessageViewLinkElementGenerator(Regex regex)
            : base(regex)
        {
            RequireControlModifierForClick = false;
        }

        protected override Uri GetUriFromMatch(Match match)
        {
            return new Uri(match.Groups[1].Value.Trim());
        }

        protected override VisualLineElement ConstructElementFromMatch(Match m)
        {
            Uri uri = GetUriFromMatch(m);
            if (uri == null)
                return null;
            var linkText = new VisualLineMessageViewLinkText(CurrentContext.VisualLine, m.Length);
            linkText.NavigateUri = uri;
            linkText.RequireControlModifierForClick = this.RequireControlModifierForClick;
            linkText.Line = int.Parse(m.Groups[2].Value);
            if (m.Groups.Count > 3)
                linkText.Column = int.Parse(m.Groups[3].Value);
            return linkText;
        }

        public static void RegisterGenerators(TextView textView)
        {
            // C#:
            textView.ElementGenerators.Add(new MessageViewLinkElementGenerator(
                new Regex(@"\b(\w:[/\\].*?)\((\d+),(\d+)\)")));
            // NUnit:
            textView.ElementGenerators.Add(new MessageViewLinkElementGenerator(
                new Regex(@"\b(\w:[/\\].*?):line\s(\d+)?$")));
            // C++:
            textView.ElementGenerators.Add(new MessageViewLinkElementGenerator(
                new Regex(@"\b(\w:[/\\].*?)\((\d+)\)")));
        }
    }

    class VisualLineMessageViewLinkText : VisualLineLinkText
    {
        public VisualLineMessageViewLinkText(VisualLine parentVisualLine, int length) : base(parentVisualLine, length)
        {
            this.RequireControlModifierForClick = false;
        }

        public int Line { get; set; }
        public int Column { get; set; }

        protected override void OnMouseDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !e.Handled && LinkIsClickable() && NavigateUri.IsFile) {
                FileService.JumpToFilePosition(NavigateUri.LocalPath, Line, Column);
                e.Handled = true;
            }
        }

        protected override VisualLineText CreateInstance(int length)
        {
            return new VisualLineMessageViewLinkText(ParentVisualLine, length) {
                NavigateUri = this.NavigateUri,
                Line = this.Line,
                Column = this.Column,
                TargetName = this.TargetName,
                RequireControlModifierForClick = this.RequireControlModifierForClick
            };
        }
    }
    #endregion

    readonly TextEditor textEditor = new TextEditor();
    readonly Grid panel = new Grid();
    ToolBar toolStrip;

    readonly List<MessageViewCategory> messageCategories = new List<MessageViewCategory>();

    int selectedCategory = 0;
    public int SelectedCategoryIndex {
        get {
            return selectedCategory;
        }
        set {
            SD.MainThread.VerifyAccess();
            if (selectedCategory != value) {
                selectedCategory = value;
                DisplayActiveCategory();
                OnSelectedCategoryIndexChanged(EventArgs.Empty);
            }
        }
    }

    void DisplayActiveCategory()
    {
        SD.MainThread.VerifyAccess();
        if (selectedCategory < 0) {
            textEditor.Text = "";
        } else {
            lock (messageCategories[selectedCategory].SyncRoot) {
                // accessing a categories' text takes its lock - but we have to take locks in the same
                // order as in the Append calls to prevent a deadlock
                EnqueueAppend(new AppendCall(messageCategories[selectedCategory], messageCategories[selectedCategory].Text, true));
            }
        }
    }

    public bool WordWrap {
        get {
            return properties.Get("WordWrap", true);
        }
        set {
            properties.Set("WordWrap", value);
        }
    }

    public MessageViewCategory SelectedMessageViewCategory {
        get {
            if (selectedCategory >= 0) {
                return messageCategories[selectedCategory];
            }
            return null;
        }
    }

    readonly Properties properties;

    public List<MessageViewCategory> MessageCategories {
        get {
            return messageCategories;
        }
    }

    public CompilerMessageViewViewModel()
    {
        Title = "Output";
        ContentId = "OutputPad";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Bottom"`.
        IsCloseable = true;
        LegacyPadClass = typeof(CompilerMessageView).FullName;
        PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Bottom; // Matches the legacy Pad's `defaultPosition = "Bottom"`.
        Content = panel;

        SD.Services.AddService(typeof(IOutputPad), this);
        SD.Services.AddService(typeof(IOutputPadHost), this);

        AddCategory(TaskService.BuildMessageViewCategory);

        textEditor.IsReadOnly = true;
        textEditor.ContextMenu = MenuService.CreateContextMenu(this, "/SharpDevelop/Pads/CompilerMessageView/ContextMenu");

        properties = ICSharpCode.Core.PropertyService.NestedProperties(OutputWindowOptionsPanel.OutputWindowsProperty);

        SetTextEditorFont();

        properties.PropertyChanged += new PropertyChangedEventHandler(OnOptionsPropertyChanged);

        MessageViewLinkElementGenerator.RegisterGenerators(textEditor.TextArea.TextView);
        textEditor.TextArea.TextView.ElementGenerators.OfType<LinkElementGenerator>().ForEach(x => x.RequireControlModifierForClick = false);

        toolStrip = ToolBarService.CreateToolBar(panel, this, "/SharpDevelop/Pads/CompilerMessageView/Toolbar");
        toolStrip.Items.OfType<ComboBox>().ForEach(b => b.MinWidth = 75);

        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        panel.Children.Add(toolStrip);
        panel.Children.Add(textEditor);
        Grid.SetRow(textEditor, 1);

        SetWordWrap();
        DisplayActiveCategory();
        SD.ProjectService.CurrentSolutionChanged += OnSolutionLoaded;

        SearchPanel.Install(textEditor);
    }

    void OnSolutionLoaded(object sender, EventArgs e)
    {
        foreach (MessageViewCategory category in messageCategories) {
            category.ClearText();
        }
    }

    bool IsFontChanged(string propName)
    {
        if ((propName == OutputWindowOptionsPanel.FontSizeName) || (propName == OutputWindowOptionsPanel.FontFamilyName)) {
            return true;
        }
        return false;
    }

    void SetWordWrap()
    {
        bool wordWrap = this.WordWrap;
        textEditor.WordWrap = wordWrap;
    }

    void SetTextEditorFont()
    {
        var fontDescription = OutputWindowOptionsPanel.DefaultFontDescription();
        textEditor.FontFamily = new FontFamily(fontDescription.Item1);
        textEditor.FontSize = fontDescription.Item2;
    }

    #region Category handling
    /// <summary>
    /// Adds a category to the compiler message view. This method is thread-safe.
    /// </summary>
    public void AddCategory(MessageViewCategory category)
    {
        if (SD.MainThread.InvokeRequired) {
            SD.MainThread.InvokeAsyncAndForget(() => AddCategory(category));
            return;
        }
        messageCategories.Add(category);
        category.TextSet += new TextEventHandler(CategoryTextSet);
        category.TextAppended += new TextEventHandler(CategoryTextAppended);

        OnMessageCategoryAdded(EventArgs.Empty);
    }

    void CategoryTextSet(object sender, TextEventArgs e)
    {
        EnqueueAppend(new AppendCall((MessageViewCategory)sender, e.Text, true));
    }

    struct AppendCall
    {
        internal readonly MessageViewCategory Category;
        internal readonly string Text;
        internal readonly bool ClearCategory;

        public AppendCall(MessageViewCategory category, string text, bool clearCategory)
        {
            this.Category = category;
            this.Text = text;
            this.ClearCategory = clearCategory;
        }
    }

    readonly object appendLock = new object();
    List<AppendCall> appendCalls = new List<AppendCall>();

    void CategoryTextAppended(object sender, TextEventArgs e)
    {
        EnqueueAppend(new AppendCall((MessageViewCategory)sender, e.Text, false));
    }

    void EnqueueAppend(AppendCall appendCall)
    {
        bool waitForMainThread;
        lock (appendLock) {
            appendCalls.Add(appendCall);
            if (appendCalls.Count == 1) {
                SD.MainThread.InvokeAsyncAndForget(ProcessAppendText);
            }
            waitForMainThread = appendCalls.Count > 2000;
        }
        if (waitForMainThread && SD.MainThread.InvokeRequired) {
            int sleepLength = 20;
            do {
                Thread.Sleep(sleepLength);
                sleepLength += 20;
                lock (appendLock)
                    waitForMainThread = appendCalls.Count > 2000;
            } while (waitForMainThread);
        }
    }

    void ProcessAppendText()
    {
        List<AppendCall> appendCalls;
        lock (appendLock) {
            appendCalls = this.appendCalls;
            this.appendCalls = new List<AppendCall>();
        }
        Debug.Assert(appendCalls.Count > 0);
        if (appendCalls.Count == 0)
            return;

        MessageViewCategory newCategory = appendCalls[appendCalls.Count - 1].Category;
        if (messageCategories[SelectedCategoryIndex] != newCategory) {
            SelectCategory(newCategory.Category);
            return;
        }

        bool clear;
        string text;
        if (appendCalls.Count == 1) {
            clear = appendCalls[0].ClearCategory;
            text = appendCalls[0].Text;
        } else {
            if (LoggingService.IsDebugEnabled) {
                LoggingService.Debug("CompilerMessageView: Combined " + appendCalls.Count + " appends.");
            }

            clear = false;
            StringBuilder b = new StringBuilder();
            foreach (AppendCall append in appendCalls) {
                if (append.Category == newCategory) {
                    if (append.ClearCategory) {
                        b.Length = 0;
                        clear = true;
                    }
                    b.Append(append.Text);
                }
            }
            text = b.ToString();
        }

        if (clear)
            textEditor.Text = text;
        else
            textEditor.AppendText(text);

        textEditor.ScrollToEnd();
    }

    public void SelectCategory(string categoryName)
    {
        for (int i = 0; i < messageCategories.Count; ++i) {
            MessageViewCategory category = (MessageViewCategory)messageCategories[i];
            if (category.Category == categoryName) {
                SelectedCategoryIndex = i;
                break;
            }
        }
    }

    public MessageViewCategory GetCategory(string categoryName)
    {
        foreach (MessageViewCategory category in messageCategories) {
            if (category.Category == categoryName) {
                return category;
            }
        }
        return null;
    }
    #endregion

    /// <summary>
    /// Changes wordwrap settings if that property has changed.
    /// </summary>
    void OnOptionsPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == OutputWindowOptionsPanel.WordWrapName) {
            SetWordWrap();
            ToolBarService.UpdateStatus(toolStrip.Items);
        }
        if (IsFontChanged(e.PropertyName)) {
            SetTextEditorFont();
        }
    }

    void OnMessageCategoryAdded(EventArgs e)
    {
        MessageCategoryAdded?.Invoke(this, e);
    }

    void OnSelectedCategoryIndexChanged(EventArgs e)
    {
        SelectedCategoryIndexChanged?.Invoke(this, e);
    }

    public event EventHandler MessageCategoryAdded;
    public event EventHandler SelectedCategoryIndexChanged;

    #region ICSharpCode.SharpDevelop.Gui.IClipboardHandler interface implementation

    public bool EnableCut => false;

    public bool EnableCopy => textEditor.SelectionLength > 0;

    public bool EnablePaste => false;

    public bool EnableDelete => false;

    public bool EnableSelectAll => textEditor.Document.TextLength > 0;

    public void Cut()
    {
    }

    public void Copy()
    {
        textEditor.Copy();
    }

    public void Paste()
    {
    }

    public void Delete()
    {
    }

    public void SelectAll()
    {
        textEditor.SelectAll();
    }
    #endregion
}
