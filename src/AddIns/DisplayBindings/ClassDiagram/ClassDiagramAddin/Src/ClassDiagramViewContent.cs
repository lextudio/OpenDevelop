using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using Microsoft.Win32;

namespace ICSharpCode.ClassDiagram;

public sealed class ClassDiagramViewContent : AbstractViewContent
{
    readonly Grid root = new Grid();
    readonly Canvas canvas = new Canvas();
    readonly TextBlock status = new TextBlock();
    readonly ScaleTransform scale = new ScaleTransform(1, 1);
    readonly ScrollViewer scroller;
    readonly Slider zoomSlider;
    readonly MsaglClassDiagramLayoutEngine layoutEngine = new MsaglClassDiagramLayoutEngine();
    ClassDiagramDocument document = new ClassDiagramDocument();
    Border draggedCard;
    ClassDiagramNodeState draggedState;
    Point dragOrigin;
    Point stateOrigin;
    bool showInheritance = true;
    bool showAssociations = true;
    bool showDependencies;
    string selectedTypeName;
    IReadOnlyList<ClassDiagramRoute> layoutRoutes = Array.Empty<ClassDiagramRoute>();
    readonly List<FileSystemWatcher> sourceWatchers = new List<FileSystemWatcher>();
    CancellationTokenSource refreshCancellation;
    long refreshVersion;
    bool disposed;

    public ClassDiagramViewContent(OpenedFile file) : base(file)
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        var refresh = CreateIconButton("Refresh", "Refresh from source", new Thickness(0));
        refresh.Click += delegate { RefreshFromSourcesAsync(debounce: false).FireAndForget(); };
        var arrange = CreateIconButton("Arrange", "Auto arrange", new Thickness(6, 0, 0, 0));
        arrange.Click += delegate { AutoArrange(); MarkDirty(); Render(); };
        var expand = CreateIconButton("ExpandAll", "Expand all", new Thickness(6, 0, 0, 0));
        expand.Click += delegate { SetCollapsed(false); };
        var collapse = CreateIconButton("CollapseAll", "Collapse all", new Thickness(6, 0, 0, 0));
        collapse.Click += delegate { SetCollapsed(true); };
        var addNote = CreateIconButton("Note", "Add note", new Thickness(6, 0, 0, 0));
        addNote.Click += delegate {
            document.Notes.Add(new ClassDiagramNote { X = 40, Y = 40, Text = "Note" });
            MarkDirty();
            Render();
        };
        var export = CreateIconButton("Export", "Export PNG", new Thickness(6, 0, 0, 0));
        export.Click += delegate { ExportPng(); };
        var fit = CreateIconButton("FitToScreen", "Fit to canvas", new Thickness(6, 0, 0, 0));
        fit.Click += delegate { FitToCanvas(); };
        var zoomImage = new Image {
            Source = PresentationResourceService.GetImageSource("ClassDiagram.Zoom"),
            Width = 16,
            Height = 16,
            Margin = new Thickness(12, 3, 4, 0),
            ToolTip = "Zoom"
        };
        zoomSlider = new Slider { Minimum = 0.1, Maximum = 2, Value = 1, Width = 160 };
        zoomSlider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e) {
            scale.ScaleX = scale.ScaleY = e.NewValue;
        };
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(arrange);
        toolbar.Children.Add(expand);
        toolbar.Children.Add(collapse);
        toolbar.Children.Add(addNote);
        toolbar.Children.Add(export);
        toolbar.Children.Add(fit);
        toolbar.Children.Add(zoomImage);
        toolbar.Children.Add(zoomSlider);
        AddRelationshipToggle(toolbar, "Inheritance", "Inheritance", true, value => showInheritance = value);
        AddRelationshipToggle(toolbar, "Relationship", "Associations", true, value => showAssociations = value);
        AddRelationshipToggle(toolbar, "Link", "Dependencies", false, value => showDependencies = value);

        canvas.RenderTransform = scale;
        scroller = new ScrollViewer {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        status.Margin = new Thickness(8, 4, 8, 8);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(status, 2);
        root.Children.Add(toolbar);
        root.Children.Add(scroller);
        root.Children.Add(status);
    }

    void FitToCanvas()
    {
        // Actual viewport dimensions are unavailable until the document has participated in
        // layout. A click during activation is simply retried once at dispatcher priority.
        if (scroller.ViewportWidth <= 0 || scroller.ViewportHeight <= 0 || canvas.Width <= 0 || canvas.Height <= 0) {
            _ = root.Dispatcher.BeginInvoke(new Action(FitToCanvas));
            return;
        }

        const double margin = 24;
        var availableWidth = Math.Max(1, scroller.ViewportWidth - margin * 2);
        var availableHeight = Math.Max(1, scroller.ViewportHeight - margin * 2);
        var fitScale = Math.Min(availableWidth / canvas.Width, availableHeight / canvas.Height);
        zoomSlider.Value = Math.Clamp(fitScale, zoomSlider.Minimum, zoomSlider.Maximum);
        scroller.ScrollToHorizontalOffset(0);
        scroller.ScrollToVerticalOffset(0);
    }

    public override object Control => root;

    public override void Load(OpenedFile file, Stream stream)
    {
        document = ClassDiagramDocument.Load(stream, System.IO.Path.GetDirectoryName(file.FileName) ?? string.Empty);
        if (document.SourceFiles.Count == 0) {
            var project = SD.ProjectService.CurrentProject;
            if (project is not null) {
                document.SourceFiles.AddRange(ClassDiagramProjectSources.GetSourceFiles(project));
                document.Refresh();
            }
        }
        var needsMeasuredLayout = !document.NodeStates.Values.Any(state => state.X != 0 || state.Y != 0);
        EnsureLayout();
        Render();
        if (needsMeasuredLayout) {
            // The first pass materializes/measures the real cards. Run layout once more with
            // those dimensions instead of the conservative fallback height.
            AutoArrange();
            Render();
        }
        StartWatchingSources();
    }

    public override void Save(OpenedFile file, Stream stream) =>
        document.Save(stream, System.IO.Path.GetDirectoryName(file.FileName) ?? string.Empty);

    public override void Dispose()
    {
        disposed = true;
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        foreach (var watcher in sourceWatchers)
            watcher.Dispose();
        sourceWatchers.Clear();
        base.Dispose();
    }

    async Task RefreshFromSourcesAsync(bool debounce)
    {
        var version = Interlocked.Increment(ref refreshVersion);
        var previousCancellation = refreshCancellation;
        refreshCancellation = new CancellationTokenSource();
        if (previousCancellation is not null) {
            await previousCancellation.CancelAsync();
            previousCancellation.Dispose();
        }
        var token = refreshCancellation.Token;
        try {
            if (debounce)
                await Task.Delay(300, token);
            var sourceFiles = GetCurrentProjectSourceFiles().ToArray();
            if (sourceFiles.Length == 0)
                sourceFiles = document.SourceFiles.ToArray();
            status.Text = "Analyzing source files…";
            var refreshed = await ClassDiagramDocument.CreateAsync(sourceFiles, token);
            token.ThrowIfCancellationRequested();
            if (version != Interlocked.Read(ref refreshVersion))
                return;
            refreshed.CopyUserStateFrom(document);
            document = refreshed;
            layoutRoutes = Array.Empty<ClassDiagramRoute>();
            Render();
            StartWatchingSources();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            SD.Log.Warn("Class diagram background refresh failed: " + ex);
            status.Text = "Refresh failed: " + ex.Message;
        }
    }

    IEnumerable<string> GetCurrentProjectSourceFiles()
    {
        var project = SD.ProjectService.CurrentProject;
        if (project is null)
            yield break;
        foreach (var path in ClassDiagramProjectSources.GetSourceFiles(project))
            yield return path;
    }

    void StartWatchingSources()
    {
        if (disposed)
            return;
        foreach (var watcher in sourceWatchers)
            watcher.Dispose();
        sourceWatchers.Clear();
        foreach (var directory in document.SourceFiles.Select(System.IO.Path.GetDirectoryName)
                     .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) {
            var watcher = new FileSystemWatcher(directory, "*.cs") {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler changed = delegate { ScheduleWatchedRefresh(); };
            RenamedEventHandler renamed = delegate { ScheduleWatchedRefresh(); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            sourceWatchers.Add(watcher);
        }
    }

    void ScheduleWatchedRefresh()
    {
        if (!disposed)
            _ = root.Dispatcher.BeginInvoke(new Action(() => RefreshFromSourcesAsync(debounce: true).FireAndForget()));
    }

    void Render()
    {
        canvas.Children.Clear();
        const double cardWidth = 280;
        const double gap = 30;

        foreach (var note in document.Notes) {
            var noteControl = CreateNote(note);
            Canvas.SetLeft(noteControl, note.X);
            Canvas.SetTop(noteControl, note.Y);
            canvas.Children.Add(noteControl);
        }
        foreach (var type in document.Types) {
            var state = document.NodeStates[ClassDiagramDocument.GetNodeId(type)];
            var card = CreateCard(type, state);
            Canvas.SetLeft(card, state.X);
            Canvas.SetTop(card, state.Y);
            canvas.Children.Add(card);
        }
        // Saved positions, source refreshes, collapsing, and manual dragging all retain the node
        // layout but still need fresh obstacle-aware routes using the cards' current dimensions.
        RouteCurrentPositions();
        if (showInheritance)
            DrawRelationships(cardWidth);
        DrawCodeRelationships(cardWidth);
        canvas.Width = Math.Max(800, Math.Max(
            document.NodeStates.Values.Select(state => state.X + cardWidth).DefaultIfEmpty().Max(),
            document.Notes.Select(note => note.X + note.Width).DefaultIfEmpty().Max()) + gap);
        canvas.Height = Math.Max(500, Math.Max(
            document.NodeStates.Values.Select(state => state.Y + 350).DefaultIfEmpty().Max(),
            document.Notes.Select(note => note.Y + note.Height).DefaultIfEmpty().Max()) + gap);
        status.Text = $"{document.Types.Count} types, {document.Relationships.Count} code relationships from {document.SourceFiles.Count} source files";
        if (!string.IsNullOrEmpty(selectedTypeName))
            SelectType(selectedTypeName);
    }

    FrameworkElement CreateNote(ClassDiagramNote note)
    {
        var panel = new DockPanel();
        var header = new TextBlock {
            Text = "Note",
            Background = Brushes.Goldenrod,
            Padding = new Thickness(6, 3, 6, 3),
            Cursor = Cursors.SizeAll
        };
        DockPanel.SetDock(header, Dock.Top);
        var editor = new TextBox {
            Text = note.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.LightYellow,
            Padding = new Thickness(6)
        };
        editor.TextChanged += delegate { note.Text = editor.Text; MarkDirty(); };
        panel.Children.Add(header);
        panel.Children.Add(editor);
        var border = new Border {
            Width = note.Width,
            Height = note.Height,
            Background = Brushes.LightYellow,
            BorderBrush = Brushes.DarkGoldenrod,
            BorderThickness = new Thickness(1),
            Child = panel
        };
        Point origin = default;
        Point noteOrigin = default;
        header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            origin = e.GetPosition(canvas);
            noteOrigin = new Point(note.X, note.Y);
            header.CaptureMouse();
            e.Handled = true;
        };
        header.MouseMove += delegate(object sender, MouseEventArgs e) {
            if (!header.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
                return;
            var current = e.GetPosition(canvas);
            note.X = Math.Max(0, noteOrigin.X + current.X - origin.X);
            note.Y = Math.Max(0, noteOrigin.Y + current.Y - origin.Y);
            Canvas.SetLeft(border, note.X);
            Canvas.SetTop(border, note.Y);
        };
        header.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
            if (header.IsMouseCaptured) {
                header.ReleaseMouseCapture();
                MarkDirty();
            }
        };
        return border;
    }

    void ExportPng()
    {
        var dialog = new SaveFileDialog {
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = System.IO.Path.GetFileNameWithoutExtension(PrimaryFileName) + ".png"
        };
        if (dialog.ShowDialog() != true)
            return;
        canvas.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(canvas.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(canvas.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    Border CreateCard(ClassDiagramType type, ClassDiagramNodeState state)
    {
        var panel = new StackPanel();
        var header = new TextBlock {
            Text = $"«{type.Kind}»  {type.Name}",
            FontWeight = FontWeights.SemiBold,
            Background = SystemColors.ControlBrush,
            Padding = new Thickness(8),
            Cursor = Cursors.Hand
        };
        header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            if (e.ClickCount == 1)
                SelectType(type.Name);
            else if (e.ClickCount == 2)
                FileService.JumpToFilePosition(type.SourceFile, type.SourceLine, 1);
        };
        header.MouseRightButtonUp += delegate {
            state.Collapsed = !state.Collapsed;
            MarkDirty();
            Render();
        };
        panel.Children.Add(header);
        if (!state.Collapsed && type.BaseTypes.Count > 0)
            panel.Children.Add(new TextBlock {
                Text = "inherits: " + string.Join(", ", type.BaseTypes),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8, 5, 8, 5),
                Foreground = Brushes.DimGray
            });
        if (!state.Collapsed) {
            AddMemberGroup(panel, "Fields", type.Members.Where(member => member.Kind == ClassDiagramMemberKind.Field),
                type.SourceFile, state.FieldsCollapsed, value => state.FieldsCollapsed = value);
            AddMemberGroup(panel, "Properties", type.Members.Where(member => member.Kind == ClassDiagramMemberKind.Property),
                type.SourceFile, state.PropertiesCollapsed, value => state.PropertiesCollapsed = value);
            AddMemberGroup(panel, "Events", type.Members.Where(member => member.Kind == ClassDiagramMemberKind.Event),
                type.SourceFile, state.EventsCollapsed, value => state.EventsCollapsed = value);
            AddMemberGroup(panel, "Methods", type.Members.Where(member => member.Kind == ClassDiagramMemberKind.Method),
                type.SourceFile, state.MethodsCollapsed, value => state.MethodsCollapsed = value);
        }
        var border = new Border {
            Width = 280,
            MinHeight = state.Collapsed ? 42 : 120,
            MaxHeight = state.Collapsed ? 42 : 315,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Background = SystemColors.WindowBrush,
            Child = panel,
            Tag = ClassDiagramDocument.GetNodeId(type)
        };
        border.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            if (e.ClickCount != 1)
                return;
            draggedCard = border;
            draggedState = state;
            dragOrigin = e.GetPosition(canvas);
            stateOrigin = new Point(state.X, state.Y);
            border.CaptureMouse();
            e.Handled = true;
        };
        border.MouseMove += DragCard;
        border.MouseLeftButtonUp += EndDrag;
        return border;
    }

    void SelectType(string typeName)
    {
        selectedTypeName = document.Types.FirstOrDefault(type => type.QualifiedName == typeName)?.QualifiedName
            ?? document.Types.FirstOrDefault(type => Normalize(type.Name) == Normalize(typeName))?.QualifiedName;
        if (selectedTypeName is null)
            return;
        var related = new HashSet<string>(StringComparer.Ordinal) { selectedTypeName };
        var selected = document.Types.FirstOrDefault(type => type.QualifiedName == selectedTypeName);
        if (selected is not null) {
            foreach (var baseType in selected.BaseTypeIdentities)
                related.Add(baseType);
            foreach (var candidate in document.Types.Where(type =>
                         type.BaseTypeIdentities.Contains(selectedTypeName)))
                related.Add(candidate.QualifiedName);
        }
        foreach (var relationship in document.Relationships.Where(relationship =>
                     relationship.SourceType == selectedTypeName || relationship.TargetType == selectedTypeName)) {
            related.Add(relationship.SourceType);
            related.Add(relationship.TargetType);
        }
        foreach (var card in canvas.Children.OfType<Border>().Where(card => card.Tag is string)) {
            var type = document.Types.FirstOrDefault(candidate =>
                ClassDiagramDocument.GetNodeId(candidate) == (string)card.Tag);
            if (type is null)
                continue;
            var name = type.QualifiedName;
            card.Opacity = related.Contains(name) ? 1 : 0.35;
            card.BorderBrush = name == selectedTypeName ? Brushes.DodgerBlue : SystemColors.ControlDarkBrush;
            card.BorderThickness = name == selectedTypeName ? new Thickness(3) : new Thickness(1);
        }
        status.Text = $"Selected {selected?.Name ?? selectedTypeName} — {related.Count - 1} directly related types";
    }

    void AddMemberGroup(
        Panel panel,
        string title,
        IEnumerable<ClassDiagramMember> memberSource,
        string sourceFile,
        bool collapsed,
        Action<bool> setCollapsed)
    {
        var members = memberSource.ToList();
        if (members.Count == 0)
            return;
        var rows = new StackPanel();
        foreach (var member in members.Take(12)) {
            var row = new TextBlock {
                Text = member.DisplayText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 2, 8, 2),
                Cursor = Cursors.Hand
            };
            row.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ClickCount == 2) {
                    FileService.JumpToFilePosition(sourceFile, member.SourceLine, 1);
                    e.Handled = true;
                }
            };
            rows.Children.Add(row);
        }
        if (members.Count > 12)
            rows.Children.Add(new TextBlock { Text = $"… {members.Count - 12} more", Margin = new Thickness(8, 2, 8, 4) });
        var expander = new Expander {
            Header = $"{title} ({members.Count})",
            Content = rows,
            IsExpanded = !collapsed
        };
        expander.Expanded += delegate { setCollapsed(false); MarkDirty(); };
        expander.Collapsed += delegate { setCollapsed(true); MarkDirty(); };
        panel.Children.Add(expander);
    }

    void DrawRelationships(double cardWidth)
    {
        for (var childIndex = 0; childIndex < document.Types.Count; childIndex++) {
            var child = document.Types[childIndex];
            foreach (var baseName in child.BaseTypeIdentities) {
                var parentIndex = document.Types.FindIndex(type => type.QualifiedName == baseName);
                if (parentIndex < 0)
                    continue;
                var childState = document.NodeStates[ClassDiagramDocument.GetNodeId(child)];
                var parent = document.Types[parentIndex];
                var parentState = document.NodeStates[ClassDiagramDocument.GetNodeId(parent)];
                var route = layoutRoutes.FirstOrDefault(candidate => candidate.IsInheritance
                    && candidate.Source == child.QualifiedName && candidate.Target == parent.QualifiedName);
                var points = route?.Points ?? new[] {
                    new ClassDiagramRoutePoint(childState.X + cardWidth / 2, childState.Y),
                    new ClassDiagramRoutePoint(parentState.X + cardWidth / 2, parentState.Y + (parentState.Collapsed ? 42 : 315))
                };
                var line = CreateRoute(points, Brushes.SteelBlue, 2);
                if (parent.Kind == "interface")
                    line.StrokeDashArray = new DoubleCollection { 6, 4 };
                Panel.SetZIndex(line, -1);
                canvas.Children.Add(line);
                AddHollowTriangle(points[0].X, points[0].Y, points[^1].X, points[^1].Y);
            }
        }
    }

    void AddHollowTriangle(double fromX, double fromY, double tipX, double tipY)
    {
        var dx = tipX - fromX;
        var dy = tipY - fromY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;
        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        var triangle = new Polygon {
            Points = new PointCollection {
                new Point(tipX, tipY),
                new Point(tipX - ux * 15 + px * 8, tipY - uy * 15 + py * 8),
                new Point(tipX - ux * 15 - px * 8, tipY - uy * 15 - py * 8)
            },
            Fill = SystemColors.WindowBrush,
            Stroke = Brushes.SteelBlue,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(triangle, -1);
        canvas.Children.Add(triangle);
    }

    void DrawCodeRelationships(double cardWidth)
    {
        foreach (var relationship in document.Relationships) {
            if (relationship.Kind != ClassDiagramRelationshipKind.Dependency && !showAssociations
                || relationship.Kind == ClassDiagramRelationshipKind.Dependency && !showDependencies)
                continue;
            var source = document.Types.FirstOrDefault(type => type.QualifiedName == relationship.SourceType);
            var target = document.Types.FirstOrDefault(type => type.QualifiedName == relationship.TargetType);
            if (source is null || target is null)
                continue;
            var sourceState = document.NodeStates[ClassDiagramDocument.GetNodeId(source)];
            var targetState = document.NodeStates[ClassDiagramDocument.GetNodeId(target)];
            var fromX = sourceState.X + cardWidth / 2;
            var fromY = sourceState.Y + (sourceState.Collapsed ? 21 : 157);
            var toX = targetState.X + cardWidth / 2;
            var toY = targetState.Y + (targetState.Collapsed ? 21 : 157);
            var dependency = relationship.Kind == ClassDiagramRelationshipKind.Dependency;
            var brush = relationship.Kind switch {
                ClassDiagramRelationshipKind.Aggregation => Brushes.DarkOrange,
                ClassDiagramRelationshipKind.Composition => Brushes.Purple,
                ClassDiagramRelationshipKind.Association => Brushes.SeaGreen,
                _ => Brushes.DimGray
            };
            var route = layoutRoutes.FirstOrDefault(candidate => !candidate.IsInheritance
                && candidate.Source == relationship.SourceType && candidate.Target == relationship.TargetType
                && candidate.Kind == relationship.Kind);
            var points = route?.Points ?? new[] {
                new ClassDiagramRoutePoint(fromX, fromY),
                new ClassDiagramRoutePoint(toX, toY)
            };
            var line = CreateRoute(points, brush, dependency ? 1 : 1.5);
            if (dependency)
                line.StrokeDashArray = new DoubleCollection { 4, 4 };
            Panel.SetZIndex(line, -2);
            canvas.Children.Add(line);
            if (relationship.Kind is ClassDiagramRelationshipKind.Aggregation or ClassDiagramRelationshipKind.Composition)
                AddDiamond(points[0].X, points[0].Y, points[1].X, points[1].Y, brush, relationship.Kind == ClassDiagramRelationshipKind.Composition);
            else
                AddOpenArrow(points[^2].X, points[^2].Y, points[^1].X, points[^1].Y, brush);
        }
    }

    Polyline CreateRoute(IReadOnlyList<ClassDiagramRoutePoint> points, Brush brush, double thickness) =>
        new Polyline {
            Tag = "ClassDiagramRoute",
            Points = new PointCollection(points.Select(point => new Point(point.X, point.Y))),
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };

    void AddDiamond(double ownerX, double ownerY, double targetX, double targetY, Brush brush, bool filled)
    {
        var dx = targetX - ownerX;
        var dy = targetY - ownerY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;
        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        var diamond = new Polygon {
            Points = new PointCollection {
                new Point(ownerX, ownerY),
                new Point(ownerX + ux * 10 + px * 7, ownerY + uy * 10 + py * 7),
                new Point(ownerX + ux * 20, ownerY + uy * 20),
                new Point(ownerX + ux * 10 - px * 7, ownerY + uy * 10 - py * 7)
            },
            Fill = filled ? brush : SystemColors.WindowBrush,
            Stroke = brush,
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(diamond, -2);
        canvas.Children.Add(diamond);
    }

    // Icon-only toolbar button (VS2017 Image Library icons via PresentationResourceService -
    // "ClassDiagram.X" resolves to Resources/VS2017/X/X_16x.xaml). Falls back to a text button if
    // the icon is unavailable so the command stays discoverable.
    static Button CreateIconButton(string iconName, string toolTip, Thickness margin)
    {
        var icon = PresentationResourceService.GetImageSource("ClassDiagram." + iconName);
        return new Button {
            Content = icon != null ? new Image { Source = icon, Width = 16, Height = 16 } : new TextBlock { Text = toolTip },
            ToolTip = toolTip,
            Padding = new Thickness(5, 2, 5, 2),
            Margin = margin
        };
    }

    void AddRelationshipToggle(Panel panel, string iconName, string toolTip, bool initialValue, Action<bool> setter)
    {
        var icon = PresentationResourceService.GetImageSource("ClassDiagram." + iconName);
        var toggle = new CheckBox {
            Content = icon != null ? new Image { Source = icon, Width = 16, Height = 16 } : new TextBlock { Text = toolTip },
            ToolTip = toolTip,
            IsChecked = initialValue,
            Margin = new Thickness(10, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += delegate { setter(true); Render(); };
        toggle.Unchecked += delegate { setter(false); Render(); };
        panel.Children.Add(toggle);
    }

    void AddOpenArrow(double fromX, double fromY, double tipX, double tipY, Brush brush)
    {
        var dx = tipX - fromX;
        var dy = tipY - fromY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;
        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        var arrow = new Polyline {
            Points = new PointCollection {
                new Point(tipX - ux * 12 + px * 6, tipY - uy * 12 + py * 6),
                new Point(tipX, tipY),
                new Point(tipX - ux * 12 - px * 6, tipY - uy * 12 - py * 6)
            },
            Stroke = brush,
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(arrow, -2);
        canvas.Children.Add(arrow);
    }

    void EnsureLayout()
    {
        if (document.NodeStates.Values.Any(state => state.X != 0 || state.Y != 0)) {
            RouteCurrentPositions();
            return;
        }
        AutoArrange();
    }

    void RouteCurrentPositions()
    {
        try {
            layoutRoutes = layoutEngine.Route(document, GetMeasuredNodeSizes());
        } catch (Exception ex) {
            SD.Log.Warn("MSAGL class diagram routing failed: " + ex.Message);
            layoutRoutes = Array.Empty<ClassDiagramRoute>();
        }
    }

    void AutoArrange()
    {
        if (document.Types.Count == 0)
            return;
        try {
            var measuredSizes = GetMeasuredNodeSizes();
            layoutRoutes = layoutEngine.Arrange(document, measuredSizes);
        } catch (Exception ex) {
            SD.Log.Warn("MSAGL class diagram layout failed; using grid fallback: " + ex.Message);
            ArrangeGridFallback();
            layoutRoutes = Array.Empty<ClassDiagramRoute>();
        }
    }

    IReadOnlyDictionary<string, ClassDiagramNodeSize> GetMeasuredNodeSizes()
    {
        foreach (var card in canvas.Children.OfType<Border>()) {
            if (card.DesiredSize.Width <= 0 || card.DesiredSize.Height <= 0)
                card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        return canvas.Children.OfType<Border>()
            .Where(card => card.Tag is string)
            .ToDictionary(
                card => (string)card.Tag,
                card => new ClassDiagramNodeSize(
                    card.ActualWidth > 0 ? card.ActualWidth : card.DesiredSize.Width,
                    card.ActualHeight > 0 ? card.ActualHeight : card.DesiredSize.Height),
                StringComparer.Ordinal);
    }

    void ArrangeGridFallback()
    {
        const int columns = 4;
        const double width = 310;
        const double height = 345;
        for (var index = 0; index < document.Types.Count; index++) {
            var state = document.NodeStates[ClassDiagramDocument.GetNodeId(document.Types[index])];
            state.X = 30 + index % columns * width;
            state.Y = 30 + index / columns * height;
        }
    }

    void SetCollapsed(bool collapsed)
    {
        foreach (var state in document.NodeStates.Values) {
            state.Collapsed = collapsed;
            state.FieldsCollapsed = collapsed;
            state.PropertiesCollapsed = collapsed;
            state.EventsCollapsed = collapsed;
            state.MethodsCollapsed = collapsed;
        }
        MarkDirty();
        Render();
    }

    void DragCard(object sender, MouseEventArgs e)
    {
        if (draggedCard is null || draggedState is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(canvas);
        draggedState.X = Math.Max(0, stateOrigin.X + current.X - dragOrigin.X);
        draggedState.Y = Math.Max(0, stateOrigin.Y + current.Y - dragOrigin.Y);
        Canvas.SetLeft(draggedCard, draggedState.X);
        Canvas.SetTop(draggedCard, draggedState.Y);
    }

    void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (draggedCard is null)
            return;
        draggedCard.ReleaseMouseCapture();
        draggedCard = null;
        draggedState = null;
        RouteCurrentPositions();
        MarkDirty();
        Render();
        e.Handled = true;
    }

    void MarkDirty()
    {
        if (PrimaryFile is not null)
            PrimaryFile.MakeDirty();
    }

    static string Normalize(string name)
    {
        var generic = name.IndexOf('<');
        return (generic >= 0 ? name.Substring(0, generic) : name).Trim();
    }
}
