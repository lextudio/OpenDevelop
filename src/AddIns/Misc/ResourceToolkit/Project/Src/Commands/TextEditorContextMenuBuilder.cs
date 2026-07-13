using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Controls;
using Hornung.ResourceToolkit.Gui;
using Hornung.ResourceToolkit.Refactoring;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Refactoring;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Hornung.ResourceToolkit.Commands
{
    public sealed class TextEditorContextMenuBuilder : IMenuItemBuilder
    {
        static readonly System.Windows.Controls.Control[] EmptyControlArray = new System.Windows.Controls.Control[0];

        IEnumerable<object> IMenuItemBuilder.BuildItems(Codon codon, object owner)
        {
            ITextEditor editor = owner as ITextEditor;
            if (editor == null) {
                return EmptyControlArray;
            }

            ResourceResolveResult result = ResourceResolverService.Resolve(editor, null);
            if (result != null && result.ResourceFileContent != null && result.Key != null) {

                List<MenuItem> items = new List<MenuItem>();
                MenuItem item = new MenuItem();

                if (result.ResourceFileContent.ContainsKey(result.Key)) {
                    item.Header = MenuService.ConvertLabel(StringParser.Parse("${res:Hornung.ResourceToolkit.TextEditorContextMenu.EditResource}"));
                } else {
                    item.Header = MenuService.ConvertLabel(StringParser.Parse("${res:Hornung.ResourceToolkit.TextEditorContextMenu.AddResource}"));
                }
                item.Click += this.EditResource;
                item.Tag = result;
                items.Add(item);

                item = new MenuItem();
                item.Header = MenuService.ConvertLabel(StringParser.Parse("${res:SharpDevelop.Refactoring.FindReferencesCommand}"));
                item.Click += this.FindReferences;
                item.Tag = result;
                items.Add(item);

                item = new MenuItem();
                item.Header = MenuService.ConvertLabel(StringParser.Parse("${res:SharpDevelop.Refactoring.RenameCommand}"));
                item.Click += this.Rename;
                item.Tag = result;
                items.Add(item);

                item = new MenuItem();
                item.Header = result.Key;
                item.ItemsSource = items;
                return new System.Windows.Controls.Control[] { item, new Separator() };
            }

            return EmptyControlArray;
        }

        void EditResource(object sender, EventArgs e)
        {
            MenuItem item = sender as MenuItem;
            if (item == null) return;

            ResourceResolveResult result = item.Tag as ResourceResolveResult;
            if (result == null) return;

            object value;
            string svalue = null;
            if (result.ResourceFileContent.TryGetValue(result.Key, out value)) {
                svalue = value as string;
                if (svalue == null) {
                    MessageService.ShowWarning("${res:Hornung.ResourceToolkit.ResourceTypeNotSupported}");
                    return;
                }
            }

            EditStringResourceDialog dialog = new EditStringResourceDialog(result.ResourceFileContent, result.Key, svalue, false);
            if (svalue == null) {
                dialog.Title = String.Format(CultureInfo.CurrentCulture, StringParser.Parse("${res:Hornung.ResourceToolkit.CodeCompletion.AddNewDescription}"), result.ResourceFileContent.FileName);
            }
            dialog.Owner = WorkbenchSingleton.MainWindow;
            if (dialog.ShowDialog() == true) {
                if (svalue == null) {
                    result.ResourceFileContent.Add(dialog.Key, dialog.Value);
                } else {
                    result.ResourceFileContent.SetValue(result.Key, dialog.Value);
                }
            }
        }

        void FindReferences(object sender, EventArgs e)
        {
            MenuItem item = sender as MenuItem;
            if (item == null) return;

            ResourceResolveResult result = item.Tag as ResourceResolveResult;
            if (result == null) return;

            FindReferencesAndRenameHelper.RunFindReferences((ICSharpCode.TypeSystem.IEntity)null);
        }

        void Rename(object sender, EventArgs e)
        {
            MenuItem item = sender as MenuItem;
            if (item == null) return;

            ResourceResolveResult result = item.Tag as ResourceResolveResult;
            if (result == null) return;

            ResourceRefactoringService.Rename(result);
        }
    }
}
