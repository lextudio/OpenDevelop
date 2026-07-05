// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Creates WPF menu controls from the AddIn Tree.
	/// </summary>
	public static class MenuService
	{
		internal sealed class MenuCreateContext
		{
			public UIElement InputBindingOwner;
			public string ActivationMethod;
			public bool ImmediatelyExpandMenuBuildersForShortcuts;
		}
		
		static Dictionary<string, System.Windows.Input.ICommand> knownCommands = LoadDefaultKnownCommands();
		
		static Dictionary<string, System.Windows.Input.ICommand> LoadDefaultKnownCommands()
		{
			var knownCommands = new Dictionary<string, System.Windows.Input.ICommand>();
			foreach (Type t in new Type[] { typeof(ApplicationCommands), typeof(NavigationCommands) }) {
				foreach (PropertyInfo p in t.GetProperties()) {
					knownCommands.Add(p.Name, (System.Windows.Input.ICommand)p.GetValue(null, null));
				}
			}
			return knownCommands;
		}
		
		/// <summary>
		/// Gets a known WPF command.
		/// </summary>
		/// <param name="commandName">The name of the command, e.g. "Copy".</param>
		/// <returns>The WPF ICommand with the given name, or null if the command was not found.</returns>
		public static System.Windows.Input.ICommand GetKnownCommand(string commandName)
		{
			if (commandName == null)
				throw new ArgumentNullException("commandName");
			System.Windows.Input.ICommand command;
			lock (knownCommands) {
				if (knownCommands.TryGetValue(commandName, out command))
					return command;
			}
			return null;
		}
		
		/// <summary>
		/// Registers a WPF command for use with the &lt;MenuItem command="name"&gt; syntax.
		/// </summary>
		public static void RegisterKnownCommand(string name, System.Windows.Input.ICommand command)
		{
			if (name == null)
				throw new ArgumentNullException("name");
			if (command == null)
				throw new ArgumentNullException("command");
			lock (knownCommands) {
				knownCommands.Add(name, command);
			}
		}
		
		public static void UpdateStatus(IEnumerable menuItems)
		{
			if (menuItems == null)
				return;
			foreach (object o in menuItems) {
				IStatusUpdate cmi = o as IStatusUpdate;
				if (cmi != null)
					cmi.UpdateStatus();
			}
		}
		
		public static void UpdateText(IEnumerable menuItems)
		{
			if (menuItems == null)
				return;
			foreach (object o in menuItems) {
				IStatusUpdate cmi = o as IStatusUpdate;
				if (cmi != null)
					cmi.UpdateText();
			}
		}
		
		public static ContextMenu CreateContextMenu(object owner, string addInTreePath)
		{
			return CreateContextMenu(
				new MenuCreateContext { ActivationMethod = "ContextMenu" },
				owner,
				addInTreePath);
		}

		public static ContextMenu CreateContextMenu(UIElement inputBindingOwner, object owner, string addInTreePath)
		{
			return CreateContextMenu(
				new MenuCreateContext {
					InputBindingOwner = inputBindingOwner,
					ActivationMethod = "ContextMenu"
				},
				owner,
				addInTreePath);
		}

		static ContextMenu CreateContextMenu(MenuCreateContext context, object owner, string addInTreePath)
		{
			IList items = CreateUnexpandedMenuItems(
				context,
				AddInTree.BuildItems<MenuItemDescriptor>(addInTreePath, owner, false));
			return CreateContextMenu(items);
		}
	
		public static ContextMenu ShowContextMenu(UIElement parent, object owner, string addInTreePath)
		{
			ContextMenu menu = new ContextMenu();
			menu.ItemsSource = CreateMenuItems(menu, owner, addInTreePath, "ContextMenu");
			menu.PlacementTarget = parent;
			menu.IsOpen = true;
			return menu;
		}
		
		internal static ContextMenu CreateContextMenu(IList subItems)
		{
			var contextMenu = new ContextMenu() {
				ItemsSource = new object[1]
			};
			contextMenu.Opened += (sender, args) => {
				contextMenu.ItemsSource = ExpandMenuBuilders(subItems, true);
				args.Handled = true;
			};
			return contextMenu;
		}
		
		public static IList CreateMenuItems(UIElement inputBindingOwner, object owner, string addInTreePath, string activationMethod = null, bool immediatelyExpandMenuBuildersForShortcuts = false)
		{
			var descriptors = AddInTree.BuildItems<MenuItemDescriptor>(addInTreePath, owner, false);
			System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: path=" + addInTreePath + " descriptors.Count=" + (descriptors != null ? descriptors.Count.ToString() : "null") + "\n");
			IList items = CreateUnexpandedMenuItems(
				new MenuCreateContext {
					InputBindingOwner = inputBindingOwner,
					ActivationMethod = activationMethod,
					ImmediatelyExpandMenuBuildersForShortcuts =immediatelyExpandMenuBuildersForShortcuts
				},
				descriptors);
			var result = ExpandMenuBuilders(items, false);
			System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: result.Count=" + (result != null ? result.Count.ToString() : "null") + "\n");
			return result;
		}
		
		sealed class MenuItemBuilderPlaceholder
		{
			readonly IMenuItemBuilder builder;
			readonly Codon codon;
			readonly object caller;
			
			public MenuItemBuilderPlaceholder(IMenuItemBuilder builder, Codon codon, object caller)
			{
				this.builder = builder;
				this.codon = codon;
				this.caller = caller;
			}
			
			public IEnumerable<object> BuildItems()
			{
				return builder.BuildItems(codon, caller);
			}
		}
		
		internal static IList CreateUnexpandedMenuItems(MenuCreateContext context, IEnumerable descriptors)
		{
			ArrayList result = new ArrayList();
			if (descriptors != null) {
				foreach (MenuItemDescriptor descriptor in descriptors) {
					result.Add(CreateMenuItemFromDescriptor(context, descriptor));
				}
			}
			return result;
		}
		
		static IList ExpandMenuBuilders(ICollection input, bool addDummyEntryIfMenuEmpty)
		{
			List<object> result = new List<object>(input.Count);
			System.IO.File.AppendAllText("/tmp/opencode_menu.log", "ExpandMenuBuilders: input.Count=" + (input != null ? input.Count.ToString() : "null") + " addDummy=" + addDummyEntryIfMenuEmpty + "\n");
			foreach (object o in input) {
				MenuItemBuilderPlaceholder p = o as MenuItemBuilderPlaceholder;
				if (p != null) {
					try {
						IEnumerable<object> c = p.BuildItems();
						if (c != null)
							result.AddRange(c);
					} catch (Exception ex) {
						System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: ExpandMenuBuilders builder error: " + ex.ToString() + "\n");
					}
				} else {
					result.Add(o);
					IStatusUpdate statusUpdate = o as IStatusUpdate;
					if (statusUpdate != null) {
						try {
							statusUpdate.UpdateStatus();
							statusUpdate.UpdateText();
						} catch (Exception ex) {
							System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: ExpandMenuBuilders UpdateStatus error: " + ex.ToString() + "\n");
						}
					}
				}
			}
			System.IO.File.AppendAllText("/tmp/opencode_menu.log", "ExpandMenuBuilders: result.Count=" + result.Count + "\n");
			if (addDummyEntryIfMenuEmpty && result.Count == 0) {
				result.Add(new MenuItem { Header = "(empty menu)", IsEnabled = false });
			}
			return result;
		}

		static void ReplaceMenuItems(MenuItem menuItem, IList items)
		{
			menuItem.ItemsSource = null;
			menuItem.Items.Clear();
			foreach (object item in items) {
				menuItem.Items.Add(item);
			}
		}
		
		static object CreateMenuItemFromDescriptor(MenuCreateContext context, MenuItemDescriptor descriptor)
		{
			Codon codon = descriptor.Codon;
			string type = codon.Properties.Contains("type") ? codon.Properties["type"] : "Command";
			
			switch (type) {
				case "Separator":
					return new ConditionalSeparator(codon, descriptor.Parameter, false, descriptor.Conditions);
				case "CheckBox":
					return new MenuCheckBox(context.InputBindingOwner, codon, descriptor.Parameter, descriptor.Conditions);
				case "Item":
				case "Command":
					return new MenuCommand(context.InputBindingOwner, codon, descriptor.Parameter, context.ActivationMethod, descriptor.Conditions);
				case "Menu":
					System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: Creating Menu '" + (codon.Properties.Contains("label") ? codon.Properties["label"] : codon.Id) + "' subItems=" + (descriptor.SubItems != null ? descriptor.SubItems.OfType<object>().Count().ToString() : "null(Null)") + "\n");
					var item = new CoreMenuItem(codon, descriptor.Parameter, descriptor.Conditions) {
						SetEnabled = true
					};
					item.Items.Add(new MenuItem());
					var subItems = CreateUnexpandedMenuItems(context, descriptor.SubItems);
					System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: subItems.Count=" + (subItems != null ? subItems.Count.ToString() : "null") + "\n");
					item.SubmenuOpened += (sender, args) => {
						System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: SubmenuOpened for '" + (codon.Properties.Contains("label") ? codon.Properties["label"] : codon.Id) + "'\n");
						try {
							var expandedItems = ExpandMenuBuilders(subItems, true);
							System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: SubmenuOpened expandedItems.Count=" + (expandedItems != null ? expandedItems.Count.ToString() : "null") + "\n");
							ReplaceMenuItems(item, expandedItems);
							System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: After ReplaceMenuItems IsSubmenuOpen=" + item.IsSubmenuOpen + " Items.Count=" + item.Items.Count + " HasItems=" + item.HasItems + "\n");
							item.Dispatcher.BeginInvoke((Action)(() => {
								try {
									var popup = item.Template != null ? item.Template.FindName("PART_Popup", item) as Popup : null;
									if (popup != null) {
										popup.AllowsTransparency = false;
										popup.PlacementTarget = item;
										popup.Placement = ItemsControl.ItemsControlFromItemContainer(item) is Menu ? PlacementMode.Bottom : PlacementMode.Right;
										popup.IsOpen = true;
									}
									System.IO.File.AppendAllText("/tmp/opencode_menu.log",
										"MenuService: Deferred popup state IsSubmenuOpen=" + item.IsSubmenuOpen
										+ " Items.Count=" + item.Items.Count
										+ " PopupFound=" + (popup != null)
										+ (popup != null ? " Popup.IsOpen=" + popup.IsOpen + " Popup.AllowsTransparency=" + popup.AllowsTransparency + " Popup.Placement=" + popup.Placement + " Popup.Target=" + (popup.PlacementTarget != null ? popup.PlacementTarget.GetType().Name : "null") + " Popup.Child=" + (popup.Child != null ? popup.Child.GetType().Name : "null") : "")
										+ " Actual=" + item.ActualWidth + "x" + item.ActualHeight
										+ "\n");
								} catch (Exception ex) {
									System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: Deferred popup state error: " + ex + "\n");
								}
							}), System.Windows.Threading.DispatcherPriority.Background);
						} catch (Exception ex) {
							System.IO.File.AppendAllText("/tmp/opencode_menu.log", "MenuService: SubmenuOpened error: " + ex.ToString() + "\n");
						}
						args.Handled = true;
					};
					if (context.ImmediatelyExpandMenuBuildersForShortcuts)
						ExpandMenuBuilders(subItems, false);
					return item;
				case "Builder":
					IMenuItemBuilder builder = codon.AddIn.CreateObject(codon.Properties["class"]) as IMenuItemBuilder;
					if (builder == null)
						throw new NotSupportedException("Menu item builder " + codon.Properties["class"] + " does not implement IMenuItemBuilder");
					return new MenuItemBuilderPlaceholder(builder, descriptor.Codon, descriptor.Parameter);
				default:
					throw new NotSupportedException("unsupported menu item type : " + type);
			}
		}
		
		/// <summary>
		/// Converts from the Windows-Forms style label format (accessor key marked with '&amp;')
		/// to a WPF label format (accessor key marked with '_').
		/// </summary>
		public static string ConvertLabel(string label)
		{
			return label.Replace("_", "__").Replace("&", "_");
		}
		
		/// <summary>
		/// Creates an KeyGesture for a shortcut.
		/// </summary>
		public static KeyGesture ParseShortcut(string text)
		{
			return (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(text.Replace('|', '+'));
		}
		
		public static string GetDisplayStringForShortcut(KeyGesture kg)
		{
			// Note: the original implementation used Win32 P/Invoke (user32.dll ToUnicodeEx etc.)
			// via a WinForms.Keys bridge to render localized key-cap glyphs. That is WinForms/Win32
			// interop with no cross-platform meaning, so it has been stripped; we fall back to the
			// standard WPF-provided display string for the culture.
			return kg.GetDisplayStringForCulture(Thread.CurrentThread.CurrentUICulture);
		}
	}
}
