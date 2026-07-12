using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;

using AvalonDock;
using AvalonDock.Core;
using AvalonDock.Core.Serialization;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;

using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.SharpDevelop.Workbench;

internal sealed class DockWorkspace : ObservableObjectBase, ILayoutUpdateStrategy
{
    private readonly DockingManager dockingManager;
    private readonly ObservableCollection<AvalonWorkbenchWindow> documents = new ObservableCollection<AvalonWorkbenchWindow>();
    private ReadOnlyCollection<ToolPaneModel> toolPanes;

    public DockWorkspace(DockingManager dockingManager)
    {
        this.dockingManager = dockingManager;
        Current = this;
    }

    public static DockWorkspace Current { get; private set; }

    public ReadOnlyCollection<ToolPaneModel> ToolPanes =>
        toolPanes ??= OpenDevelopMefHost.ExportProvider
            .GetExportedValues<ToolPaneModel>("ToolPane")
            .OrderBy(item => item.Title)
            .ToArray()
            .AsReadOnly();

    public ReadOnlyObservableCollection<AvalonWorkbenchWindow> Documents { get; private set; }

    public AvalonWorkbenchWindow ActiveDocument {
        get => dockingManager.ActiveContent as AvalonWorkbenchWindow;
        set => dockingManager.ActiveContent = value;
    }

    public void AddDocument(AvalonWorkbenchWindow document, bool activate)
    {
        documents.Add(document);
        document.IsVisible = true;
        if (activate) {
            document.IsSelected = true;
            document.IsActive = true;
            ActiveDocument = document;
        }
    }

    public void RemoveDocument(AvalonWorkbenchWindow document)
    {
        documents.Remove(document);
    }

    public bool ContainsToolPane(string contentId)
    {
        return ToolPanes.Any(pane => pane.ContentId == contentId);
    }

    public bool ShowToolPane(string contentId)
    {
        var pane = ToolPanes.FirstOrDefault(p => p.ContentId == contentId);
        if (pane == null)
            return false;
        pane.Show();
        return true;
    }

    public void Remove(PaneModel model)
    {
        if (model is AvalonWorkbenchWindow document) {
            document.CloseWindow(false);
        } else if (model is ToolPaneModel tool) {
            tool.IsVisible = false;
        }
    }

    public void InitializeLayout()
    {
        Documents = new ReadOnlyObservableCollection<AvalonWorkbenchWindow>(documents);
        dockingManager.DataContext = this;
        dockingManager.LayoutUpdateStrategy = this;
    }

    public void BindSources()
    {
        dockingManager.SetBinding(DockingManager.AnchorablesSourceProperty, new Binding(nameof(ToolPanes)) { Source = this });
        dockingManager.SetBinding(DockingManager.DocumentsSourceProperty, new Binding(nameof(Documents)) { Source = this });
    }

    public void RestoreLayout(string fileName)
    {
        if (!File.Exists(fileName))
            return;

        var serializer = new XmlLayoutSerializer(dockingManager);
        serializer.LayoutSerializationCallback += LayoutSerializationCallback;
        try {
            serializer.Deserialize(fileName);
        } finally {
            serializer.LayoutSerializationCallback -= LayoutSerializationCallback;
        }
    }

    public void SaveLayout(string fileName)
    {
        var serializer = new XmlLayoutSerializer(dockingManager);
        serializer.Serialize(fileName);
    }

    private void LayoutSerializationCallback(object sender, LayoutSerializationCallbackEventArgs e)
    {
        if (e.Model is LayoutDocument) {
            e.Cancel = true;
            return;
        }

        if (e.Model is not LayoutAnchorable anchorable) {
            e.Cancel = true;
            return;
        }

        var pane = ToolPanes.FirstOrDefault(p => p.ContentId == anchorable.ContentId);
        if (pane == null) {
            e.Cancel = true;
            return;
        }

        e.Content = pane;
        anchorable.CanDockAsTabbedDocument = false;
        pane.IsVisible = true;
    }

    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        anchorableToShow.CanDockAsTabbedDocument = false;
        return false;
    }

    public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
    {
        anchorableShown.IsActive = true;
        anchorableShown.IsSelected = true;
        if (anchorableShown.ContentId == "ProjectBrowser" && anchorableShown.Parent is LayoutAnchorablePane pane)
            pane.DockWidth = new GridLength(280);
    }

    public bool BeforeInsertDocument(LayoutRoot layout, LayoutDocument anchorableToShow, ILayoutContainer destinationContainer)
    {
        return false;
    }

    public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
    {
    }
}
