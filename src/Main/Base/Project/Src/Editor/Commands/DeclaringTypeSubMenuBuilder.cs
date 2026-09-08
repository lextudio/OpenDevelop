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

// The live-editor-caret path deliberately uses only language-service DTOs. In particular it must
// never put an ISymbol into /SharpDevelop/EntityContextMenu: that menu is built on the IDE side
// and therefore must work when Roslyn lives in Roslyn.Host. The IMemberModel path is untouched -
// that's SharpDevelop's separate background project-content model (bookmarks etc.), not part of
// the editor/host boundary.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	/// <summary>
	/// Builds context menu items with commands related to the declaring type of a member.
	/// </summary>
	public class DeclaringTypeSubMenuBuilder : IMenuItemBuilder
	{
		readonly struct CaretKey : IEquatable<CaretKey>
		{
			public CaretKey(string fileName, int offset, string version)
			{
				FileName = fileName;
				Offset = offset;
				Version = version;
			}

			public string FileName { get; }
			public int Offset { get; }
			public string Version { get; }
			public bool Equals(CaretKey other) => Offset == other.Offset
				&& string.Equals(FileName, other.FileName, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(Version, other.Version, StringComparison.Ordinal);
			public override bool Equals(object obj) => obj is CaretKey other && Equals(other);
			public override int GetHashCode() =>
				(StringComparer.OrdinalIgnoreCase.GetHashCode(FileName ?? string.Empty), Offset, Version ?? string.Empty).GetHashCode();
		}

		static readonly object cacheLock = new object();
		static readonly Dictionary<CaretKey, DeclaringTypeMenuContext> cache = new Dictionary<CaretKey, DeclaringTypeMenuContext>();
		static readonly HashSet<CaretKey> inFlight = new HashSet<CaretKey>();

		public IEnumerable<object> BuildItems(Codon codon, object parameter)
		{
			if (parameter is IMemberModel) {
				// Menu is directly created from a member model (e.g. bookmarks etc.)
				return BuildItemsForEntityModelMember((IMemberModel)parameter);
			}

			// This invocation is the recursive builder call made while creating the submenu. The DTO
			// still lets the condition evaluator decide type/member visibility, without re-entering us.
			if (parameter is DeclaringTypeMenuContext)
				return null;

			var editor = parameter as ITextEditor ?? SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || editor.FileName == null)
				return null;
			var key = new CaretKey(editor.FileName.ToString(), editor.Caret.Offset, editor.Document.Version?.ToString());
			DeclaringTypeMenuContext context;
			lock (cacheLock) {
				if (!cache.TryGetValue(key, out context)) {
					BeginPopulate(editor, key);
					return null;
				}
			}
			return BuildItemsForContext(context);
		}

		static void BeginPopulate(ITextEditor editor, CaretKey key)
		{
			if (!inFlight.Add(key))
				return;
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service)) {
				inFlight.Remove(key);
				return;
			}
			string text = editor.Document.Text;
			Task.Run(async () => {
				DeclaringTypeMenuContext result = null;
				try {
					var id = new DocumentId(key.FileName);
					await service.UpsertDocumentAsync(id, text, CancellationToken.None).ConfigureAwait(false);
					var kind = await service.GetSymbolKindAsync(id, key.Offset, CancellationToken.None).ConfigureAwait(false);
					var typeName = kind?.IsMember == true
						? await service.GetContainingTypeNameAsync(id, key.Offset, CancellationToken.None).ConfigureAwait(false)
						: null;
					if (!string.IsNullOrEmpty(typeName))
						result = new DeclaringTypeMenuContext(typeName, kind);
				} catch (Exception ex) {
					LoggingService.Debug("DeclaringTypeSubMenu: language-service query failed. " + ex.Message);
				} finally {
					lock (cacheLock) {
						inFlight.Remove(key);
						if (result != null) {
							if (cache.Count >= 64) cache.Clear();
							cache[key] = result;
						}
					}
				}
			});
		}

		IEnumerable<object> BuildItemsForContext(DeclaringTypeMenuContext context)
		{
			var declaringTypeItem = new MenuItem {
				Header = SD.ResourceService.GetString("SharpDevelop.Refactoring.DeclaringType") + ": " + context.Name,
				Icon = SD.ResourceService.GetImage("Icons.16x16.Class").CreateImage()
			};
			var subItems = MenuService.CreateMenuItems(null, context, "/SharpDevelop/EntityContextMenu");
			if (subItems != null)
				foreach (var item in subItems)
					declaringTypeItem.Items.Add(item);
			return new object[] { declaringTypeItem };
		}

		IEnumerable<object> BuildItemsForEntityModelMember(IMemberModel memberModel)
		{
			IMember member = memberModel.Resolve();
			ITypeDefinition declaringType = member != null ? member.DeclaringTypeDefinition : null;
			if (declaringType == null)
				return null;

			var items = new List<object>();
			var declaringTypeItem = new MenuItem {
				Header = SD.ResourceService.GetString("SharpDevelop.Refactoring.DeclaringType") + ": " + declaringType.Name,
				Icon = new Image { Source = ClassBrowserIconService.GetIcon(declaringType).ImageSource }
			};

			var subItems = MenuService.CreateMenuItems(null, declaringType, "/SharpDevelop/EntityContextMenu");
			if (subItems != null) {
				foreach (var item in subItems) {
					declaringTypeItem.Items.Add(item);
				}
			}
			items.Add(declaringTypeItem);

			return items;
		}
	}

	/// <summary>
	/// Serializable-in-spirit menu state: no backend object or Roslyn symbol crosses into the UI.
	/// The commands in EntityContextMenu resolve the active caret through ILanguageService when run.
	/// </summary>
	public sealed class DeclaringTypeMenuContext
	{
		public DeclaringTypeMenuContext(string name, SymbolKindInfo symbolKind)
		{
			Name = name;
			SymbolKind = symbolKind;
		}
		public string Name { get; }
		public SymbolKindInfo SymbolKind { get; }
	}
}
