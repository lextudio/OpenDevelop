using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ComponentModel.Design;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vb = Microsoft.CodeAnalysis.VisualBasic;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.Reflection;
using System.Runtime.Loader;

namespace ICSharpCode.FormsDesigner.Host;

sealed class DesignerHostService : IDesignerChildService
{
	const int ProtocolVersion = 2;
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	string? sessionId;
	DesignerDocumentSnapshot? current;
	DesignSurface? designSurface;
	ProjectAssemblyLoadContext? projectLoadContext;
	Assembly? projectAssembly;
	readonly List<Assembly> referencedAssemblies = new();
	Size? rootDesignSize;
	SizeF? rootAutoScaleDimensions;
	long frameSequence;
	bool initialized;
	static volatile bool portableGpuReadbackUnavailable;
	static readonly bool traceSessionOpen = String.Equals(Environment.GetEnvironmentVariable("OPENDEVELOP_DESIGNER_TRACE"), "1", StringComparison.Ordinal);

	public DesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	bool IsVisualBasic => current?.Language.Equals("VisualBasic", StringComparison.OrdinalIgnoreCase) == true
		|| current?.DesignerFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true
		|| current?.PrimaryFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true;

	bool IsValidIdentifier(string name) => IsVisualBasic
		? Vb.SyntaxFacts.IsValidIdentifier(name)
		: SyntaxFacts.IsValidIdentifier(name);

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		initialized = true;
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = ProtocolVersion, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot)
	{
		Trace("session/open received");
		EnsureInitialized();
		EnsureOwnSession(snapshot);
		Trace("session/open validated envelope");
		Validate(snapshot);
		Trace("session/open validated snapshot");
		CreateDesignSurface(snapshot);
		Trace("session/open created design surface");
		current = snapshot;
		return CurrentState(snapshot.Version);
	}

	static void Trace(string message)
	{
		if (traceSessionOpen)
			Console.Error.WriteLine($"FormsDesigner.Host: {message}");
	}

	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot)
	{
		EnsureInitialized();
		EnsureOwnSession(snapshot);
		Validate(snapshot);
		if (current is null)
			return Rejected(snapshot.Version, "No designer session is open.");
		if (snapshot.Version <= current.Version)
			return Rejected(snapshot.Version, $"Stale document baseVersion {snapshot.Version}; current baseVersion is {current.Version}.");
		CreateDesignSurface(snapshot);
		current = snapshot;
		return CurrentState(snapshot.Version);
	}

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
	{
		EnsureInitialized();
		EnsureOwnSession(sessionId, documentId);
		if (current is null || current.Version != baseVersion)
			throw new InvalidOperationException("Cannot flush a stale or unopened document baseVersion.");
		foreach (var file in current.Files.Where(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))) {
			if (IsVisualBasic) {
				var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
				file.Text = new MeQualifierRewriter().Visit(vbRoot)!.ToFullString();
			} else {
				var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
				file.Text = new ThisQualifierRewriter().Visit(root)!.ToFullString();
			}
		}
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = baseVersion, Files = current.Files };
	}

	/// <summary>Pushes the parent's selection into the child's REAL ISelectionService and returns a
	/// freshly rendered state.
	///
	/// This is what activates the genuine design-time chrome instead of us redrawing it: the real
	/// designers are all selection-driven. ToolStripDesigner keeps its template node
	/// (`_editorNode`, a DesignerToolStripControlHost it already appended to ToolStrip.Items) at
	/// Visible=false until its strip is selected; ToolStripMenuItemDesigner, on selection, calls
	/// CreatetypeHereNode(), sets `MenuItem.DropDown.TopLevel = false` and `AutoClose = false`,
	/// then ShowDropDown() - so the expanded dropdown becomes a CHILD CONTROL of the form (not a
	/// floating window) and therefore lands in Form.DrawToBitmap's output, together with that
	/// level's own "Type Here" node. Until this RPC existed the child's selection service was
	/// never told anything, so none of that chrome ever appeared.</summary>
	[JsonRpcMethod("design/set-selection")]
	public DesignerSessionState SetSelection(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "select in");
		var host = GetHost();
		if (designSurface?.GetService(typeof(ISelectionService)) is ISelectionService selection) {
			var components = (elementIds ?? [])
				.Select(id => host.Container.Components[id])
				.Where(component => component != null)
				.ToArray();
			selection.SetSelectedComponents(components, SelectionTypes.Replace);
		}
#if MICROSOFT_WINFORMS
		// The chrome the selection just triggered is created asynchronously by the designers:
		// ToolStripMenuItemDesigner's ShowDropDown()/PerformLayout() and the template node's
		// visibility change only settle once the pending messages are pumped. Rendering in the
		// same call without this captured the frame BEFORE the expanded dropdown and the "Type
		// Here" node had laid out, so their geometry was already reported correctly while their
		// pixels were missing from the bitmap.
		Application.DoEvents();
		(GetHost().RootComponent as Control)?.PerformLayout();
		Application.DoEvents();
#endif
		return CurrentState(baseVersion);
	}

	/// <summary>Hit-tests a point INSIDE one popup overlay's own local coordinate space (see
	/// DesignerSessionState.Popups) and, if it lands on an item, selects that item through the
	/// real ISelectionService - the same "let the real designer react" approach
	/// <see cref="SetSelection"/> uses. A popup's dropdown is not reachable from the root form's
	/// own Controls tree (it is parented into the designer's adorner window), so the ordinary
	/// <see cref="HitTest"/>/root-relative path cannot be reused here; the owner element id tells
	/// us which live ToolStripDropDown to test against instead of walking down from the root.</summary>
	[JsonRpcMethod("design/hit-test-popup")]
	public DesignerSessionState HitTestPopupAndSelect(string sessionId, string documentId, long baseVersion, string ownerElementId, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "select in");
#if MICROSOFT_WINFORMS
		var host = GetHost();
		var rootControl = host.RootComponent as Control;
		var dropDown = rootControl == null ? null
			: ExpandedDropDowns(rootControl).FirstOrDefault(entry => entry.OwnerElementId == ownerElementId).DropDown
			// Not an owning ToolStripDropDownItem's submenu - ownerElementId may instead name a
			// tray-only ContextMenuStrip's own overlay directly (see SelectedContextMenuStripPopups).
			?? host.Container.Components[ownerElementId] as ToolStripDropDown;
		IComponent? hit = null;
		if (dropDown != null && designSurface?.GetService(typeof(ISelectionService)) is ISelectionService selection) {
			// ToolStripDropDown IS-A ToolStrip, so the existing root hit-test walk already knows
			// how to test its Items - it just needed a dropdown instead of the form to start from.
			hit = FindDeepest(dropDown, new Point(x, y), host.Container);
			if (hit != null)
				selection.SetSelectedComponents(new IComponent[] { hit }, SelectionTypes.Replace);
		}
		var state = CurrentState(baseVersion);
		state.PopupHitElementId = hit?.Site?.Name;
		return state;
#else
		return CurrentState(baseVersion);
#endif
	}

	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "hit-test");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var root = host?.RootComponent as Control;
		// x/y arrive in surface (rendered bitmap) space; Control/ToolStripItem bounds are
		// client-space, so strip the root form's non-client offset first.
		var offset = RootClientOffset(root);
		var hit = root == null ? null
			: FindDeepest(root, new Point(x - offset.X, y - offset.Y), host?.Container)
				// A press on the caption/border falls outside the client area entirely; treat it
				// as selecting the form itself rather than clearing the selection.
				?? (x >= 0 && y >= 0 && x < root.Width && y < root.Height ? root : null);
		return new DesignerHitTestResult {
			ComponentName = hit?.Site?.Name ?? "",
			ComponentType = hit?.GetType().FullName ?? ""
		};
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		var component = host.Container.Components.Cast<IComponent>()
			.FirstOrDefault(item => String.Equals(item.Site?.Name, elementId, StringComparison.Ordinal))
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var property = TypeDescriptor.GetProperties(component)[propertyName]
			?? throw new ArgumentException("Property not found: " + propertyName, nameof(propertyName));
		if (property.IsReadOnly)
			throw new InvalidOperationException($"Property {elementId}.{propertyName} is read-only.");
		var converted = ConvertPropertyValue(property, value);
		if (component == host.RootComponent && propertyName == "AutoScaleDimensions" && converted is SizeF scale)
			rootAutoScaleDimensions = scale;
		// Validate source serialization before mutating the live component. A
		// failed complex-property serializer must not split live and source state.
		_ = SerializeValue(converted);
		using (var transaction = host.CreateTransaction($"Set {elementId}.{propertyName}")) {
			property.SetValue(component, converted);
			transaction.Commit();
		}
		RewriteProperty(elementId, propertyName, converted);
		return CurrentState(baseVersion);
	}

	static object ConvertPropertyValue(PropertyDescriptor property, string value)
	{
		if (property.PropertyType == typeof(Padding)) {
			var parts = value.Split(',').Select(item => Int32.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray();
			if (parts.Length == 1) return new Padding(parts[0]);
			if (parts.Length == 4) return new Padding(parts[0], parts[1], parts[2], parts[3]);
			throw new FormatException("Padding requires one or four comma-separated integers.");
		}
		if (property.PropertyType == typeof(Font)) {
			var parts = value.Split(',').Select(item => item.Trim()).ToArray();
			if (parts.Length < 2) throw new FormatException("Font requires 'family, size[, style]'.");
			var style = parts.Length > 2 ? Enum.Parse<FontStyle>(parts[2], true) : FontStyle.Regular;
			return new Font(parts[0], Single.Parse(parts[1], CultureInfo.InvariantCulture), style);
		}
		if (property.PropertyType == typeof(SizeF)) {
			var parts = value.Split(',').Select(item => Single.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray();
			if (parts.Length != 2) throw new FormatException("SizeF requires two comma-separated numbers.");
			return new SizeF(parts[0], parts[1]);
		}
		return property.Converter.ConvertFromInvariantString(value)
			?? throw new InvalidOperationException($"Cannot convert '{value}' to {property.PropertyType.FullName}.");
	}

	[JsonRpcMethod("design/reset-property")]
	public DesignerSessionState ResetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "reset property on");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var property = TypeDescriptor.GetProperties(component)[propertyName]
			?? throw new ArgumentException("Property not found: " + propertyName, nameof(propertyName));
		// LibreWinForms currently returns false from CanResetValue for some properties
		// (for example Enabled) even after they have been explicitly serialized.
		if (property.IsReadOnly || (!property.CanResetValue(component) && !property.ShouldSerializeValue(component)))
			throw new InvalidOperationException($"Property {elementId}.{propertyName} cannot be reset.");
		using (var transaction = host.CreateTransaction($"Reset {elementId}.{propertyName}")) {
			property.ResetValue(component);
			// Some LibreWinForms descriptors expose DefaultValueAttribute but their
			// ResetValue implementation is currently a no-op.
			if (property.ShouldSerializeValue(component)
				&& property.Attributes[typeof(DefaultValueAttribute)] is DefaultValueAttribute defaultValue)
				property.SetValue(component, defaultValue.Value);
			if (property.ShouldSerializeValue(component) && TryGetDefaultPropertyValue(component, property, out var freshDefault))
				property.SetValue(component, freshDefault);
			transaction.Commit();
		}
		RewriteResetProperty(elementId, propertyName);
		return CurrentState(baseVersion);
	}

	static bool TryGetDefaultPropertyValue(IComponent component, PropertyDescriptor property, out object? value)
	{
		value = null;
		object? fresh = null;
		try {
			fresh = Activator.CreateInstance(component.GetType());
			if (fresh == null) return false;
			var freshProperty = TypeDescriptor.GetProperties(fresh)[property.Name];
			if (freshProperty == null) return false;
			value = freshProperty.GetValue(fresh);
			return true;
		} catch {
			return false;
		} finally {
			(fresh as IDisposable)?.Dispose();
		}
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState RenameComponent(string sessionId, string documentId, long baseVersion, string elementId, string newName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "rename");
		if (!IsValidIdentifier(newName))
			throw new ArgumentException("A valid component name is required.", nameof(newName));
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		if (component == host.RootComponent) throw new InvalidOperationException("The root component cannot be renamed here.");
		if (host.Container.Components[newName] != null) throw new ArgumentException("A component with that name already exists: " + newName, nameof(newName));
		RewriteComponentName(elementId, newName);
		// Control.Name and ToolStripItem.Name are both real, separately-serialized design-time
		// properties (a "foo.Name = "oldName";" statement, distinct from the field/identifier
		// RewriteComponentName above already renamed) - not just Control's. Missing the
		// ToolStripItem case left its Name property statement holding the stale OLD name as a
		// string literal after every identifier reference had already moved to the new one.
		if (component is Control or ToolStripItem) RewriteProperty(newName, "Name", newName);
		CreateDesignSurface(current!);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!String.IsNullOrEmpty(handlerName) && !IsValidIdentifier(handlerName))
			throw new ArgumentException("A valid event handler name is required.", nameof(handlerName));
		var component = GetHost().Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var descriptor = TypeDescriptor.GetEvents(component)[eventName]
			?? throw new ArgumentException("Event not found: " + eventName, nameof(eventName));
		RewriteEvent(elementId, descriptor, handlerName);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/activate-default-event")]
	public DesignerSessionState ActivateDefaultEvent(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "activate the default event on");
		var component = GetHost().Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var attribute = TypeDescriptor.GetAttributes(component)[typeof(DefaultEventAttribute)] as DefaultEventAttribute;
		var eventName = attribute?.Name;
		// LibreWinForms does not yet expose the framework DefaultEventAttribute on
		// several standard controls. Preserve the established WinForms behavior.
		if (String.IsNullOrEmpty(eventName))
			eventName = component is Form ? "Load" : "Click";
		var descriptor = String.IsNullOrEmpty(eventName) ? null : TypeDescriptor.GetEvents(component)[eventName];
		if (descriptor == null)
			throw new InvalidOperationException($"Component {elementId} has no default event.");
		var existing = DescribeEvents(component).FirstOrDefault(item => item.Name == descriptor.Name)?.Handler;
		RewriteEvent(elementId, descriptor, String.IsNullOrEmpty(existing) ? elementId + "_" + descriptor.Name : existing);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddControl(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string elementId, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!IsValidIdentifier(elementId))
			throw new ArgumentException("A valid component name is required.", nameof(elementId));
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		if (host.Container.Components[elementId] != null)
			throw new ArgumentException("A component with that name already exists: " + elementId, nameof(elementId));
		var parent = host.Container.Components[parentId] as Control
			?? throw new ArgumentException("Parent control not found: " + parentId, nameof(parentId));
		var type = ResolveControlType(item?.TypeName);
		Control control;
		using (var transaction = host.CreateTransaction("Add " + elementId)) {
			control = (Control)host.CreateComponent(type, elementId);
			if (control.Width <= 0 || control.Height <= 0)
				control.Size = type == typeof(NumericUpDown) ? new Size(120, 20) : new Size(75, 23);
			control.Location = new Point(x, y);
			parent.Controls.Add(control);
			transaction.Commit();
		}
		RewriteAddedControl(parentId, type, elementId, x, y, control.Width, control.Height);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/set-bounds")]
	public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, int x, int y, int width, int height)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Control bounds must be positive.");
		var host = GetHost();
		var control = host.Container.Components[elementId] as Control
			?? throw new ArgumentException("Control not found: " + elementId, nameof(elementId));
		if (control == host.RootComponent) {
			RewriteRootSize(width, height);
			CreateDesignSurface(current!);
			return CurrentState(baseVersion);
		}
		using (var transaction = host.CreateTransaction("Set bounds " + elementId)) {
			control.Bounds = new Rectangle(x, y, width, height);
			transaction.Commit();
		}
		RewriteBounds(elementId, x, y, width, height);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteComponent(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		if (component == host.RootComponent) throw new InvalidOperationException("The root component cannot be deleted.");
		using (var transaction = host.CreateTransaction("Delete " + elementId)) {
			host.DestroyComponent(component);
			transaction.Commit();
		}
		RewriteDeletedComponent(elementId);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/set-z-order")]
	public DesignerSessionState SetZOrder(string sessionId, string documentId, long baseVersion, string elementId, bool bringToFront)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "change z-order for");
		var host = GetHost();
		var control = host.Container.Components[elementId] as Control
			?? throw new ArgumentException("Control not found: " + elementId, nameof(elementId));
		if (control == host.RootComponent || control.Parent == null)
			throw new InvalidOperationException("The root component z-order cannot be changed.");
		using (var transaction = host.CreateTransaction((bringToFront ? "Bring to front " : "Send to back ") + elementId)) {
			control.Parent.Controls.SetChildIndex(control, bringToFront ? 0 : control.Parent.Controls.Count - 1);
			transaction.Commit();
		}
		RewriteZOrder(control.Parent.Site?.Name ?? "", elementId, bringToFront ? 0 : control.Parent.Controls.Count - 1);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/apply-layout")]
	public DesignerSessionState ApplyLayout(string sessionId, string documentId, long baseVersion, string operation, string[] elementIds, int deltaX, int deltaY)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "apply layout to");
		var host = GetHost();
		var controls = elementIds.Distinct(StringComparer.Ordinal)
			.Select(name => host.Container.Components[name] as Control
				?? throw new ArgumentException("Control not found: " + name, nameof(elementIds)))
			.Where(control => control != host.RootComponent).ToArray();
		if (controls.Length == 0) return CurrentState(baseVersion);
		if (controls.Select(control => control.Parent).Distinct().Count() != 1)
			throw new InvalidOperationException("Layout commands require controls with the same parent.");
		var primary = controls[0];
		using (var transaction = host.CreateTransaction("Remote layout " + operation)) {
				switch (operation) {
				case "move": foreach (var item in controls) item.Location = new Point(Math.Max(0, item.Left + deltaX), Math.Max(0, item.Top + deltaY)); break;
				case "align-left": foreach (var item in controls.Skip(1)) item.Left = primary.Left; break;
				case "align-right": foreach (var item in controls.Skip(1)) item.Left = primary.Right - item.Width; break;
				case "align-top": foreach (var item in controls.Skip(1)) item.Top = primary.Top; break;
				case "align-bottom": foreach (var item in controls.Skip(1)) item.Top = primary.Bottom - item.Height; break;
				case "align-horizontal-centers": foreach (var item in controls.Skip(1)) item.Left = primary.Left + (primary.Width - item.Width) / 2; break;
				case "align-vertical-centers": foreach (var item in controls.Skip(1)) item.Top = primary.Top + (primary.Height - item.Height) / 2; break;
				case "same-size": foreach (var item in controls.Skip(1)) item.Size = primary.Size; break;
				case "same-width": foreach (var item in controls.Skip(1)) item.Width = primary.Width; break;
				case "same-height": foreach (var item in controls.Skip(1)) item.Height = primary.Height; break;
				case "center-horizontal": foreach (var item in controls) item.Left = Math.Max(0, (item.Parent!.ClientSize.Width - item.Width) / 2); break;
				case "center-vertical": foreach (var item in controls) item.Top = Math.Max(0, (item.Parent!.ClientSize.Height - item.Height) / 2); break;
				case "snap-grid": foreach (var item in controls) item.Bounds = new Rectangle(Snap(item.Left), Snap(item.Top), Math.Max(8, Snap(item.Width)), Math.Max(8, Snap(item.Height))); break;
				case "horizontal-space-equal": SpaceEqually(controls, true); break;
				case "vertical-space-equal": SpaceEqually(controls, false); break;
				case "horizontal-space-increase": AdjustSpacing(controls, true, 8, false); break;
				case "horizontal-space-decrease": AdjustSpacing(controls, true, -8, false); break;
				case "horizontal-space-concatenate": AdjustSpacing(controls, true, 0, true); break;
				case "vertical-space-increase": AdjustSpacing(controls, false, 8, false); break;
				case "vertical-space-decrease": AdjustSpacing(controls, false, -8, false); break;
				case "vertical-space-concatenate": AdjustSpacing(controls, false, 0, true); break;
				default: throw new NotSupportedException("Unsupported remote layout operation: " + operation);
			}
			transaction.Commit();
		}
		foreach (var control in controls)
			RewriteBounds(control.Site!.Name!, control.Left, control.Top, control.Width, control.Height);
		return CurrentState(baseVersion);
	}

#if MICROSOFT_WINFORMS
	/// <summary>Generic smart-tag listing: works for any component with registered
	/// DesignerActionLists (VS calls this the "smart tag" - the chevron button at a selected
	/// component's top-right). LibreWinForms's portable fork has no
	/// System.ComponentModel.Design.DesignerActionService support, so this whole feature is
	/// Microsoft-backend only.</summary>
	[JsonRpcMethod("design/list-smart-tag-actions")]
	public DesignerSmartTagActions ListSmartTagActions(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "list smart tag actions for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var lists = GetActionLists(host, component);
		var items = new List<DesignerSmartTagActionInfo>();
		if (lists != null) {
			for (var listIndex = 0; listIndex < lists.Count; listIndex++) {
				var list = lists[listIndex];
				var sorted = list.GetSortedActionItems();
				for (var itemIndex = 0; itemIndex < sorted.Count; itemIndex++)
					items.Add(DescribeSmartTagItem(list, sorted[itemIndex], listIndex, itemIndex, elementId));
			}
		}
		return new DesignerSmartTagActions { Accepted = true, Items = items };
	}

	/// <summary>Smart-tag lists come from the component's own <c>ComponentDesigner.ActionLists</c>
	/// (e.g. <c>ToolStripDesigner</c> overrides it to return <c>ToolStripActionList</c>) rather
	/// than <c>DesignerActionService.GetComponentActions</c>: the latter only returns anything for
	/// a component whose designer registered with the service during its own
	/// <c>Initialize()</c>, which requires <c>DesignerActionService</c> to already be present in
	/// the container at that moment - something only the VS shell's own designer loader sets up,
	/// not the bare <see cref="DesignSurface"/> this host constructs. Reading <c>ActionLists</c>
	/// directly needs no such service.</summary>
	static DesignerActionListCollection? GetActionLists(IDesignerHost host, IComponent component)
		=> (host.GetDesigner(component) as ComponentDesigner)?.ActionLists;

	static DesignerSmartTagActionInfo DescribeSmartTagItem(DesignerActionList list, DesignerActionItem item, int listIndex, int itemIndex, string ownerElementId)
	{
		var info = new DesignerSmartTagActionInfo {
			ListIndex = listIndex,
			ItemIndex = itemIndex,
			DisplayName = item.DisplayName ?? "",
			Description = item.Description ?? "",
			Category = item.Category ?? "",
		};
		switch (item) {
		case DesignerActionMethodItem methodItem:
			info.Kind = "Method";
			info.MemberName = methodItem.MemberName ?? "";
			break;
		case DesignerActionPropertyItem propertyItem: {
			info.Kind = "Property";
			info.MemberName = propertyItem.MemberName ?? "";
			info.PropertyOwnerElementId = ownerElementId;
			var property = TypeDescriptor.GetProperties(list)[propertyItem.MemberName];
			if (property != null) {
				info.TypeName = property.PropertyType.FullName ?? property.PropertyType.Name;
				info.IsEnum = property.PropertyType.IsEnum;
				if (info.IsEnum) info.AllowedValues.AddRange(Enum.GetNames(property.PropertyType));
				else if (property.PropertyType == typeof(bool)) info.AllowedValues.AddRange(["True", "False"]);
				try {
					var value = property.GetValue(list);
					info.Value = value == null ? "" : (property.Converter.ConvertToInvariantString(value) ?? "");
				} catch { info.Value = ""; }
			}
			break;
		}
		// DesignerActionHeaderItem derives from DesignerActionTextItem - check it first.
		case DesignerActionHeaderItem: info.Kind = "Header"; break;
		case DesignerActionTextItem: info.Kind = "Text"; break;
		default: info.Kind = "Text"; break;
		}
		return info;
	}

	/// <summary>Invokes a <c>DesignerActionMethodItem</c> found by re-fetching the same smart
	/// tag list (never cached between calls - the live <c>DesignerActionList</c> is not a
	/// serializable DDP object). Many such methods (e.g. ToolStripActionList's "Insert Standard
	/// Items") mutate the component's runtime child collection directly rather than through a
	/// property this host already knows how to serialize, so - unlike every other mutation RPC
	/// in this file - this one does not attempt to rewrite the designer source; the caller sees
	/// the new state immediately via the returned <see cref="DesignerSessionState"/>, but a
	/// subsequent Flush will not yet emit source for whatever the method added. Persisting
	/// arbitrary smart-tag method side effects to source is a follow-up, not attempted here.</summary>
	[JsonRpcMethod("design/invoke-smart-tag-method")]
	public DesignerSessionState InvokeSmartTagMethod(string sessionId, string documentId, long baseVersion, string elementId, int listIndex, int itemIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "invoke smart tag method for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var lists = GetActionLists(host, component);
		if (lists == null || listIndex < 0 || listIndex >= lists.Count)
			throw new ArgumentException("Smart tag action list not found.", nameof(listIndex));
		var sorted = lists[listIndex].GetSortedActionItems();
		if (itemIndex < 0 || itemIndex >= sorted.Count || sorted[itemIndex] is not DesignerActionMethodItem methodItem)
			throw new ArgumentException("Smart tag method item not found.", nameof(itemIndex));
		using (var transaction = host.CreateTransaction("Invoke " + methodItem.DisplayName)) {
			methodItem.Invoke();
			transaction.Commit();
		}
		return CurrentState(baseVersion);
	}

	/// <summary>The ToolStrip/StatusStrip/MenuStrip "insert new item" chevron: creates a real
	/// sited ToolStripItem via <c>host.CreateComponent</c> (so it is indistinguishable from a
	/// hand-authored item, unlike the unsited scaffolding <c>BuildElementTree</c> already filters
	/// out) and appends it to the strip's own Items, or to a submenu's DropDownItems when
	/// <paramref name="parentItemId"/> names a drop-down item.</summary>
	[JsonRpcMethod("design/add-toolstrip-item")]
	public DesignerSessionState AddToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, string itemTypeName, string parentItemId, string newItemId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!IsValidIdentifier(newItemId))
			throw new ArgumentException("A valid component name is required.", nameof(newItemId));
		var host = GetHost();
		if (host.Container.Components[newItemId] != null)
			throw new ArgumentException("A component with that name already exists: " + newItemId, nameof(newItemId));
		var strip = host.Container.Components[elementId] as ToolStrip
			?? throw new ArgumentException("ToolStrip not found: " + elementId, nameof(elementId));
		var parentDropDown = String.IsNullOrEmpty(parentItemId) ? null
			: host.Container.Components[parentItemId] as ToolStripDropDownItem
				?? throw new ArgumentException("Parent menu item not found: " + parentItemId, nameof(parentItemId));
		var type = ResolveToolStripItemType(itemTypeName);
		ToolStripItem item;
		using (var transaction = host.CreateTransaction("Add " + newItemId)) {
			item = (ToolStripItem)host.CreateComponent(type, newItemId);
			var items = parentDropDown != null ? parentDropDown.DropDownItems : strip.Items;
			items.Add(item);
			transaction.Commit();
		}
		RewriteAddedToolStripItem(elementId, parentItemId, type, newItemId);
		return CurrentState(baseVersion);
	}

	static Type ResolveToolStripItemType(string name) => ResolveToolStripItemTypeShortName(ShortTypeName(name));

	static string ShortTypeName(string name)
	{
		var lastDot = name.LastIndexOf('.');
		return lastDot < 0 ? name : name.Substring(lastDot + 1);
	}

	static Type ResolveToolStripItemTypeShortName(string name) => name switch {
		"Button" or "ToolStripButton" => typeof(ToolStripButton),
		"Label" or "ToolStripLabel" => typeof(ToolStripLabel),
		"SplitButton" or "ToolStripSplitButton" => typeof(ToolStripSplitButton),
		"DropDownButton" or "ToolStripDropDownButton" => typeof(ToolStripDropDownButton),
		"Separator" or "ToolStripSeparator" => typeof(ToolStripSeparator),
		"ComboBox" or "ToolStripComboBox" => typeof(ToolStripComboBox),
		"TextBox" or "ToolStripTextBox" => typeof(ToolStripTextBox),
		"ProgressBar" or "ToolStripProgressBar" => typeof(ToolStripProgressBar),
		"StatusLabel" or "ToolStripStatusLabel" => typeof(ToolStripStatusLabel),
		"MenuItem" or "ToolStripMenuItem" => typeof(ToolStripMenuItem),
		_ => throw new NotSupportedException("Unsupported ToolStripItem type: " + name)
	};

	void RewriteAddedToolStripItem(string stripId, string parentItemId, Type type, string elementId)
	{
		if (IsVisualBasic) { RewriteAddedToolStripItemVisualBasic(stripId, parentItemId, type, elementId); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		var collectionExpression = String.IsNullOrEmpty(parentItemId)
			? $"this.{stripId}.Items" : $"this.{parentItemId}.DropDownItems";
		var statements = new[] {
			SyntaxFactory.ParseStatement($"this.{elementId} = new {type.FullName}();\n"),
			SyntaxFactory.ParseStatement($"{collectionExpression}.Add(this.{elementId});\n")
		};
		var updatedMethod = method.WithBody(method.Body!.AddStatements(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(item => item.Identifier.ValueText == className);
		var field = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private {type.FullName} {elementId};\n")!;
		root = root.ReplaceNode(declaration, declaration.AddMembers(field));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteAddedToolStripItemVisualBasic(string stripId, string parentItemId, Type type, string elementId)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
		var collectionExpression = String.IsNullOrEmpty(parentItemId)
			? $"Me.{stripId}.Items" : $"Me.{parentItemId}.DropDownItems";
		var statements = new[] {
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId} = New {type.FullName}()"),
			Vb.SyntaxFactory.ParseExecutableStatement($"{collectionExpression}.Add(Me.{elementId})")
		};
		var updatedMethod = method.WithStatements(method.Statements.AddRange(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().First(item => item.BlockStatement.Identifier.ValueText == className);
		var field = ParseMemberField($"Friend WithEvents {elementId} As {type.FullName}");
		root = root.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(field)));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}
#else
	/// <summary>LibreWinForms has no System.ComponentModel.Design.DesignerActionService support
	/// (verified: its portable fork does not implement the smart-tag/action-list design-time
	/// services at all, only the base TypeDescriptor property/event model this file already uses
	/// elsewhere), so the smart-tag and ToolStrip-item-insertion features are Microsoft-backend
	/// only. Fail clearly rather than silently no-op.</summary>
	[JsonRpcMethod("design/list-smart-tag-actions")]
	public DesignerSmartTagActions ListSmartTagActions(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "list smart tag actions for");
		return new DesignerSmartTagActions { Accepted = false, Error = "Smart tag actions are only supported by the Microsoft WinForms designer host." };
	}

	[JsonRpcMethod("design/invoke-smart-tag-method")]
	public DesignerSessionState InvokeSmartTagMethod(string sessionId, string documentId, long baseVersion, string elementId, int listIndex, int itemIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "invoke smart tag method for");
		throw new NotSupportedException("Smart tag actions are only supported by the Microsoft WinForms designer host.");
	}

	[JsonRpcMethod("design/add-toolstrip-item")]
	public DesignerSessionState AddToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, string itemTypeName, string parentItemId, string newItemId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		throw new NotSupportedException("ToolStrip item insertion is only supported by the Microsoft WinForms designer host.");
	}
#endif

#if MICROSOFT_WINFORMS
	/// <summary>Drag-to-reorder for a ToolStrip/StatusStrip/MenuStrip item: moves it to
	/// <paramref name="targetIndex"/> within whatever collection it is CURRENTLY in (the strip's
	/// own Items, or a submenu's DropDownItems - resolved from the item's own Owner/OwnerItem, the
	/// same way <see cref="ExpandedDropDowns"/>/<see cref="BelongsTo"/> do; this RPC never moves an
	/// item to a DIFFERENT collection than the one it started in). Gated to Microsoft only, like
	/// AddToolStripItem: unlike that RPC this needs no DesignerActionService/CreateComponent
	/// machinery, but LibreWinForms' ToolStripItem does not expose Owner/OwnerItem at all.</summary>
	[JsonRpcMethod("design/reorder-toolstrip-item")]
	public DesignerSessionState ReorderToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, int targetIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = GetHost();
		var item = host.Container.Components[elementId] as ToolStripItem
			?? throw new ArgumentException("ToolStripItem not found: " + elementId, nameof(elementId));
		var ownerItem = item.OwnerItem as ToolStripDropDownItem;
		var collection = ownerItem != null ? ownerItem.DropDownItems : item.Owner?.Items
			?? throw new ArgumentException("Item has no owning collection: " + elementId, nameof(elementId));
		using (var transaction = host.CreateTransaction("Reorder " + elementId)) {
			collection.Remove(item);
			var clampedIndex = Math.Clamp(targetIndex, 0, collection.Count);
			collection.Insert(clampedIndex, item);
			transaction.Commit();
		}
		var stripId = (ownerItem != null ? (ownerItem.Owner ?? item.Owner) : item.Owner)?.Site?.Name ?? "";
		RewriteReorderedToolStripItems(stripId, ownerItem?.Site?.Name ?? "", collection);
		return CurrentState(baseVersion);
	}

	/// <summary>Reorders the designer source's own record of a strip's items to match the LIVE
	/// collection's new order, after <see cref="ReorderToolStripItem"/> has already reordered the
	/// real ToolStripItemCollection. Handles both shapes existing fixtures/tests use: a single
	/// "collection.AddRange(new T[] { a, b, c })" call (its array elements are reordered in place)
	/// and a sequence of separate "collection.Add(x)" statements (the STATEMENTS are reordered in
	/// place, at the positions the original ones occupied) - anything else (an item that was only
	/// ever added at runtime, never textually) is left untouched, since there is nothing to move.</summary>
	void RewriteReorderedToolStripItems(string stripId, string parentItemId, ToolStripItemCollection collection)
	{
		if (String.IsNullOrEmpty(stripId)) return;
		var orderedNames = collection.Cast<ToolStripItem>().Select(i => i.Site?.Name ?? "").Where(n => n.Length > 0).ToList();
		if (orderedNames.Count < 2) return;
		if (IsVisualBasic) { RewriteReorderedToolStripItemsVisualBasic(stripId, parentItemId, orderedNames); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(item => item.Identifier.ValueText == "InitializeComponent");
		if (method?.Body == null) return;

		// Deliberately NOT a string match against a hardcoded "this.{stripId}.Items"/"Me.{...}":
		// design/session-flush's ThisQualifierRewriter persistently drops the "this."/"Me."
		// qualifier from current.Files' own text (not just its own returned copy), so a second
		// reorder call after a Flush would silently find nothing if this matched by exact text.
		var addRange = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(invocation =>
			invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddRange" } access
			&& IsTargetCollectionAccess(access.Expression, stripId, parentItemId));
		if (addRange?.ArgumentList.Arguments is [{ Expression: ArrayCreationExpressionSyntax { Initializer: { } initializer } }]) {
			var byName = initializer.Expressions.ToDictionary(SimpleTargetName, expression => expression);
			var reordered = orderedNames.Where(byName.ContainsKey).Select(name => byName[name]);
			var newArray = ((ArrayCreationExpressionSyntax)addRange.ArgumentList.Arguments[0].Expression)
				.WithInitializer(initializer.WithExpressions(SyntaxFactory.SeparatedList(reordered)));
			root = root.ReplaceNode(addRange.ArgumentList.Arguments[0].Expression, newArray);
			file.Text = root.NormalizeWhitespace().ToFullString();
			return;
		}

		var addStatements = method.Body.Statements.OfType<ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } access, ArgumentList.Arguments: [var argument] }
			&& IsTargetCollectionAccess(access.Expression, stripId, parentItemId)).ToList();
		if (addStatements.Count < 2) return;
		var statementsByName = addStatements.ToDictionary(
			statement => SimpleTargetName(((InvocationExpressionSyntax)statement.Expression).ArgumentList.Arguments[0].Expression), statement => statement);
		var reorderedStatements = orderedNames.Where(statementsByName.ContainsKey).Select(name => statementsByName[name]).ToList();
		var bodyStatements = method.Body.Statements.ToList();
		var positions = addStatements.Select(statement => bodyStatements.IndexOf(statement)).ToList();
		for (var i = 0; i < positions.Count; i++)
			bodyStatements[positions[i]] = reorderedStatements[i];
		var updatedMethod = method.WithBody(method.Body.WithStatements(SyntaxFactory.List(bodyStatements)));
		root = root.ReplaceNode(method, updatedMethod);
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Whether expression is "&lt;owner&gt;.Items"/"&lt;owner&gt;.DropDownItems" naming
	/// exactly the strip/parent this reorder targets - regardless of a "this."/"Me." qualifier (or
	/// none at all) on owner, since that qualifier is not stable across a Flush (see the caller's
	/// own note).</summary>
	static bool IsTargetCollectionAccess(ExpressionSyntax expression, string stripId, string parentItemId)
	{
		if (expression is not MemberAccessExpressionSyntax access) return false;
		var wantMember = String.IsNullOrEmpty(parentItemId) ? "Items" : "DropDownItems";
		var wantOwner = String.IsNullOrEmpty(parentItemId) ? stripId : parentItemId;
		return access.Name.Identifier.ValueText == wantMember && SimpleTargetName(access.Expression) == wantOwner;
	}

	void RewriteReorderedToolStripItemsVisualBasic(string stripId, string parentItemId, List<string> orderedNames)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().FirstOrDefault(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == "InitializeComponent");
		if (method == null) return;

		var addStatements = method.Statements.OfType<VbSyntax.ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax access, ArgumentList.Arguments.Count: 1 }
			&& access.Name.Identifier.ValueText == "Add" && IsVbTargetCollectionAccess(access.Expression, stripId, parentItemId)).ToList();
		if (addStatements.Count < 2) return;
		var statementsByName = addStatements.ToDictionary(statement => VbSimpleTargetName(
			((VbSyntax.InvocationExpressionSyntax)statement.Expression).ArgumentList.Arguments[0].GetExpression()), statement => statement);
		var reorderedStatements = orderedNames.Where(statementsByName.ContainsKey).Select(name => statementsByName[name]).ToList();
		var bodyStatements = method.Statements.ToList();
		var positions = addStatements.Select(statement => bodyStatements.IndexOf(statement)).ToList();
		for (var i = 0; i < positions.Count; i++)
			bodyStatements[positions[i]] = reorderedStatements[i];
		var updatedMethod = method.WithStatements(Vb.SyntaxFactory.List(bodyStatements));
		root = root.ReplaceNode(method, updatedMethod);
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	static bool IsVbTargetCollectionAccess(VbSyntax.ExpressionSyntax expression, string stripId, string parentItemId)
	{
		if (expression is not VbSyntax.MemberAccessExpressionSyntax access) return false;
		var wantMember = String.IsNullOrEmpty(parentItemId) ? "Items" : "DropDownItems";
		var wantOwner = String.IsNullOrEmpty(parentItemId) ? stripId : parentItemId;
		return access.Name.Identifier.ValueText == wantMember && VbSimpleTargetName(access.Expression) == wantOwner;
	}
#else
	[JsonRpcMethod("design/reorder-toolstrip-item")]
	public DesignerSessionState ReorderToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, int targetIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		throw new NotSupportedException("ToolStrip item reordering is only supported by the Microsoft WinForms designer host.");
	}
#endif

	static int Snap(int value) => (int)Math.Round(value / 8d, MidpointRounding.AwayFromZero) * 8;

	static void SpaceEqually(Control[] controls, bool horizontal)
	{
		if (controls.Length < 3) return;
		var ordered = horizontal ? controls.OrderBy(item => item.Left).ToArray() : controls.OrderBy(item => item.Top).ToArray();
		var first = horizontal ? ordered[0].Left : ordered[0].Top;
		var lastEdge = horizontal ? ordered[^1].Right : ordered[^1].Bottom;
		var occupied = ordered.Sum(item => horizontal ? item.Width : item.Height);
		var gap = (lastEdge - first - occupied) / (ordered.Length - 1);
		var cursor = first;
		foreach (var item in ordered) {
			if (horizontal) item.Left = cursor; else item.Top = cursor;
			cursor += (horizontal ? item.Width : item.Height) + gap;
		}
	}

	static void AdjustSpacing(Control[] controls, bool horizontal, int delta, bool concatenate)
	{
		if (controls.Length < 2) return;
		var ordered = horizontal ? controls.OrderBy(item => item.Left).ToArray() : controls.OrderBy(item => item.Top).ToArray();
		var cursor = horizontal ? ordered[0].Right : ordered[0].Bottom;
		for (var index = 1; index < ordered.Length; index++) {
			var previousEdge = horizontal ? ordered[index - 1].Right : ordered[index - 1].Bottom;
			var currentStart = horizontal ? ordered[index].Left : ordered[index].Top;
			var gap = concatenate ? 0 : Math.Max(0, currentStart - previousEdge + delta);
			if (horizontal) ordered[index].Left = cursor + gap; else ordered[index].Top = cursor + gap;
			cursor = horizontal ? ordered[index].Right : ordered[index].Bottom;
		}
	}

	static IComponent? FindDeepest(Control control, Point point, IContainer? container = null)
	{
		for (var index = control.Controls.Count - 1; index >= 0; index--) {
			var child = control.Controls[index];
			if (!child.Visible || !child.Bounds.Contains(point)) continue;
			// Same container-membership filtering the ToolStrip.Items walk below already applies,
			// extended to plain child Controls: a ToolStripTemplateNode's in-place editor
			// (ToolStripTemplateNode.EnterInSituEdit's TextBox, hosted via a
			// DesignerToolStripControlHost) becomes a REAL child Control of the ToolStrip/dropdown
			// it lives in, but it is never sited in the designer's own IContainer - it is UI, not
			// a component. Without this check, a click on it hit-tested as that raw TextBox and
			// got passed to ISelectionService.SetSelectedComponents, which real WinForms then
			// treated as "selection left" this dropdown's ownership and closed it - reported as
			// "clicking Type Here makes the popup disappear".
			if (container != null && !container.Components.Cast<IComponent>().Contains(child))
				continue;
			return FindDeepest(child, new Point(point.X - child.Left, point.Y - child.Top), container);
		}
		if (!control.ClientRectangle.Contains(point))
			return null;
#if MICROSOFT_WINFORMS
		// A click on a real ToolStrip/MenuStrip/StatusStrip should select the item under the
		// pointer directly, the same way clicking a plain Control does - point is already in
		// this control's own client space by the time we get here, which is the same space
		// ToolStripItem.Bounds live in (both relative to the owning ToolStrip).
		if (control is ToolStrip toolStrip) {
			for (var index = toolStrip.Items.Count - 1; index >= 0; index--) {
				var item = toolStrip.Items[index];
				if (item.Visible && item.Bounds.Contains(point)
					&& (container == null || container.Components.Cast<IComponent>().Contains(item)))
					return item;
			}
		}
#endif
		return control;
	}

	[JsonRpcMethod("shutdown")]
	public void Shutdown() => shutdown.Set();
	[JsonRpcMethod("ping")]
	public void Ping() { }
	[JsonRpcMethod("diagnostics/delay")]
	public async Task Delay(int milliseconds) => await Task.Delay(milliseconds);
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();

	internal void Close()
	{
		designSurface?.Dispose();
		designSurface = null;
		projectLoadContext?.Unload();
		projectLoadContext = null;
		projectAssembly = null;
		referencedAssemblies.Clear();
		current = null;
	}

	void EnsureInitialized()
	{
		if (!initialized) throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
	}

	void EnsureOwnSession(DesignerDocumentSnapshot snapshot) => EnsureOwnSession(snapshot.SessionId, snapshot.DocumentId);

	void EnsureOwnSession(string requestSessionId, string requestDocumentId)
	{
		if (requestSessionId != sessionId)
			throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
		if (current != null && requestDocumentId != current.DocumentId)
			throw new InvalidOperationException("The request's document id does not match the open document.");
	}

	void EnsureCurrentVersion(string sessionId, string documentId, long baseVersion, string operation)
	{
		EnsureInitialized();
		EnsureOwnSession(sessionId, documentId);
		if (current == null || current.Version != baseVersion)
			throw new InvalidOperationException($"Cannot {operation} a stale or unopened document baseVersion.");
	}

	IDesignerHost GetHost() => designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
		?? throw new InvalidOperationException("The designer surface is unavailable.");

	void RewriteProperty(string elementId, string propertyName, object value)
	{
		if (IsVisualBasic) { RewritePropertyVisualBasic(elementId, propertyName, value); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var target = elementId + "." + propertyName;
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target);
		var expression = SerializeValue(value);
		if (assignment != null) {
			file.Text = root.ReplaceNode(assignment.Right, expression.WithTriviaFrom(assignment.Right)).ToFullString();
			return;
		}
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var statement = SyntaxFactory.ExpressionStatement(
			SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
				SyntaxFactory.ParseExpression("this." + target), expression));
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body!.AddStatements(statement)))
			.NormalizeWhitespace().ToFullString();
	}

	void RewritePropertyVisualBasic(string elementId, string propertyName, object value)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var target = elementId + "." + propertyName;
		var assignment = root.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target);
		var expression = SerializeValueVisualBasic(value);
		if (assignment != null) {
			file.Text = root.ReplaceNode(assignment.Right, expression.WithTriviaFrom(assignment.Right)).ToFullString();
			return;
		}
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var statement = Vb.SyntaxFactory.ParseExecutableStatement($"Me.{target} = {expression}");
		file.Text = root.ReplaceNode(method, method.WithStatements(method.Statements.Add(statement)))
			.NormalizeWhitespace().ToFullString();
	}

	void RewriteComponentName(string oldName, string newName)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbTokens = vbRoot.DescendantTokens().Where(token => token.IsKind(Vb.SyntaxKind.IdentifierToken)
				&& token.ValueText == oldName && (token.Parent!.AncestorsAndSelf().OfType<VbSyntax.MethodBlockSyntax>()
					.Any(method => method.BlockStatement is VbSyntax.MethodStatementSyntax ms
					&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
					&& ms.Identifier.ValueText == "InitializeComponent")
					|| token.Parent.AncestorsAndSelf().OfType<VbSyntax.FieldDeclarationSyntax>().Any())).ToArray();
			vbRoot = vbRoot.ReplaceTokens(vbTokens, (token, _) => Vb.SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia));
			file.Text = vbRoot.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var tokens = root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)
			&& token.ValueText == oldName && (token.Parent!.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
				.Any(method => method.Identifier.ValueText == "InitializeComponent")
				|| token.Parent.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().Any())).ToArray();
		root = root.ReplaceTokens(tokens, (token, _) => SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteResetProperty(string elementId, string propertyName)
	{
		var file = CurrentDesignerFile();
		var target = elementId + "." + propertyName;
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbStatements = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(statement => NormalizeTarget(statement.Left.ToString()) == target).ToArray();
			file.Text = vbRoot.RemoveNodes(vbStatements, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var statements = root.DescendantNodes().OfType<ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is AssignmentExpressionSyntax assignment
			&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
			&& NormalizeTarget(assignment.Left.ToString()) == target).ToArray();
		file.Text = root.RemoveNodes(statements, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
	}

	static string NormalizeTarget(string target) => target.StartsWith("this.", StringComparison.Ordinal) ? target[5..]
		: target.StartsWith("Me.", StringComparison.Ordinal) ? target[3..] : target;

	static ExpressionSyntax SerializeValue(object value) => value switch {
		string text => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text)),
		bool boolean => SyntaxFactory.LiteralExpression(boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
		char character => SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(character)),
		byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
			=> SyntaxFactory.ParseExpression(Convert.ToString(value, CultureInfo.InvariantCulture)!),
		Enum enumeration => SyntaxFactory.ParseExpression($"({enumeration.GetType().FullName ?? enumeration.GetType().Name}){Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)}"),
		Point point => SyntaxFactory.ParseExpression($"new System.Drawing.Point({point.X}, {point.Y})"),
		Size size => SyntaxFactory.ParseExpression($"new System.Drawing.Size({size.Width}, {size.Height})"),
		SizeF size => SyntaxFactory.ParseExpression($"new System.Drawing.SizeF({size.Width.ToString(CultureInfo.InvariantCulture)}F, {size.Height.ToString(CultureInfo.InvariantCulture)}F)"),
		Padding padding => SyntaxFactory.ParseExpression($"new System.Windows.Forms.Padding({padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom})"),
		Font font => SyntaxFactory.ParseExpression($"new System.Drawing.Font({SyntaxFactory.Literal(font.Name)}, {font.Size.ToString(CultureInfo.InvariantCulture)}F, (System.Drawing.FontStyle){Convert.ToInt32(font.Style, CultureInfo.InvariantCulture)})"),
		Color color => SyntaxFactory.ParseExpression($"System.Drawing.Color.FromArgb({color.A}, {color.R}, {color.G}, {color.B})"),
		_ => throw new NotSupportedException($"Serializing property values of type {value.GetType().FullName} is not supported yet.")
	};

	/// <summary>VB expression form: literals use True/False/Nothing and the character/float
	/// suffixes ("c, !, R), casts are CType, and struct values use New.</summary>
	static VbSyntax.ExpressionSyntax SerializeValueVisualBasic(object value) => value switch {
		string text => Vb.SyntaxFactory.StringLiteralExpression(Vb.SyntaxFactory.Literal(text)),
		bool boolean => boolean
			? Vb.SyntaxFactory.TrueLiteralExpression(Vb.SyntaxFactory.Token(Vb.SyntaxKind.TrueKeyword))
			: Vb.SyntaxFactory.FalseLiteralExpression(Vb.SyntaxFactory.Token(Vb.SyntaxKind.FalseKeyword)),
		char character => Vb.SyntaxFactory.CharacterLiteralExpression(Vb.SyntaxFactory.Literal(character)),
		int integer => Vb.SyntaxFactory.ParseExpression(integer.ToString(CultureInfo.InvariantCulture)),
		short or ushort or long or ulong or byte or sbyte or uint
			=> Vb.SyntaxFactory.ParseExpression(Convert.ToString(value, CultureInfo.InvariantCulture)!),
		float single => Vb.SyntaxFactory.ParseExpression(single.ToString(CultureInfo.InvariantCulture) + "!"),
		double number => Vb.SyntaxFactory.ParseExpression(number.ToString(CultureInfo.InvariantCulture) + "R"),
		decimal money => Vb.SyntaxFactory.ParseExpression(money.ToString(CultureInfo.InvariantCulture) + "D"),
		Enum enumeration => Vb.SyntaxFactory.ParseExpression($"CType({Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)}, {enumeration.GetType().FullName ?? enumeration.GetType().Name})"),
		Point point => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Point({point.X}, {point.Y})"),
		Size size => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({size.Width}, {size.Height})"),
		SizeF size => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.SizeF({size.Width.ToString(CultureInfo.InvariantCulture)}!, {size.Height.ToString(CultureInfo.InvariantCulture)}!)"),
		Padding padding => Vb.SyntaxFactory.ParseExpression($"New System.Windows.Forms.Padding({padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom})"),
		Font font => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Font(\"{font.Name}\", {font.Size.ToString(CultureInfo.InvariantCulture)}!, CType({Convert.ToInt32(font.Style, CultureInfo.InvariantCulture)}, System.Drawing.FontStyle))"),
		Color color => Vb.SyntaxFactory.ParseExpression($"System.Drawing.Color.FromArgb({color.A}, {color.R}, {color.G}, {color.B})"),
		_ => throw new NotSupportedException($"Serializing property values of type {value.GetType().FullName} is not supported yet.")
	};

	void RewriteEvent(string elementId, EventDescriptor descriptor, string handlerName)
	{
		if (IsVisualBasic) { RewriteEventVisualBasic(elementId, descriptor, handlerName); return; }
		var designerFile = CurrentDesignerFile();
		var root = CSharpSyntaxTree.ParseText(designerFile.Text).GetCompilationUnitRoot();
		var target = elementId + "." + descriptor.Name;
		var isRootComponent = elementId == GetHost().RootComponent?.Site?.Name;
		var existing = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => item.IsKind(SyntaxKind.AddAssignmentExpression)
				&& EventTargetMatches(NormalizeTarget(item.Left.ToString()), target, descriptor.Name, isRootComponent));
		if (String.IsNullOrEmpty(handlerName)) {
			if (existing?.Parent is StatementSyntax statement)
				designerFile.Text = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		var handlerExpression = SyntaxFactory.ParseExpression(handlerName);
		if (existing != null)
			root = root.ReplaceNode(existing.Right, handlerExpression.WithTriviaFrom(existing.Right));
		else {
			var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
				.First(item => item.Identifier.ValueText == "InitializeComponent");
			var sourceTarget = isRootComponent ? "this." + descriptor.Name : "this." + target;
			var statement = SyntaxFactory.ParseStatement($"{sourceTarget} += {handlerName};\n");
			root = root.ReplaceNode(initialize, initialize.WithBody(initialize.Body!.AddStatements(statement)));
		}
		designerFile.Text = root.NormalizeWhitespace().ToFullString();

		var primaryFile = current!.Files.FirstOrDefault(item => item.Kind.Equals("Source", StringComparison.OrdinalIgnoreCase));
		if (primaryFile == null) return;
		var primaryRoot = CSharpSyntaxTree.ParseText(primaryFile.Text).GetCompilationUnitRoot();
		if (primaryRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(item => item.Identifier.ValueText == handlerName)) return;
		var declaration = primaryRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
		if (declaration == null) return;
		var invoke = descriptor.EventType?.GetMethod("Invoke");
		var parameters = invoke?.GetParameters() ?? [];
		var parameterList = String.Join(", ", parameters.Select((parameter, index) =>
			(parameter.ParameterType.FullName ?? parameter.ParameterType.Name) + " " + (parameter.Name ?? "arg" + index)));
		var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
			$"private void {handlerName}({parameterList}) {{\n}}\n")!;
		primaryFile.Text = primaryRoot.ReplaceNode(declaration, declaration.AddMembers(method)).NormalizeWhitespace().ToFullString();
	}

	void RewriteEventVisualBasic(string elementId, EventDescriptor descriptor, string handlerName)
	{
		var designerFile = CurrentDesignerFile();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(designerFile.Text).GetRoot();
		var target = elementId + "." + descriptor.Name;
		var isRootComponent = elementId == GetHost().RootComponent?.Site?.Name;
		var existing = root.DescendantNodes().OfType<VbSyntax.AddRemoveHandlerStatementSyntax>()
			.FirstOrDefault(item => item.IsKind(Vb.SyntaxKind.AddHandlerStatement)
				&& EventTargetMatches(NormalizeTarget(item.EventExpression.ToString()), target, descriptor.Name, isRootComponent));
		if (String.IsNullOrEmpty(handlerName)) {
			if (existing != null)
				designerFile.Text = root.RemoveNode(existing, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		if (existing != null) {
			var handlerExpression = Vb.SyntaxFactory.AddressOfExpression(
				Vb.SyntaxFactory.ParseExpression("Me." + handlerName));
			designerFile.Text = root.ReplaceNode(existing.DelegateExpression, handlerExpression.WithTriviaFrom(existing.DelegateExpression))
				.NormalizeWhitespace().ToFullString();
		} else {
			var initialize = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
				.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			var sourceTarget = isRootComponent ? "Me." + descriptor.Name : "Me." + target;
			var statement = Vb.SyntaxFactory.ParseExecutableStatement($"AddHandler {sourceTarget}, AddressOf Me.{handlerName}");
			designerFile.Text = root.ReplaceNode(initialize, initialize.WithStatements(initialize.Statements.Add(statement)))
				.NormalizeWhitespace().ToFullString();
		}

		var primaryFile = current!.Files.FirstOrDefault(item => item.Kind.Equals("Source", StringComparison.OrdinalIgnoreCase));
		if (primaryFile == null) return;
		var primaryRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(primaryFile.Text).GetRoot();
		if (primaryRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().Any(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms
			&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == handlerName)) return;
		var declaration = primaryRoot.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().FirstOrDefault();
		if (declaration == null) return;
		var invoke = descriptor.EventType?.GetMethod("Invoke");
		var parameters = invoke?.GetParameters() ?? [];
		// VB's NormalizeWhitespace round-trips "name As Type" parameter lists cleanly, while a
		// bare "Type name" list gets "System . Object" spacing artifacts.
		var parameterList = String.Join(", ", parameters.Select((parameter, index) =>
			(parameter.Name ?? "arg" + index) + " As " + (parameter.ParameterType.FullName ?? parameter.ParameterType.Name)));
		var member = ParseMemberMethod($"Private Sub {handlerName}({parameterList})\nEnd Sub");
		primaryFile.Text = primaryRoot.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(member)))
			.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Parses a single VB member declaration through a dummy partial class; the
	/// VisualBasic SyntaxFactory has no ParseMemberDeclaration equivalent.</summary>
	static VbSyntax.MethodBlockSyntax ParseMemberMethod(string text)
	{
		var unit = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText("Partial Class Dummy\n" + text + "\nEnd Class").GetRoot();
		return unit.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First();
	}

	readonly Dictionary<string, string> typeIconCache = new(StringComparer.Ordinal);

	/// <summary>Real WinForms toolbox icon for a CLR type - the same embedded-resource lookup
	/// (<c>System.Drawing.ToolboxBitmapAttribute.GetImageFromResource</c>) real Visual Studio's
	/// Toolbox/smart-tag/insert-item UI relies on, walking up the type hierarchy when a type has
	/// no explicit [ToolboxBitmap]. Works for both Control and ToolStripItem types (unlike
	/// ResolveControlType, which requires Control assignability), so it can serve the smart-tag
	/// popup's own type rows and the ToolStrip insert-item dropdown's item types alike. Cached
	/// per type name since the embedded resource never changes within a session.</summary>
	[JsonRpcMethod("design/get-type-icon")]
	public DesignerTypeIconResult GetTypeIcon(string typeName)
	{
		if (typeIconCache.TryGetValue(typeName, out var cached))
			return new DesignerTypeIconResult { Accepted = true, PngBase64 = cached };
		try {
			var type = ResolveTypeForIcon(typeName)
				?? throw new NotSupportedException("Unknown type: " + typeName);
			using var image = System.Drawing.ToolboxBitmapAttribute.GetImageFromResource(type, null, false);
			var base64 = "";
			if (image != null) {
				using var stream = new MemoryStream();
				image.Save(stream, ImageFormat.Png);
				base64 = Convert.ToBase64String(stream.ToArray());
			}
			typeIconCache[typeName] = base64;
			return new DesignerTypeIconResult { Accepted = true, PngBase64 = base64 };
		} catch (Exception exception) {
			// A missing/unsupported icon is not a client-visible error - the smart-tag/insert-item
			// popups fall back to their own placeholder glyph when PngBase64 comes back empty.
			// Only genuinely unexpected failures (bad type name) are reported as Accepted=false.
			return new DesignerTypeIconResult { Accepted = false, Error = exception.Message };
		}
	}

	/// <summary>Like <see cref="ResolveControlType"/> but for icon lookup only: no Control-
	/// assignability requirement, since ToolStripItem (StatusStrip/MenuStrip items) is not a
	/// Control but still has a real toolbox icon.</summary>
	Type? ResolveTypeForIcon(string name)
	{
		var fullName = name.Contains('.') ? name : "System.Windows.Forms." + name;
		return projectAssembly?.GetType(fullName, false)
			?? referencedAssemblies.Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(candidate => candidate != null)
			?? typeof(Control).Assembly.GetType(fullName, false)
			?? AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(fullName, false)).FirstOrDefault(item => item != null);
	}

	Type ResolveControlType(string name)
	{
		var fullName = name.Contains('.') ? name : "System.Windows.Forms." + name;
		var type = projectAssembly?.GetType(fullName, false) ?? referencedAssemblies.Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(candidate => candidate != null) ?? typeof(Control).Assembly.GetType(fullName, false)
			?? AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(fullName, false)).FirstOrDefault(item => item != null);
		if (type == null || !typeof(Control).IsAssignableFrom(type) || type.IsAbstract)
			throw new NotSupportedException("Unsupported WinForms control type: " + name);
		return type;
	}

	void RewriteAddedControl(string parentId, Type type, string elementId, int x, int y, int width, int height)
	{
		if (IsVisualBasic) { RewriteAddedControlVisualBasic(parentId, type, elementId, x, y, width, height); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		var parentExpression = parentId == className
			? "this" : "this." + parentId;
		var statements = new[] {
			SyntaxFactory.ParseStatement($"this.{elementId} = new {type.FullName}();\n"),
			SyntaxFactory.ParseStatement($"this.{elementId}.Location = new System.Drawing.Point({x}, {y});\n"),
			SyntaxFactory.ParseStatement($"this.{elementId}.Size = new System.Drawing.Size({width}, {height});\n"),
			SyntaxFactory.ParseStatement($"{parentExpression}.Controls.Add(this.{elementId});\n")
		};
		var updatedMethod = method.WithBody(method.Body!.AddStatements(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(item => item.Identifier.ValueText == className);
		var field = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private {type.FullName} {elementId};\n")!;
		root = root.ReplaceNode(declaration, declaration.AddMembers(field));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteAddedControlVisualBasic(string parentId, Type type, string elementId, int x, int y, int width, int height)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
		var parentExpression = parentId == className ? "Me" : "Me." + parentId;
		var statements = new[] {
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId} = New {type.FullName}()"),
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Location = New System.Drawing.Point({x}, {y})"),
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Size = New System.Drawing.Size({width}, {height})"),
			Vb.SyntaxFactory.ParseExecutableStatement($"{parentExpression}.Controls.Add(Me.{elementId})")
		};
		var updatedMethod = method.WithStatements(method.Statements.AddRange(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().First(item => item.BlockStatement.Identifier.ValueText == className);
		var field = ParseMemberField($"Friend WithEvents {elementId} As {type.FullName}");
		root = root.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(field)));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Parses a single VB field declaration through a dummy partial class.</summary>
	static VbSyntax.FieldDeclarationSyntax ParseMemberField(string text)
	{
		var unit = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText("Partial Class Dummy\n" + text + "\nEnd Class").GetRoot();
		return unit.DescendantNodes().OfType<VbSyntax.FieldDeclarationSyntax>().First();
	}

	void RewriteBounds(string elementId, int x, int y, int width, int height)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			vbRoot = ReplaceRequiredAssignmentVisualBasic(vbRoot, elementId + ".Location",
				Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Point({x}, {y})"));
			vbRoot = ReplaceRequiredAssignmentVisualBasic(vbRoot, elementId + ".Size",
				Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({width}, {height})"));
			file.Text = vbRoot.ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		root = ReplaceRequiredAssignment(root, elementId + ".Location",
			SyntaxFactory.ParseExpression($"new System.Drawing.Point({x}, {y})"));
		root = ReplaceRequiredAssignment(root, elementId + ".Size",
			SyntaxFactory.ParseExpression($"new System.Drawing.Size({width}, {height})"));
		file.Text = root.ToFullString();
	}

	void RewriteRootSize(int width, int height)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbAssignment = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) is "ClientSize" or "Size");
			var vbValue = Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({width}, {height})");
			if (vbAssignment != null) {
				// The selection outline and rendered bitmap use the outer Form bounds.  Persist
				// those exact bounds to Form.Size rather than ClientSize; otherwise each drag
				// adds the caption/border dimensions again when the host reloads the form.
				var vbReplacement = Vb.SyntaxFactory.AssignmentStatement(Vb.SyntaxKind.SimpleAssignmentStatement,
					Vb.SyntaxFactory.ParseExpression("Me.Size"), Vb.SyntaxFactory.Token(Vb.SyntaxKind.EqualsToken), vbValue).WithTriviaFrom(vbAssignment);
				file.Text = vbRoot.ReplaceNode(vbAssignment, vbReplacement).ToFullString();
				return;
			}
			var vbInitialize = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
				.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			file.Text = vbRoot.ReplaceNode(vbInitialize, vbInitialize.WithStatements(vbInitialize.Statements.Add(
				Vb.SyntaxFactory.ParseExecutableStatement($"Me.ClientSize = New System.Drawing.Size({width}, {height})"))))
				.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) is "ClientSize" or "Size");
		var value = SyntaxFactory.ParseExpression($"new System.Drawing.Size({width}, {height})");
		if (assignment != null) {
			// The selection outline and rendered bitmap use the outer Form bounds. Persist
			// those exact bounds to Form.Size rather than ClientSize; otherwise each drag
			// adds the caption/border dimensions again when the host reloads the form.
			var replacement = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
				SyntaxFactory.ParseExpression("this.Size"), value).WithTriviaFrom(assignment);
			file.Text = root.ReplaceNode(assignment, replacement).ToFullString();
			return;
		}
		var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		file.Text = root.ReplaceNode(initialize, initialize.WithBody(initialize.Body!.AddStatements(
			SyntaxFactory.ParseStatement($"this.ClientSize = new System.Drawing.Size({width}, {height});\n"))))
			.NormalizeWhitespace().ToFullString();
	}

	/// <summary>The bare identifier a "this.foo"/"Me.foo" member-access expression (or a plain
	/// "foo" identifier) ultimately names - matches how AddToolStripItem's own generated statements
	/// (and every existing designer-source fixture) reference a sited component.</summary>
	static string SimpleTargetName(ExpressionSyntax expression) => expression switch {
		MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => ""
	};

	static string VbSimpleTargetName(VbSyntax.ExpressionSyntax expression) => expression switch {
		VbSyntax.MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		VbSyntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => ""
	};

	void RewriteDeletedComponent(string elementId)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			// Shrink a shared AddRange array in place first - see the C# branch's own note on why.
			foreach (var arrayCreation in vbRoot.DescendantNodes().OfType<VbSyntax.ArrayCreationExpressionSyntax>().ToArray()) {
				if (arrayCreation.Initializer is not { } initializer) continue;
				var match = initializer.Initializers.FirstOrDefault(expression => VbSimpleTargetName(expression) == elementId);
				if (match == null || initializer.Initializers.Count <= 1) continue;
				var shrunk = initializer.WithInitializers(initializer.Initializers.Remove(match));
				vbRoot = vbRoot.ReplaceNode(initializer, shrunk);
			}
			// MethodBlockSyntax (InitializeComponent's own included) IS a StatementSyntax in VB's
			// model too - see the C# branch's own comment on why that must be excluded here.
			var vbStatements = vbRoot.DescendantNodes().OfType<VbSyntax.StatementSyntax>()
				.Where(statement => statement is not VbSyntax.MethodBlockSyntax
					&& statement.DescendantNodesAndSelf().OfType<VbSyntax.IdentifierNameSyntax>()
						.Any(identifier => identifier.Identifier.ValueText == elementId)).ToArray();
			vbRoot = vbRoot.RemoveNodes(vbStatements, SyntaxRemoveOptions.KeepNoTrivia)!;
			var vbFields = vbRoot.DescendantNodes().OfType<VbSyntax.FieldDeclarationSyntax>()
				.Where(field => field.Declarators.SelectMany(declarator => declarator.Names)
					.Any(name => name.Identifier.ValueText == elementId)).ToArray();
			vbRoot = vbRoot.RemoveNodes(vbFields, SyntaxRemoveOptions.KeepNoTrivia)!;
			file.Text = vbRoot.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		// A deleted item that is one of SEVERAL elements in a shared "collection.AddRange(new
		// T[] { a, b, c })" call (see RewriteReorderedToolStripItems' own note on this shape) must
		// only lose ITS OWN array element - not the whole statement, which would silently drop
		// every OTHER sibling in that same AddRange from the designer source too. Shrinking the
		// array here, before the generic statement-removal pass below, means that pass no longer
		// sees this identifier in the (now-shorter) array and leaves the statement alone.
		foreach (var arrayCreation in root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().ToArray()) {
			if (arrayCreation.Initializer is not { } initializer) continue;
			var match = initializer.Expressions.FirstOrDefault(expression => SimpleTargetName(expression) == elementId);
			if (match == null || initializer.Expressions.Count <= 1) continue;
			var shrunk = initializer.WithExpressions(initializer.Expressions.Remove(match));
			root = root.ReplaceNode(initializer, shrunk);
		}
		// BlockSyntax (a `{ ... }` body, InitializeComponent's own included) IS a StatementSyntax
		// in Roslyn's model, so without excluding it here it always ends up in this list too -
		// InitializeComponent's body mentions elementId somewhere by construction - and
		// RemoveNodes drops an ANCESTOR before its own now-redundant descendants, silently wiping
		// the WHOLE METHOD BODY (every other component's statements included) instead of just the
		// deleted component's own few statements.
		var statements = root.DescendantNodes().OfType<StatementSyntax>()
			.Where(statement => statement is not BlockSyntax
				&& statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
					.Any(identifier => identifier.Identifier.ValueText == elementId)).ToArray();
		root = root.RemoveNodes(statements, SyntaxRemoveOptions.KeepNoTrivia)!;
		var variables = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
			.Where(variable => variable.Identifier.ValueText == elementId).ToArray();
		foreach (var variable in variables) {
			if (variable.Parent?.Parent is FieldDeclarationSyntax field)
				root = root.RemoveNode(field, SyntaxRemoveOptions.KeepNoTrivia)!;
		}
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteZOrder(string parentId, string elementId, int childIndex)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) { RewriteZOrderVisualBasic(parentId, elementId, childIndex); return; }
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var oldStatements = initialize.Body!.Statements.Where(statement => {
			var invocation = statement.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault();
			return invocation?.Expression is MemberAccessExpressionSyntax member
				&& member.Name.Identifier.ValueText == "SetChildIndex"
				&& NormalizeTarget(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? "") == elementId;
		}).ToArray();
		var body = initialize.Body.RemoveNodes(oldStatements, SyntaxRemoveOptions.KeepNoTrivia)!;
		var parentExpression = String.IsNullOrEmpty(parentId) || parentId == initialize.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText
			? "this" : "this." + parentId;
		body = body.AddStatements(SyntaxFactory.ParseStatement($"{parentExpression}.Controls.SetChildIndex(this.{elementId}, {childIndex});\n"));
		file.Text = root.ReplaceNode(initialize, initialize.WithBody(body)).NormalizeWhitespace().ToFullString();
	}

	void RewriteZOrderVisualBasic(string parentId, string elementId, int childIndex)
	{
		var file = CurrentDesignerFile();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var initialize = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var oldStatements = initialize.Statements.Where(statement => {
			var invocation = statement.DescendantNodes().OfType<VbSyntax.InvocationExpressionSyntax>().FirstOrDefault();
			return invocation?.Expression is VbSyntax.MemberAccessExpressionSyntax member
				&& member.Name.Identifier.ValueText == "SetChildIndex"
				&& NormalizeTarget(invocation.ArgumentList.Arguments.FirstOrDefault() is VbSyntax.SimpleArgumentSyntax first ? first.Expression.ToString() : "") == elementId;
		}).ToArray();
		var body = initialize.WithStatements(Vb.SyntaxFactory.List(initialize.Statements.Where(statement => !oldStatements.Contains(statement))));
		var parentExpression = String.IsNullOrEmpty(parentId) || parentId == initialize.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText
			? "Me" : "Me." + parentId;
		body = body.WithStatements(body.Statements.Add(Vb.SyntaxFactory.ParseExecutableStatement(
			$"{parentExpression}.Controls.SetChildIndex(Me.{elementId}, {childIndex})")));
		file.Text = root.ReplaceNode(initialize, body).NormalizeWhitespace().ToFullString();
	}

	DesignerSourceFileSnapshot CurrentDesignerFile() => current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
		?? current.Files.First();

	static VbSyntax.CompilationUnitSyntax ReplaceRequiredAssignmentVisualBasic(VbSyntax.CompilationUnitSyntax root, string target, VbSyntax.ExpressionSyntax value)
	{
		var assignment = root.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target)
			?? throw new NotSupportedException("The existing designer source has no assignment for " + target + ".");
		return root.ReplaceNode(assignment.Right, value.WithTriviaFrom(assignment.Right));
	}

	static CompilationUnitSyntax ReplaceRequiredAssignment(CompilationUnitSyntax root, string target, ExpressionSyntax value)
	{
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target)
			?? throw new NotSupportedException("The existing designer source has no assignment for " + target + ".");
		return root.ReplaceNode(assignment.Right, value.WithTriviaFrom(assignment.Right));
	}

	static void Validate(DesignerDocumentSnapshot snapshot)
	{
		if (snapshot.Version < 0) throw new ArgumentOutOfRangeException(nameof(snapshot.Version));
		if (String.IsNullOrWhiteSpace(snapshot.PrimaryFileName)) throw new ArgumentException("A primary file is required.");
		if (snapshot.Files.Count == 0) throw new ArgumentException("At least one source file is required.");
		if (snapshot.Files.Count > 256) throw new ArgumentException("A designer snapshot may contain at most 256 files.");
		long payloadSize = 0;
		foreach (var file in snapshot.Files) {
			if (file.FileName.Length > 4096) throw new ArgumentException("A designer snapshot file name is too long.");
			payloadSize += file.Text.Length * sizeof(char);
			payloadSize += file.Base64.Length * 3L / 4L;
			if (payloadSize > 16 * 1024 * 1024)
				throw new ArgumentException("The designer snapshot exceeds the 16 MiB payload limit.");
		}
	}

	/// <summary>Normalize a project output path to the managed assembly: <c>OutputAssemblyFullPath</c>
	/// can point at the apphost (an extensionless executable on Unix, or a ".exe" native shim on
	/// Windows), whose bytes are not valid IL. The managed assembly always sits next to it as
	/// "<c>name</c>.dll".</summary>
	static string ResolveManagedAssemblyPath(string path)
	{
		var full = Path.GetFullPath(path);
		if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			return full;
		var dll = Path.ChangeExtension(full, ".dll");
		return File.Exists(dll) ? dll : full;
	}

	void CreateDesignSurface(DesignerDocumentSnapshot snapshot)
	{
		Trace("CreateDesignSurface disposing previous surface");
		designSurface?.Dispose();
		projectLoadContext?.Unload();
		projectLoadContext = null;
		projectAssembly = null;
		referencedAssemblies.Clear();
		rootDesignSize = ReadRootDesignSize(snapshot);
		rootAutoScaleDimensions = ReadRootAutoScaleDimensions(snapshot);
		if (!String.IsNullOrWhiteSpace(snapshot.ProjectAssemblyPath) && File.Exists(snapshot.ProjectAssemblyPath)) {
			var managedAssemblyPath = ResolveManagedAssemblyPath(snapshot.ProjectAssemblyPath);
			if (File.Exists(managedAssemblyPath)) {
				projectLoadContext = new ProjectAssemblyLoadContext(managedAssemblyPath);
				projectAssembly = projectLoadContext.LoadFromAssemblyPath(managedAssemblyPath);
			}
		}
		var referencePaths = snapshot.ReferencedAssemblyPaths.Where(path => File.Exists(path) && !string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase)).ToArray();
		if (projectLoadContext == null && referencePaths.Length > 0)
			projectLoadContext = new ProjectAssemblyLoadContext(Path.GetFullPath(referencePaths[0]));
		if (projectLoadContext != null) {
			int loaded = 0;
			foreach (var path in referencePaths) {
				try { referencedAssemblies.Add(projectLoadContext.LoadFromAssemblyPath(Path.GetFullPath(path))); loaded++; } catch { }
			}
		}
		Trace("CreateDesignSurface constructing DesignSurface");
		designSurface = new DesignSurface();
		Trace("CreateDesignSurface beginning loader");
		designSurface.BeginLoad(new SnapshotDesignerLoader(snapshot, ResolveProjectType));
		Trace("CreateDesignSurface loader completed");
		if (!designSurface.IsLoaded) {
			var errors = designSurface.LoadErrors?.Cast<object>().Select(item => item?.ToString()).Where(item => !String.IsNullOrEmpty(item));
			var errStr = String.Join(" | ", errors ?? []);
			throw new InvalidOperationException("The child design surface failed to load: " + errStr);
		}
		if (designSurface.View is Control view) {
			Trace("CreateDesignSurface creating view control");
			view.CreateControl();
			view.PerformLayout();
			Trace("CreateDesignSurface laid out view control");
		}
	}

	static string VbArgumentText(VbSyntax.ObjectCreationExpressionSyntax creation, int index)
		=> ((VbSyntax.SimpleArgumentSyntax)creation.ArgumentList!.Arguments[index]).Expression.ToString();

	static bool SnapshotIsVisualBasic(DesignerDocumentSnapshot snapshot) => snapshot.Language.Equals("VisualBasic", StringComparison.OrdinalIgnoreCase)
		|| snapshot.DesignerFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
		|| snapshot.PrimaryFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

	static Size? ReadRootDesignSize(DesignerDocumentSnapshot snapshot)
	{
		var source = snapshot.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))?.Text;
		if (String.IsNullOrEmpty(source)) return null;
		if (SnapshotIsVisualBasic(snapshot)) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var vbCreation = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(item => NormalizeTarget(item.Left.ToString()) is "Size" or "ClientSize")
				.Select(item => item.Right).OfType<VbSyntax.ObjectCreationExpressionSyntax>().FirstOrDefault();
			if (vbCreation?.ArgumentList?.Arguments.Count != 2
				|| !Int32.TryParse(VbArgumentText(vbCreation, 0), out var vbWidth)
				|| !Int32.TryParse(VbArgumentText(vbCreation, 1), out var vbHeight)) return null;
			return new Size(vbWidth, vbHeight);
		}
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var creation = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(item => NormalizeTarget(item.Left.ToString()) is "Size" or "ClientSize")
			.Select(item => item.Right).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
		if (creation?.ArgumentList?.Arguments.Count != 2
			|| !Int32.TryParse(creation.ArgumentList.Arguments[0].Expression.ToString(), out var width)
			|| !Int32.TryParse(creation.ArgumentList.Arguments[1].Expression.ToString(), out var height)) return null;
		return new Size(width, height);
	}

	static SizeF? ReadRootAutoScaleDimensions(DesignerDocumentSnapshot snapshot)
	{
		var source = snapshot.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))?.Text;
		if (String.IsNullOrEmpty(source)) return null;
		if (SnapshotIsVisualBasic(snapshot)) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var vbCreation = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(item => NormalizeTarget(item.Left.ToString()) == "AutoScaleDimensions")
				.Select(item => item.Right).OfType<VbSyntax.ObjectCreationExpressionSyntax>().FirstOrDefault();
			if (vbCreation?.ArgumentList?.Arguments.Count != 2
				|| !Single.TryParse(VbArgumentText(vbCreation, 0).TrimEnd('!', 'F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vbWidth)
				|| !Single.TryParse(VbArgumentText(vbCreation, 1).TrimEnd('!', 'F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vbHeight)) return null;
			return new SizeF(vbWidth, vbHeight);
		}
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var creation = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(item => NormalizeTarget(item.Left.ToString()) == "AutoScaleDimensions")
			.Select(item => item.Right).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
		if (creation?.ArgumentList?.Arguments.Count != 2
			|| !Single.TryParse(creation.ArgumentList.Arguments[0].Expression.ToString().TrimEnd('F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
			|| !Single.TryParse(creation.ArgumentList.Arguments[1].Expression.ToString().TrimEnd('F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return null;
		return new SizeF(width, height);
	}

	Type? ResolveProjectType(string name) => projectAssembly?.GetType(name, false);

	DesignerSessionState CurrentState(long baseVersion)
	{
		Trace("CurrentState resolving design host");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var rootControl = host?.RootComponent as Control;
		Trace("CurrentState rendering frame");
		var render = Render(rootControl, rootDesignSize);
		Trace("CurrentState building element tree");
		var tree = rootControl == null ? null : BuildElementTree(rootControl, "", host?.Container);
		Trace("CurrentState describing components");
		var components = host?.Container?.Components.Cast<IComponent>().Select(component => {
			var properties = DescribeProperties(component);
			if (component == host.RootComponent && rootAutoScaleDimensions.HasValue) {
				var scale = properties.FirstOrDefault(item => item.Name == "AutoScaleDimensions");
				if (scale != null)
					scale.Value = $"{rootAutoScaleDimensions.Value.Width.ToString(CultureInfo.InvariantCulture)}, {rootAutoScaleDimensions.Value.Height.ToString(CultureInfo.InvariantCulture)}";
			}
			return new DesignerComponentInfo {
			Name = component.Site?.Name ?? "",
			Type = component.GetType().FullName ?? component.GetType().Name,
			Parent = component is Control control ? control.Parent?.Site?.Name ?? ""
#if MICROSOFT_WINFORMS
				: component is ToolStripItem toolStripItem ? ToolStripItemParentName(toolStripItem) : "",
#else
				: "",
#endif
			Text = component is Control textControl ? textControl.Text ?? ""
#if MICROSOFT_WINFORMS
				: component is ToolStripItem textItem ? textItem.Text ?? "" : "",
#else
				: "",
#endif
			AccessibleName = PropertyText(component, "AccessibleName") is { Length: > 0 } accessibleName
				? accessibleName : component is Control namedControl && !String.IsNullOrEmpty(namedControl.Text)
					? namedControl.Text : component.Site?.Name ?? "",
			AccessibleDescription = PropertyText(component, "AccessibleDescription"),
			AccessibleRole = PropertyText(component, "AccessibleRole") is { Length: > 0 } accessibleRole
				&& accessibleRole != "Default"
				? accessibleRole : component.GetType().Name,
			X = component is Control boundsControl ? boundsControl.Left
#if MICROSOFT_WINFORMS
				: component is ToolStripItem boundsItem ? boundsItem.Bounds.X : 0,
#else
				: 0,
#endif
			Y = component is Control boundsControl2 ? boundsControl2.Top
#if MICROSOFT_WINFORMS
				: component is ToolStripItem boundsItem2 ? boundsItem2.Bounds.Y : 0,
#else
				: 0,
#endif
			SurfaceX = component is Control surfaceControl ? SurfaceLocation(surfaceControl).X
#if MICROSOFT_WINFORMS
				: component is ToolStripItem surfaceItem && surfaceItem.Owner != null
					? SurfaceLocation(surfaceItem.Owner).X + surfaceItem.Bounds.X : 0,
#else
				: 0,
#endif
			SurfaceY = component is Control surfaceControl2 ? SurfaceLocation(surfaceControl2).Y
#if MICROSOFT_WINFORMS
				: component is ToolStripItem surfaceItem2 && surfaceItem2.Owner != null
					? SurfaceLocation(surfaceItem2.Owner).Y + surfaceItem2.Bounds.Y : 0,
#else
				: 0,
#endif
			Width = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
				? (component as Control)?.Width ?? rootDesignSize.Value.Width
				: component is Control sizeControl ? sizeControl.Width
				: component is ToolStripItem sizeItem ? sizeItem.Bounds.Width : 0,
#else
				? rootDesignSize.Value.Width
				: component is Control sizeControl ? sizeControl.Width : 0,
#endif
			Height = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
				? (component as Control)?.Height ?? rootDesignSize.Value.Height
				: component is Control sizeControl2 ? sizeControl2.Height
				: component is ToolStripItem sizeItem2 ? sizeItem2.Bounds.Height : 0,
#else
				? rootDesignSize.Value.Height
				: component is Control sizeControl2 ? sizeControl2.Height : 0,
#endif
			IsTrayComponent = IsTrayComponent(component),
			IsControl = component is Control,
#if MICROSOFT_WINFORMS
			IsDropDownItem = component is ToolStripItem { OwnerItem: not null },
#endif
			ItemInsertionStyle = ItemInsertionStyle(component),
			NewItemTypeNames = NewItemTypeNames(component),
			Properties = properties,
			Events = DescribeEvents(component)
			};
		}).ToList() ?? [];
		Trace("CurrentState described components");
		var diagnostics = new List<DesignerDiagnostic>();
		if (portableGpuReadbackUnavailable)
			diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = "GPU frame readback is unavailable; showing the bounded software fallback frame." });
		return new DesignerSessionState {
			SessionId = current?.SessionId ?? sessionId ?? "",
			DocumentId = current?.DocumentId ?? "",
			Version = baseVersion,
			Accepted = true,
			RootType = host?.RootComponent?.GetType().FullName ?? "",
			ComponentCount = host?.Container?.Components.Count ?? 0,
			Render = render,
			Diagnostics = diagnostics,
			Tree = tree,
			Components = components,
#if MICROSOFT_WINFORMS
			Popups = rootControl == null ? [] : CapturePopupFrames(rootControl)
#endif
		};
	}

	/// <summary>How this strip lets the user add items, mirroring the branch in
	/// ToolStripTemplateNode.SetupNewEditNode: a MenuStrip (like any dropdown) gets the editable
	/// "Type Here" cell built by SetUpMenuTemplateNode, while ToolStrip/StatusStrip and
	/// ContextMenuStrip get the split button built by SetUpToolTemplateNode. Anything that is not
	/// a strip gets None.</summary>
	static string ItemInsertionStyle(IComponent component)
	{
#if MICROSOFT_WINFORMS
		// A ToolStripDropDownItem (a menu item with its own submenu, like "File") is not itself a
		// MenuStrip/ToolStripDropDown, but ToolStripDesignerUtils.GetToolStripFromComponent
		// resolves ITS "ToolStrip" as item.DropDown - so it gets the same TypeHere style as the
		// dropdown it owns, for the popup overlay this item's own expanded submenu needs.
		if (component is MenuStrip or ToolStripDropDown or ToolStripDropDownItem)
			return DesignerItemInsertionStyles.TypeHere;
		if (component is ToolStrip)
			return DesignerItemInsertionStyles.SplitButton;
#endif
		return DesignerItemInsertionStyles.None;
	}

	/// <summary>The item types this strip's template node offers, in
	/// ToolStripDesignerUtils.GetStandardItemTypes' own order - its FIRST entry is what
	/// CommitTextToDesigner falls back to when the user types a name without picking a type.
	/// Reproduced here (rather than reflected out of the internal ToolStripDesignerUtils) so both
	/// backends and the client agree on one list.</summary>
	static List<string> NewItemTypeNames(IComponent component)
	{
#if MICROSOFT_WINFORMS
		const string ns = "System.Windows.Forms.";
		if (component is MenuStrip)
			return [ns + "ToolStripMenuItem", ns + "ToolStripComboBox", ns + "ToolStripTextBox"];
		// ToolStripDropDownItem: same list as ToolStripDropDown, for the reason ItemInsertionStyle
		// above documents - GetToolStripFromComponent resolves this item's own "ToolStrip" as its
		// DropDown, which is a ToolStripDropDownMenu (this list), not a plain ToolStripDropDown.
		if (component is ToolStripDropDown or ToolStripDropDownItem)
			return [ns + "ToolStripMenuItem", ns + "ToolStripComboBox", ns + "ToolStripSeparator", ns + "ToolStripTextBox"];
		if (component is StatusStrip)
			return [ns + "ToolStripStatusLabel", ns + "ToolStripProgressBar", ns + "ToolStripDropDownButton", ns + "ToolStripSplitButton"];
		if (component is ToolStrip)
			return [ns + "ToolStripButton", ns + "ToolStripLabel", ns + "ToolStripSplitButton",
				ns + "ToolStripDropDownButton", ns + "ToolStripSeparator", ns + "ToolStripComboBox",
				ns + "ToolStripTextBox", ns + "ToolStripProgressBar"];
#endif
		return [];
	}

	/// <summary>Whether a component gets an entry in the component tray. Ports the rule from
	/// System.Windows.Forms.Design.DocumentDesigner.OnComponentAdded, whose own comment reads
	/// "If the component is a toolstrip or a top level form, we should add to the tray":
	/// <code>
	/// bool addControl = designer is ToolStripDesigner
	///     || designer is not ControlDesigner cd
	///     || (cd.Control is Form form &amp;&amp; form.TopLevel);
	/// if (!addControl || !attributes.Contains(DesignTimeVisibleAttribute.Yes)) return;
	/// </code>
	/// The ToolStripDesigner clause is why every MenuStrip/ToolStrip/StatusStrip (and
	/// BindingNavigator, whose designer derives from ToolStripDesigner) appears in the tray IN
	/// ADDITION to being laid out on the surface, while ToolStripContainer - a ControlDesigner
	/// that is not a ToolStripDesigner - does not. The "not a ControlDesigner" clause covers the
	/// non-visual components (Timer/ImageList/ToolTip/dialogs) and the Controls whose designer is
	/// a plain ComponentDesigner (ContextMenuStrip, PrintPreviewDialog).
	///
	/// NOTE this is deliberately NOT ComponentTray.CanCreateComponentFromTool: that predicate
	/// answers a different question (may a toolbox item be created by dropping it ONTO the tray)
	/// and excludes the strips, which is how this started out wrong.</summary>
	bool IsTrayComponent(IComponent component)
	{
		try {
			// The root component is the design surface itself, never a tray entry. Real WinForms
			// gets this from its TopLevel check (a hosted root form has TopLevel=false), which
			// does not hold for this out-of-process host's own root form.
			if (component == (designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost)?.RootComponent)
				return false;
			if (!TypeDescriptor.GetAttributes(component).Contains(DesignTimeVisibleAttribute.Yes))
				return false;
#if MICROSOFT_WINFORMS
			var designerType = DeclaredDesignerType(component.GetType());
			if (IsToolStripDesigner(designerType)) return true;
			if (!IsControlDesigner(designerType)) return true;
			return component is Form { TopLevel: true };
#else
			// The portable LibreWinForms fork does not ship the System.Windows.Forms.Design
			// designer types these attributes name, so the designer-kind clauses cannot be
			// evaluated there; only the "not a Control at all" case can be honored.
			return component is not Control;
#endif
		} catch {
			return false;
		}
	}

#if MICROSOFT_WINFORMS
	/// <summary>The type named by the component type's DesignerAttribute registered against the
	/// IDesigner base type, or NULL when it declares none / it cannot be loaded - the same lookup
	/// ComponentTray.GetDesignerType performs.</summary>
	static Type? DeclaredDesignerType(Type componentType)
	{
		foreach (var attribute in TypeDescriptor.GetAttributes(componentType).OfType<DesignerAttribute>()) {
			if (Type.GetType(attribute.DesignerBaseTypeName, false) == typeof(IDesigner))
				return Type.GetType(attribute.DesignerTypeName, false);
		}
		return null;
	}

	static bool IsControlDesigner(Type? designerType)
		=> designerType != null && typeof(System.Windows.Forms.Design.ControlDesigner).IsAssignableFrom(designerType);

	/// <summary>The equivalent of DocumentDesigner's <c>designer is ToolStripDesigner</c> test.
	/// Matched by name across the base chain because ToolStripDesigner is INTERNAL to
	/// System.Windows.Forms.Design and cannot be referenced as a type - walking the chain still
	/// catches the derived designers that must behave the same way (BindingNavigatorDesigner).</summary>
	static bool IsToolStripDesigner(Type? designerType)
	{
		for (var type = designerType; type != null; type = type.BaseType) {
			if (type.FullName == "System.Windows.Forms.Design.ToolStripDesigner")
				return true;
		}
		return false;
	}
#endif

	static string PropertyText(IComponent component, string propertyName)
	{
		try {
			var property = TypeDescriptor.GetProperties(component)[propertyName];
			var value = property?.GetValue(component);
			return value == null ? "" : property!.Converter.ConvertToInvariantString(value) ?? "";
		} catch { return ""; }
	}

	/// <summary>Builds the element tree for the Document Outline pad (the protocol's
	/// <c>Tree</c> shape), mirroring the flat <c>Components</c> list's control hierarchy.
	/// A ToolStrip/MenuStrip/StatusStrip's real children are ToolStripItems living in its
	/// Items collection - Controls is empty (or holds only internal implementation controls
	/// like the overflow button), so walking only Controls silently drops every menu item,
	/// toolbar button and status label from the outline.
	/// <paramref name="container"/> is the design host's own component container (null when no
	/// host is active, e.g. before a document is open) - both Controls and Items must be filtered
	/// to components it actually contains. Once an IDesignerHost/ToolStripDesigner is attached
	/// (true for every open document, not just an edge case), .NET's own ToolStrip design-time
	/// support injects live in-place-edit scaffolding - System.Windows.Forms.Design.
	/// ToolStripTemplateNode's TransparentToolStrip overlay control, ItemTypeToolStripMenuItem
	/// "choose item type" entries, DesignerToolStripControlHost - directly into the SAME runtime
	/// Controls/Items collections as the real, user-added items. Unlike the flat Components list
	/// below (built from host.Container.Components, which never contains this scaffolding because
	/// it was never sited through it), a naive Controls/Items walk cannot tell them apart from
	/// genuine children by type or position - only container membership distinguishes them.</summary>
	static DesignerElementNode BuildElementTree(Control control, string path, IContainer? container)
	{
		var children = control.Controls.Cast<Control>()
			.Where(child => container == null || container.Components.Cast<IComponent>().Contains(child))
			.Select((child, index) => BuildElementTree(child, ChildPath(path, index), container));
#if MICROSOFT_WINFORMS
		// The portable LibreWinForms ToolStripItem does not expose Bounds/Owner/OwnerItem or
		// ToolStripDropDownItem at all, so this walk only exists for the real Microsoft backend.
		if (control is ToolStrip toolStrip) {
			var offset = control.Controls.Count;
			children = children.Concat(toolStrip.Items.Cast<ToolStripItem>()
				.Where(item => container == null || container.Components.Cast<IComponent>().Contains(item))
				.Select((item, index) => BuildToolStripItemNode(item, ChildPath(path, offset + index), container)));
		}
#endif
		return new DesignerElementNode {
			Id = control.Site?.Name ?? control.GetType().Name,
			Name = control.Site?.Name,
			Type = control.GetType().FullName ?? control.GetType().Name,
			X = control.Left,
			Y = control.Top,
			Width = control.Width,
			Height = control.Height,
			Path = path,
			IsDesignable = true,
			Children = children.ToList()
		};
	}

	static string ChildPath(string path, int index) =>
		path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "," + index.ToString(CultureInfo.InvariantCulture);

#if MICROSOFT_WINFORMS
	/// <summary>A ToolStripDropDownItem's own children (a menu item's submenu) live in
	/// DropDownItems, not Items - only ToolStrip/MenuStrip/StatusStrip themselves use Items.
	/// Same container-membership filtering as BuildElementTree: a submenu can carry the same
	/// unsited ItemTypeToolStripMenuItem/DesignerToolStripControlHost design-time scaffolding.</summary>
	static DesignerElementNode BuildToolStripItemNode(ToolStripItem item, string path, IContainer? container)
	{
		var children = item is ToolStripDropDownItem dropDown
			? dropDown.DropDownItems.Cast<ToolStripItem>()
				.Where(child => container == null || container.Components.Cast<IComponent>().Contains(child))
				.Select((child, index) => BuildToolStripItemNode(child, ChildPath(path, index), container))
				.ToList()
			: [];
		return new DesignerElementNode {
			Id = item.Site?.Name ?? item.GetType().Name,
			Name = item.Site?.Name,
			Type = item.GetType().FullName ?? item.GetType().Name,
			X = item.Bounds.X,
			Y = item.Bounds.Y,
			Width = item.Bounds.Width,
			Height = item.Bounds.Height,
			Path = path,
			IsDesignable = true,
			Children = children
		};
	}
#endif

	List<DesignerEventInfo> DescribeEvents(IComponent component)
	{
		var handlers = CurrentDesignerFile().Text;
		// The root form is conventionally emitted as "this.Load += ..." rather
		// than "Form1.Load += ...".  A component's site name is still Form1,
		// so accept the root shorthand as well as the normal component target.
		var isRootComponent = ReferenceEquals(component, GetHost().RootComponent);
		return TypeDescriptor.GetEvents(component).Cast<EventDescriptor>().Where(item => item.IsBrowsable).Select(item => {
			var target = (component.Site?.Name ?? "") + "." + item.Name;
			var handler = "";
			if (IsVisualBasic) {
				var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(handlers).GetRoot();
				var statement = vbRoot.DescendantNodes().OfType<VbSyntax.AddRemoveHandlerStatementSyntax>()
					.FirstOrDefault(node => node.IsKind(Vb.SyntaxKind.AddHandlerStatement)
						&& EventTargetMatches(NormalizeTarget(node.EventExpression.ToString()), target, item.Name, isRootComponent));
				handler = statement?.DelegateExpression.ToString() ?? "";
				if (handler.StartsWith("AddressOf Me.", StringComparison.Ordinal)) handler = handler["AddressOf ".Length..];
				if (handler.StartsWith("Me.", StringComparison.Ordinal)) handler = handler[3..];
			} else {
				var root = CSharpSyntaxTree.ParseText(handlers).GetCompilationUnitRoot();
				var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
					.FirstOrDefault(node => node.IsKind(SyntaxKind.AddAssignmentExpression)
						&& EventTargetMatches(NormalizeTarget(node.Left.ToString()), target, item.Name, isRootComponent));
				handler = assignment?.Right.ToString() ?? "";
				if (handler.StartsWith("this.", StringComparison.Ordinal)) handler = handler[5..];
			}
			return new DesignerEventInfo { Name = item.Name, Category = item.Category ?? "Action", HandlerTypeName = item.EventType?.FullName ?? item.EventType?.Name ?? "", Handler = handler };
		}).ToList();
	}

	static bool EventTargetMatches(string normalizedTarget, string componentTarget, string eventName, bool isRootComponent)
		=> normalizedTarget == componentTarget || (isRootComponent && normalizedTarget == eventName);

	List<DesignerPropertyInfo> DescribeProperties(IComponent component)
	{
		var result = new List<DesignerPropertyInfo>();
		var elementId = component.Site?.Name ?? "";
		var designerRoot = IsVisualBasic ? null : CSharpSyntaxTree.ParseText(CurrentDesignerFile().Text).GetCompilationUnitRoot();
		var vbDesignerRoot = IsVisualBasic ? (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(CurrentDesignerFile().Text).GetRoot() : null;
		foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
			if (!property.IsBrowsable || property.Name is "Site" or "Container" or "Parent") continue;
			var assignedInSource = IsVisualBasic
				? vbDesignerRoot!.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>().Any(assignment =>
					NormalizeTarget(assignment.Left.ToString()) == elementId + "." + property.Name)
				: designerRoot!.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
					assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
					&& NormalizeTarget(assignment.Left.ToString()) == elementId + "." + property.Name);
			var isImageProperty = typeof(Image).IsAssignableFrom(property.PropertyType);
			object? value;
			string serialized;
			try {
				value = property.GetValue(component);
				if (value == null) serialized = isImageProperty && assignedInSource ? "[binary]" : "";
				// Portable resource loading can materialize an image entry as a byte-backed
				// object rather than a concrete System.Drawing.Image. The property contract is
				// still Image, so expose it as an opaque binary DDP value instead of leaking an
				// implementation-specific converter string to the Properties pad.
				else if (value is Image || isImageProperty) serialized = "[binary]";
				else if (value is Padding padding) serialized = $"{padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom}";
				else if (value is Font font) serialized = $"{font.Name}, {font.Size.ToString(CultureInfo.InvariantCulture)}, {font.Style}";
				else if (value is SizeF size) serialized = $"{size.Width.ToString(CultureInfo.InvariantCulture)}, {size.Height.ToString(CultureInfo.InvariantCulture)}";
				else if (property.Converter.CanConvertTo(typeof(string))) serialized = property.Converter.ConvertToInvariantString(value) ?? "";
				else serialized = "[binary]";
			} catch { continue; }
			result.Add(new DesignerPropertyInfo {
				Name = property.Name,
				DisplayName = property.DisplayName ?? property.Name,
				Description = property.Description ?? "",
				Category = property.Category ?? "Misc",
				TypeName = property.PropertyType.FullName ?? property.PropertyType.Name,
				Value = serialized,
				IsNull = value == null && !(isImageProperty && assignedInSource),
				IsReadOnly = property.IsReadOnly || (!property.Converter.CanConvertFrom(typeof(string))
					&& property.PropertyType != typeof(Padding) && property.PropertyType != typeof(Font)
					&& property.PropertyType != typeof(SizeF)),
				// The source assignment is authoritative. Some LibreWinForms
				// descriptors keep returning true after ResetValue.
				ShouldSerialize = assignedInSource,
				IsEnum = property.PropertyType.IsEnum
			});
		}
		return result;
	}

	/// <summary>Position relative to the ROOT design component, which is the coordinate space the
	/// render frame and hit-testing both use.
	///
	/// Anchoring on the root explicitly rather than on "the first parentless control" matters
	/// because the two runtimes parent the root differently: under LibreWinForms the root Form has
	/// no parent, so the old walk stopped there by accident, but the Microsoft WinForms design
	/// surface hosts the root inside its own frame control and positions it at an offset (15,15).
	/// The old loop therefore folded that frame offset into every component, putting the reported
	/// coordinates 15px off from the very bitmap and hit-test results they are meant to line up
	/// with.</summary>
#if MICROSOFT_WINFORMS
	/// <summary>A ToolStripItem's logical parent for the Properties Pad / flat component list:
	/// a submenu item's parent is the ToolStripDropDownItem that owns its dropdown (OwnerItem);
	/// a top-level item's parent is the ToolStrip/MenuStrip/StatusStrip itself (Owner).</summary>
	static string ToolStripItemParentName(ToolStripItem item) =>
		item.OwnerItem?.Site?.Name ?? item.Owner?.Site?.Name ?? "";
#endif

	Point SurfaceLocation(Control control)
	{
		var root = (designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost)?.RootComponent as Control;
		if (control == root) return Point.Empty;
		var offset = RootClientOffset(root);
#if MICROSOFT_WINFORMS
		// Measure against the root's SCREEN origin rather than by summing Locations up the parent
		// chain. The chain walk cannot describe an expanded menu dropdown: the designer parents
		// those into its own adorner window, so the loop below never reaches the root and sums
		// unrelated ancestor offsets - which is exactly why the dropdown items' outlines and name
		// labels landed away from the dropdown the designer had actually drawn. Screen-relative
		// measurement is also the basis PaintExpandedDropDowns composites with, so the reported
		// geometry and the painted pixels agree by construction.
		if (root != null && root.IsHandleCreated && control.IsHandleCreated) {
			try {
				var origin = control.PointToScreen(Point.Empty);
				var rootOrigin = root.PointToScreen(Point.Empty);
				return new Point(origin.X - rootOrigin.X + offset.X, origin.Y - rootOrigin.Y + offset.Y);
			} catch {
				// Fall through to the Location walk below.
			}
		}
#endif
		var point = control.Location;
		for (var parent = control.Parent; parent != null && parent != root; parent = parent.Parent)
			point.Offset(parent.Location);
		point.Offset(offset.X, offset.Y);
		return point;
	}

#if MICROSOFT_WINFORMS
	/// <summary>Captures every menu dropdown the designer currently has expanded as ITS OWN
	/// bitmap/frame, rather than compositing them into the root frame.
	///
	/// Needed because ToolStripMenuItemDesigner keeps a selected menu item's dropdown open
	/// (TopLevel=false, AutoClose=false, ShowDropDown()), and it is parented into the designer's
	/// own adorner window rather than the form, so Form.DrawToBitmap never sees it - the items'
	/// geometry was already reported correctly while their pixels were missing. Reporting each as
	/// an independent DesignerPopupFrame (rather than baking it into the shared bitmap) is what
	/// lets the client host it as its own WPF overlay: pointer/keyboard input aimed at the popup
	/// then hit-tests and drags against just that surface, never needing to reverse through the
	/// root form's own coordinate space or fight the root frame's own adorners for z-order.</summary>
	List<DesignerPopupFrame> CapturePopupFrames(Control root)
	{
		var frames = new List<DesignerPopupFrame>();
		try {
			// A ContextMenuStrip's own ToolStripDropDownDesigner.InitializeDropDown shows it
			// UNCONDITIONALLY as soon as the component exists (not gated on selection - see
			// SelectedContextMenuStripPopups), via a synthetic owner item ExpandedDropDowns'
			// existing MenuStrip-oriented walk happens to discover it through - reported under
			// that synthetic item's own (wrong, internal) element id, not the real strip's name.
			// Every ContextMenuStrip reference is therefore excluded here and re-added, correctly
			// named and selection-gated, by SelectedContextMenuStripPopups below.
			var fromExpanded = ExpandedDropDowns(root).Where(entry => entry.DropDown is not ContextMenuStrip).ToList();
			var fromContextMenus = SelectedContextMenuStripPopups().ToList();
			foreach (var (ownerId, dropDown) in fromExpanded.Concat(fromContextMenus)) {
				if (dropDown.Width <= 0 || dropDown.Height <= 0)
					continue;
				// SurfaceLocation, not a second copy of the offset math: this must be the same
				// basis DesignerComponentInfo.SurfaceX/Y use, or the overlay lands in the wrong
				// spot relative to everything else the client draws.
				var origin = SurfaceLocation(dropDown);
				using var bitmap = new Bitmap(dropDown.Width, dropDown.Height);
				dropDown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
				using var stream = new MemoryStream();
				bitmap.Save(stream, ImageFormat.Png);
				frames.Add(new DesignerPopupFrame {
					OwnerElementId = ownerId,
					X = origin.X,
					Y = origin.Y,
					TypeHereBounds = FindTemplateNodeBounds(dropDown),
					Render = new DesignerRenderFrame {
						Sequence = Interlocked.Increment(ref frameSequence),
						Width = bitmap.Width,
						Height = bitmap.Height,
						Dpi = 1,
						PngBase64 = Convert.ToBase64String(stream.ToArray())
					}
				});
			}
		} catch (Exception exception) {
			// A popup that cannot be captured must not cost us the whole frame.
			Trace("CapturePopupFrames failed: " + exception.Message);
		}
		return frames;
	}

	/// <summary>The real "Type Here" template node's own bounds within a dropdown, or null when
	/// it has none. Matched by type name (both "DesignerToolStripControlHost", the wrapper
	/// ToolStripTemplateNode.EnterInSituEdit swaps in) rather than by reference, since
	/// System.Windows.Forms.Design's template-node types are all internal and cannot be named
	/// directly from this assembly - the same constraint <see cref="IsToolStripDesigner"/> already
	/// works around for the designer TYPES themselves.</summary>
	static DesignerRectangle? FindTemplateNodeBounds(ToolStripDropDown dropDown)
	{
		foreach (ToolStripItem item in dropDown.Items) {
			if (item.GetType().Name is not ("DesignerToolStripControlHost" or "ToolStripControlHost"))
				continue;
			return new DesignerRectangle { X = item.Bounds.X, Y = item.Bounds.Y, Width = item.Bounds.Width, Height = item.Bounds.Height };
		}
		return null;
	}

	/// <summary>Every dropdown currently held open by the designer, outermost first so a client
	/// that z-orders by list position still stacks nested submenus correctly. Paired with the
	/// element id of the ToolStripDropDownItem that owns it (empty for a strip's own
	/// ContextMenuStrip), which the client uses to keep the same overlay across frames.</summary>
	static IEnumerable<(string OwnerElementId, ToolStripDropDown DropDown)> ExpandedDropDowns(Control root)
	{
		var strips = root.Controls.Cast<Control>().OfType<ToolStrip>().ToList();
		var pending = new Queue<ToolStrip>(strips);
		while (pending.Count > 0) {
			var strip = pending.Dequeue();
			foreach (var item in strip.Items.Cast<ToolStripItem>().OfType<ToolStripDropDownItem>()) {
				if (item.DropDown is not { Visible: true } dropDown)
					continue;
				yield return (item.Site?.Name ?? "", dropDown);
				pending.Enqueue(dropDown);
			}
		}
	}

	/// <summary>ContextMenuStrip is never parented into <c>root.Controls</c> - it lives only in the
	/// tray - so <see cref="ExpandedDropDowns"/> never finds it. Unlike a MenuStrip submenu, the
	/// real ContextMenuStripDesigner (ToolStripDropDownDesigner.InitializeDropDown) shows it
	/// unconditionally as soon as the component exists, not gated on selection - so reusing that
	/// designer's own Visible flag would make every ContextMenuStrip permanently overlay the
	/// surface. OpenDevelop deliberately narrows this to "shown only while selected" (its own tray
	/// icon, or one of its own items/submenu items), matching the "default hidden, click the tray
	/// icon to edit like a main menu" UX asked for, rather than VS's always-on behaviour.</summary>
	IEnumerable<(string OwnerElementId, ToolStripDropDown DropDown)> SelectedContextMenuStripPopups()
	{
		var host = GetHost();
		if (designSurface?.GetService(typeof(ISelectionService)) is not ISelectionService selection)
			yield break;
		var selected = selection.GetSelectedComponents().Cast<IComponent>().ToHashSet();
		foreach (var strip in host.Container.Components.Cast<IComponent>().OfType<ContextMenuStrip>()) {
			if (strip.Width <= 0 || strip.Height <= 0)
				continue;
			var owns = selected.Contains(strip)
				|| selected.OfType<ToolStripItem>().Any(item => BelongsTo(item, strip));
			if (owns)
				yield return (strip.Site?.Name ?? "", strip);
		}
	}

	/// <summary>Whether item, or one of the (possibly several) submenu levels containing it,
	/// belongs to this exact ContextMenuStrip - the same walk <c>PopupTypeHereEditor.Commit</c>
	/// does client-side to find the real Control a template node's new item belongs to, mirrored
	/// here server-side. Checks .Owner == strip at EVERY level and stops as soon as it matches,
	/// rather than walking all the way up to whatever ToolStrip ultimately owns the chain: real
	/// ContextMenuStripDesigner wires the strip's OWN OwnerItem to an internal synthetic item (so
	/// ExpandedDropDowns' existing MenuStrip-oriented walk can discover it too, always-on rather
	/// than selection-gated - see CapturePopupFrames/SelectedContextMenuStripPopups), so climbing
	/// past a match here would walk right past the real strip into that internal plumbing and
	/// never find it.</summary>
	static bool BelongsTo(ToolStripItem item, ContextMenuStrip strip)
	{
		ToolStripItem? current = item;
		var guard = 0;
		while (current != null && guard++ < 32) {
			if (current.Owner == strip)
				return true;
			current = (current.Owner as ToolStripDropDown)?.OwnerItem;
		}
		return false;
	}
#endif

	/// <summary>How far the painted bitmap's origin sits outside the root form's client area:
	/// native Form.DrawToBitmap paints the outer window (border + caption) while every child
	/// Location is client-space. Surface (bitmap) coordinates therefore differ from client
	/// coordinates by this much, in both directions - <see cref="SurfaceLocation"/> adds it when
	/// reporting bounds, and <see cref="HitTest"/> must subtract it before comparing an incoming
	/// surface point against client-space Control/ToolStripItem bounds.</summary>
	static Point RootClientOffset(Control? root)
	{
#if MICROSOFT_WINFORMS
		if (root is Form form) {
			var border = Math.Max(0, (form.Width - form.ClientSize.Width) / 2);
			return new Point(border, Math.Max(border, form.Height - form.ClientSize.Height - border));
		}
#endif
		return Point.Empty;
	}

	DesignerRenderFrame? Render(Control? root, Size? designSize)
	{
		if (root == null) return null;
		Trace("Render sizing root");
		if (root.Width <= 0 || root.Height <= 0) root.Size = new Size(300, 200);
		Trace("Render creating root control");
		root.CreateControl();
		Trace("Render laying out root control");
		root.PerformLayout();
		var renderSize = designSize ?? root.Size;
#if MICROSOFT_WINFORMS
		// Do not crop the non-client frame that DrawToBitmap actually paints. Its dimensions
		// must match the root selection rectangle and the child SurfaceLocation offsets above.
		if (root is Form)
			renderSize = root.Size;
#endif
		Trace("Render creating bitmap");
		var bitmap = new Bitmap(Math.Max(1, renderSize.Width), Math.Max(1, renderSize.Height));
		Trace("Render creating graphics");
		using (var graphics = Graphics.FromImage(bitmap)) {
#if MICROSOFT_WINFORMS
			// The Microsoft child uses the native WinForms paint pipeline. The portable host keeps
			// its software painter below because it has no HWND/GDI implementation to capture.
			root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, renderSize));
			// DesignSurface Forms lack a real HWND so DrawToBitmap never paints the non-client
			// frame.  Overlay a simulated title bar so the designer surface visually identifies
			// the root component as a windowed form.
			if (root is Form formForChrome)
				PaintFormChrome(formForChrome, graphics, new Rectangle(Point.Empty, renderSize));
#else
			Trace("Render painting portable frame");
			if (designSize.HasValue) {
				PaintStandardControl(root, graphics, new Rectangle(Point.Empty, renderSize));
				foreach (Control child in root.Controls) {
					var state = graphics.Save();
					graphics.TranslateTransform(child.Left, child.Top);
					PaintControl(child, graphics);
					graphics.Restore(state);
				}
			} else PaintControl(root, graphics);
#endif
		}
		Trace("Render encoding PNG");
#if MICROSOFT_WINFORMS
		using var stream = new MemoryStream();
		bitmap.Save(stream, ImageFormat.Png);
		// Width/Height must be read before Dispose() - a disposed Bitmap's GDI+ handle is invalid,
		// and querying either property throws ArgumentException("Parameter is not valid.") instead
		// of returning the size it had a moment ago.
		var bitmapWidth = bitmap.Width;
		var bitmapHeight = bitmap.Height;
		bitmap.Dispose();
		Trace("Render encoded PNG");
		return new DesignerRenderFrame {
			Sequence = Interlocked.Increment(ref frameSequence),
			Width = bitmapWidth,
			Height = bitmapHeight,
			// The portable renderer paints in WinForms logical pixels. ProGPU's
			// Bitmap does not expose a device resolution on macOS.
			Dpi = 1,
			PngBase64 = Convert.ToBase64String(stream.ToArray())
		};
#else
		if (portableGpuReadbackUnavailable) {
			bitmap.Dispose();
			return FallbackFrame(root, renderSize);
		}
		var encoding = Task.Run(() => {
			try {
				using var stream = new MemoryStream();
				bitmap.Save(stream, ImageFormat.Png);
				return Convert.ToBase64String(stream.ToArray());
			} finally {
				bitmap.Dispose();
			}
		});
		if (!encoding.Wait(TimeSpan.FromSeconds(2))) {
			portableGpuReadbackUnavailable = true;
			_ = encoding.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
			return FallbackFrame(root, renderSize);
		}
		Trace("Render encoded PNG");
		return new DesignerRenderFrame {
			Sequence = Interlocked.Increment(ref frameSequence), Width = renderSize.Width, Height = renderSize.Height, Dpi = 1,
			PngBase64 = encoding.GetAwaiter().GetResult()
		};
#endif
	}

	DesignerRenderFrame FallbackFrame(Control root, Size size)
	{
		var pixels = new byte[checked(size.Width * size.Height * 4)];
		for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = SystemColors.Control.B; pixels[i + 1] = SystemColors.Control.G; pixels[i + 2] = SystemColors.Control.R; pixels[i + 3] = 255; }
		PaintFallback(root, 0, 0, pixels, size.Width, size.Height);
		return new DesignerRenderFrame { Sequence = Interlocked.Increment(ref frameSequence), Width = size.Width, Height = size.Height, Dpi = 1, Data = DesignerFrameCodec.EncodeDeflateBase64(pixels) };
	}

	static void PaintFallback(Control control, int offsetX, int offsetY, byte[] pixels, int width, int height)
	{
		var x = offsetX + control.Left; var y = offsetY + control.Top;
		var right = Math.Min(width, x + Math.Max(1, control.Width)); var bottom = Math.Min(height, y + Math.Max(1, control.Height));
		for (var yy = Math.Max(0, y); yy < bottom; yy++) for (var xx = Math.Max(0, x); xx < right; xx++) {
			var i = (yy * width + xx) * 4; var border = xx == x || yy == y || xx == right - 1 || yy == bottom - 1;
			pixels[i] = border ? (byte)96 : control.BackColor.B; pixels[i + 1] = border ? (byte)96 : control.BackColor.G; pixels[i + 2] = border ? (byte)96 : control.BackColor.R; pixels[i + 3] = 255;
		}
		foreach (Control child in control.Controls) PaintFallback(child, x, y, pixels, width, height);
	}

	static void PaintControl(Control control, Graphics graphics)
	{
		var bounds = new Rectangle(Point.Empty, control.Size);
#if MICROSOFT_WINFORMS
		PaintStandardControl(control, graphics, bounds);
#else
		if (control is IPortableWinFormsPaintSource paintSource && paintSource.SupportsPortablePainting) {
			var args = new PaintEventArgs(graphics, bounds);
			paintSource.PaintPortableBackground(args);
			paintSource.PaintPortable(args);
		} else PaintStandardControl(control, graphics, bounds);
		foreach (Control child in control.Controls) {
			var state = graphics.Save();
			graphics.TranslateTransform(child.Left, child.Top);
			PaintControl(child, graphics);
			graphics.Restore(state);
		}
#endif
	}

	static void PaintStandardControl(Control control, Graphics graphics, Rectangle bounds)
	{
		var back = control.BackColor.IsEmpty ? SystemColors.Control : control.BackColor;
		var fore = control.ForeColor.IsEmpty ? SystemColors.ControlText : control.ForeColor;
		using var background = new SolidBrush(back);
		using var foreground = new SolidBrush(fore);
		using var border = new Pen(SystemColors.ControlDark, 1);
		using var light = new Pen(SystemColors.ControlLightLight, 1);
		graphics.FillRectangle(background, bounds);
		var font = control.Font ?? SystemFonts.DefaultFont;

		if (control is Button) {
			graphics.FillRectangle(new SolidBrush(SystemColors.Control), bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			graphics.DrawLine(light, 1, 1, Math.Max(1, bounds.Width - 2), 1);
			DrawCenteredText(graphics, control.Text, font, foreground, bounds);
		} else if (control is TextBoxBase) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(3, 3));
		} else if (control is CheckBox or RadioButton) {
			var mark = new Rectangle(1, Math.Max(1, (bounds.Height - 13) / 2), 12, 12);
			if (control is RadioButton) graphics.DrawEllipse(border, mark);
			else graphics.DrawRectangle(border, mark);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(17, Math.Max(1, (bounds.Height - font.Height) / 2f)));
		} else if (control is ComboBox or NumericUpDown) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			var buttonWidth = Math.Min(18, bounds.Width);
			graphics.FillRectangle(new SolidBrush(SystemColors.Control), bounds.Width - buttonWidth, 1, buttonWidth - 1, Math.Max(0, bounds.Height - 2));
			graphics.DrawLine(border, bounds.Width - buttonWidth, 1, bounds.Width - buttonWidth, bounds.Height - 2);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(3, 3));
		} else if (control is GroupBox) {
			var top = Math.Max(6, font.Height / 2);
			graphics.DrawRectangle(border, 0, top, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - top - 1));
			graphics.FillRectangle(background, 8, 0, Math.Min(bounds.Width - 10, graphics.MeasureString(control.Text ?? "", font).Width + 4), font.Height + 2);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(10, 0));
		} else if (control is TabControl tabs) {
			graphics.FillRectangle(Brushes.White, 1, 22, Math.Max(0, bounds.Width - 2), Math.Max(0, bounds.Height - 23));
			graphics.DrawRectangle(border, 0, 21, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 22));
			var left = 2;
			foreach (TabPage page in tabs.TabPages) {
				var width = Math.Max(45, (int)graphics.MeasureString(page.Text ?? "", font).Width + 14);
				graphics.FillRectangle(page == tabs.SelectedTab ? Brushes.White : background, left, 1, width, 21);
				graphics.DrawRectangle(border, left, 1, width, 21);
				graphics.DrawString(page.Text ?? "", font, foreground, new PointF(left + 7, 4));
				left += width + 1;
			}
		} else if (control is TreeView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			DrawPlaceholderRows(graphics, font, foreground, border, bounds, tree: true);
		} else if (control is ListView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			DrawPlaceholderRows(graphics, font, foreground, border, bounds, tree: false);
		} else if (control is DataGridView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.FillRectangle(background, 1, 1, Math.Max(0, bounds.Width - 2), Math.Min(23, bounds.Height - 2));
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			for (var x = 36; x < bounds.Width; x += 80) graphics.DrawLine(border, x, 1, x, bounds.Height - 2);
			for (var y = 24; y < bounds.Height; y += 22) graphics.DrawLine(border, 1, y, bounds.Width - 2, y);
			graphics.DrawString("DataGridView", font, foreground, new PointF(42, 4));
		} else if (control is MenuStrip or ToolStrip) {
			graphics.FillRectangle(new SolidBrush(SystemColors.Menu), bounds);
			graphics.DrawLine(border, 0, Math.Max(0, bounds.Height - 1), bounds.Width, Math.Max(0, bounds.Height - 1));
			graphics.DrawString(control is MenuStrip ? "File    Edit    View" : "New   Open   Save", font, foreground, new PointF(6, Math.Max(1, (bounds.Height - font.Height) / 2f)));
		} else if (control is Panel) {
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
		} else if (control is ListBox) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
		} else if (control is ProgressBar progress) {
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			var range = Math.Max(1, progress.Maximum - progress.Minimum);
			var fill = Math.Max(0, (bounds.Width - 4) * (progress.Value - progress.Minimum) / range);
			graphics.FillRectangle(new SolidBrush(SystemColors.Highlight), 2, 2, fill, Math.Max(0, bounds.Height - 4));
		} else if (control is Label) {
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(0, Math.Max(0, (bounds.Height - font.Height) / 2f)));
		} else if (!String.IsNullOrEmpty(control.Text)) {
			graphics.DrawString(control.Text, font, foreground, new PointF(3, 3));
		}
	}

	static void DrawPlaceholderRows(Graphics graphics, Font font, Brush foreground, Pen border, Rectangle bounds, bool tree)
	{
		for (var row = 0; row < 4; row++) {
			var y = 5 + row * 20;
			if (y + font.Height >= bounds.Height) break;
			var indent = tree ? 8 + row * 8 : 8;
			if (tree) {
				graphics.DrawRectangle(border, indent, y + 3, 8, 8);
				graphics.DrawLine(border, indent + 2, y + 7, indent + 6, y + 7);
			}
			graphics.DrawString(tree ? "Node " + (row + 1) : "List item " + (row + 1), font, foreground,
				new PointF(indent + (tree ? 14 : 0), y));
		}
	}

	static void DrawCenteredText(Graphics graphics, string? text, Font font, Brush brush, Rectangle bounds)
	{
		if (String.IsNullOrEmpty(text)) return;
		var size = graphics.MeasureString(text, font);
		graphics.DrawString(text, font, brush,
			new PointF(Math.Max(2, (bounds.Width - size.Width) / 2), Math.Max(1, (bounds.Height - size.Height) / 2)));
	}

	static void PaintFormChrome(Form form, Graphics graphics, Rectangle bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0) return;
		const int titleHeight = 30;
		const int btnW = 12;
		const int btnH = 12;
		const int btnPad = 4;

		using var titleBg = new SolidBrush(SystemColors.ActiveCaption);
		using var titleText = new SolidBrush(SystemColors.ActiveCaptionText);
		using var captionBorder = new Pen(SystemColors.ControlDark, 1);
		using var btnFace = new SolidBrush(SystemColors.Control);
		using var btnHighlight = new Pen(SystemColors.ControlLightLight, 1);
		using var btnShadow = new Pen(SystemColors.ControlDark, 1);
		using var closeRed = new SolidBrush(Color.FromArgb(196, 43, 28));
		using var titleFont = new Font(SystemFonts.DefaultFont.FontFamily,
			Math.Max(7, SystemFonts.DefaultFont.Size), FontStyle.Bold);

		graphics.FillRectangle(titleBg, 0, 0, bounds.Width, Math.Min(titleHeight, bounds.Height));
		graphics.DrawLine(captionBorder, 0, Math.Min(titleHeight, bounds.Height) - 1, bounds.Width - 1, Math.Min(titleHeight, bounds.Height) - 1);

		if (!string.IsNullOrEmpty(form.Text)) {
			var textRect = new RectangleF(6, 0, Math.Max(0, bounds.Width - (btnW + btnPad) * 3 - 16), titleHeight);
			var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
			graphics.DrawString(form.Text, titleFont, titleText, textRect, fmt);
		}

		var btnY = Math.Max(2, (titleHeight - btnH) / 2);
		var btnX = bounds.Width - (btnW + btnPad);

		graphics.FillRectangle(closeRed, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		var cx = btnX + btnW / 2;
		var cy = btnY + btnH / 2;
		graphics.DrawLine(Pens.White, cx - 3, cy - 3, cx + 3, cy + 3);
		graphics.DrawLine(Pens.White, cx + 3, cy - 3, cx - 3, cy + 3);

		btnX -= btnW + btnPad;
		graphics.FillRectangle(btnFace, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(btnHighlight, btnX + 1, btnY + 1, Math.Max(0, btnW - 3), Math.Max(0, btnH - 3));
		graphics.DrawLine(btnShadow, btnX + 3, btnY + btnH - 3, btnX + btnW - 3, btnY + btnH - 3);
		graphics.DrawLine(btnShadow, btnX + btnW - 3, btnY + 3, btnX + btnW - 3, btnY + btnH - 3);

		btnX -= btnW + btnPad;
		graphics.FillRectangle(btnFace, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		graphics.DrawLine(btnShadow, btnX + btnW / 2, btnY + 4, btnX + btnW / 2, btnY + btnH - 4);
		graphics.DrawLine(btnShadow, btnX + 3, btnY + btnH / 2, btnX + btnW - 3, btnY + btnH / 2);
	}

	static DesignerSessionState Accepted(long baseVersion) => new() { Version = baseVersion, Accepted = true };
	DesignerSessionState Rejected(long baseVersion, string error) => new() { SessionId = current?.SessionId ?? sessionId ?? "", DocumentId = current?.DocumentId ?? "", Version = baseVersion, Error = error };
}

sealed class ThisQualifierRewriter : CSharpSyntaxRewriter
{
	public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
	{
		if (node.Expression is ThisExpressionSyntax)
			return node.Name.WithTriviaFrom(node);
		return base.VisitMemberAccessExpression(node);
	}
}

sealed class MeQualifierRewriter : Vb.VisualBasicSyntaxRewriter
{
	public override SyntaxNode? VisitMemberAccessExpression(VbSyntax.MemberAccessExpressionSyntax node)
	{
		if (node.Expression is VbSyntax.MeExpressionSyntax)
			return node.Name.WithTriviaFrom(node);
		return base.VisitMemberAccessExpression(node);
	}
}


sealed class ProjectAssemblyLoadContext : AssemblyLoadContext
{
	readonly AssemblyDependencyResolver resolver;
	public ProjectAssemblyLoadContext(string assemblyPath) : base(isCollectible: true) => resolver = new AssemblyDependencyResolver(assemblyPath);
	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is "System.Windows.Forms" or "System.Drawing.Common" or "System.Drawing") return null;
		var path = resolver.ResolveAssemblyToPath(assemblyName);
		return path == null ? null : LoadFromAssemblyPath(path);
	}
}
