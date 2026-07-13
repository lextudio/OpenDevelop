using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace Hornung.ResourceToolkit.Gui
{
    public class UnusedResourceKeysViewContent : AbstractViewContent, IFilterHost<ResourceItem>
    {
        readonly ICollection<ResourceItem> unusedKeys;
        readonly StackPanel panel;
        readonly ListView listView;
        readonly ToolBar toolBar;

        public override object Control {
            get { return this.panel; }
        }

        public ListView ListView {
            get { return this.listView; }
        }

        public ICollection<ResourceItem> UnusedKeys {
            get { return this.unusedKeys; }
        }

        public UnusedResourceKeysViewContent(ICollection<ResourceItem> unusedKeys)
        {
            LoggingService.Debug("ResourceToolkit: Creating new UnusedResourceKeysViewContent");

            SetLocalizedTitle("${res:Hornung.ResourceToolkit.UnusedResourceKeys.Title}");

            if (unusedKeys == null) {
                throw new ArgumentNullException("unusedKeys");
            }
            this.unusedKeys = unusedKeys;

            this.panel = new StackPanel { Orientation = Orientation.Vertical };

            this.toolBar = new ToolBar();
            this.toolBar.Loaded += ToolBarLoaded;
            this.panel.Children.Add(this.toolBar);

            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:Global.FileName}"), Width = 60 });
            gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:Hornung.ResourceToolkit.Key}"), Width = 140 });
            gridView.Columns.Add(new GridViewColumn { Header = StringParser.Parse("${res:Hornung.ResourceToolkit.Value}"), Width = 140 });

            this.listView = new ListView {
                View = gridView,
                SelectionMode = SelectionMode.Extended
            };

            this.listView.SizeChanged += (sender, e) => {
                if (this.listView.View is GridView gv && gv.Columns.Count >= 3 && this.listView.ActualWidth > 0) {
                    gv.Columns[0].Width = this.listView.ActualWidth * 0.20;
                    gv.Columns[1].Width = this.listView.ActualWidth * 0.45;
                    gv.Columns[2].Width = this.listView.ActualWidth * 0.30;
                }
            };

            this.listView.Loaded += (sender, e) => this.FillListView();
            this.panel.Children.Add(this.listView);
        }

        void ToolBarLoaded(object sender, RoutedEventArgs e)
        {
            this.toolBar.Loaded -= ToolBarLoaded;
            var items = ToolBarService.CreateToolBarItems(this.panel, this, "/AddIns/ResourceToolkit/ViewContent/UnusedResourceKeys/Toolbar");
            foreach (var item in items) {
                this.toolBar.Items.Add(item);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
        }

        bool fillListViewQueued;

        public void FillListView()
        {
            if (!this.fillListViewQueued) {
                this.fillListViewQueued = true;
                this.listView.Dispatcher.BeginInvoke(new Action(this.FillListViewInternal));
            }
        }

        void FillListViewInternal()
        {
            try
            {
                this.listView.Items.Clear();

                foreach (ResourceItem entry in this.UnusedKeys)
                {
                    if (!this.ItemMatchesCurrentFilter(entry))
                    {
                        continue;
                    }

                    IResourceFileContent c = ResourceFileContentRegistry.GetResourceFileContent(entry.FileName);
                    object o;

                    var item = new ListViewItem();
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var fileNameBlock = new TextBlock { Text = Path.GetFileName(entry.FileName), ToolTip = entry.FileName };
                    Grid.SetColumn(fileNameBlock, 0);
                    grid.Children.Add(fileNameBlock);

                    var keyBlock = new TextBlock { Text = entry.Key };
                    Grid.SetColumn(keyBlock, 1);
                    grid.Children.Add(keyBlock);

                    if (c.TryGetValue(entry.Key, out o))
                    {
                        var valueBlock = new TextBlock { Text = (o ?? (object)"<<null>>").ToString() };
                        Grid.SetColumn(valueBlock, 2);
                        grid.Children.Add(valueBlock);
                    }
                    else
                    {
                        throw new InvalidOperationException("The key '" + entry.Key + "' in file '" + entry.FileName + "' does not exist although it was reported as unused.");
                    }

                    item.Content = grid;
                    item.Tag = entry.FileName;

                    this.listView.Items.Add(item);
                }
            }
            finally
            {
                this.fillListViewQueued = false;
            }
        }

        #region Filter

        readonly List<IFilter<ResourceItem>> filters = new List<IFilter<ResourceItem>>();

        public void RegisterFilter(IFilter<ResourceItem> filter)
        {
            if (filter == null)
            {
                throw new ArgumentNullException("filter");
            }

            if (!this.filters.Contains(filter))
            {
                this.filters.Add(filter);
            }

            this.FillListView();
        }

        public void UnregisterFilter(IFilter<ResourceItem> filter)
        {
            if (filter == null)
            {
                throw new ArgumentNullException("filter");
            }

            this.filters.Remove(filter);
            this.FillListView();
        }

        bool ItemMatchesCurrentFilter(ResourceItem item)
        {
            foreach (IFilter<ResourceItem> filter in this.filters)
            {
                if (!filter.IsMatch(item))
                {
                    return false;
                }
            }
            return true;
        }

        #endregion

        public void DeleteResources(System.Collections.IEnumerable itemsToDelete)
        {
            try
            {
                List<ListViewItem> items = new List<ListViewItem>();
                foreach (ListViewItem item in itemsToDelete)
                {
                    DeleteResourceKey((string)item.Tag, ((Grid)item.Content).Children[1].ToString());
                    items.Add(item);
                }

                foreach (var item in items)
                {
                    this.listView.Items.Remove(item);
                }
            }
            finally
            {
            }
        }

        protected static void DeleteResourceKey(string fileName, string key)
        {
            try
            {
                IResourceFileContent content = ResourceFileContentRegistry.GetResourceFileContent(fileName);
                if (content != null)
                {
                    if (content.ContainsKey(key))
                    {
                        LoggingService.Debug("ResourceToolkit: Remove key '" + key + "' from resource file '" + fileName + "'");
                        content.RemoveKey(key);
                    }
                    else
                    {
                        MessageService.ShowWarningFormatted("${res:Hornung.ResourceToolkit.KeyNotFoundWarning}", key, fileName);
                    }
                }
                else
                {
                    MessageService.ShowWarning("ResoureToolkit: Could not get ResourceFileContent for '" + fileName + "' key +'" + key + "'.");
                }
            }
            catch (Exception ex)
            {
                MessageService.ShowWarningFormatted("${res:Hornung.ResourceToolkit.ErrorProcessingResourceFile}" + Environment.NewLine + ex.Message, fileName);
                return;
            }

            foreach (KeyValuePair<string, IResourceFileContent> entry in ResourceFileContentRegistry.GetLocalizedContents(fileName))
            {
                LoggingService.Debug("ResourceToolkit: Looking in localized resource file: '" + entry.Value.FileName + "'");
                try
                {
                    if (entry.Value.ContainsKey(key))
                    {
                        LoggingService.Debug("ResourceToolkit:   -> Key found, removing.");
                        entry.Value.RemoveKey(key);
                    }
                }
                catch (Exception ex)
                {
                    MessageService.ShowWarningFormatted("${res:Hornung.ResourceToolkit.ErrorProcessingResourceFile}" + Environment.NewLine + ex.Message, entry.Value.FileName);
                }
            }
        }
    }
}
