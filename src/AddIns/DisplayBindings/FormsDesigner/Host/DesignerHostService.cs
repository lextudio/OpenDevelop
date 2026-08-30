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
		if (!CryptographicOperations.FixedTimeEquals(
			Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
			throw new UnauthorizedAccessException("Invalid designer-host token.");
		if (protocolVersion != ProtocolVersion)
			throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
		initialized = true;
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = ProtocolVersion, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot)
	{
		EnsureInitialized();
		EnsureOwnSession(snapshot);
		Validate(snapshot);
		CreateDesignSurface(snapshot);
		current = snapshot;
		return CurrentState(snapshot.Version);
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

	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "hit-test");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var root = host?.RootComponent as Control;
		var hit = root == null ? null : FindDeepest(root, new Point(x, y));
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
		if (component is Control) RewriteProperty(newName, "Name", newName);
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

	static Control? FindDeepest(Control control, Point point)
	{
		for (var index = control.Controls.Count - 1; index >= 0; index--) {
			var child = control.Controls[index];
			if (!child.Visible || !child.Bounds.Contains(point)) continue;
			return FindDeepest(child, new Point(point.X - child.Left, point.Y - child.Top));
		}
		return control.ClientRectangle.Contains(point) ? control : null;
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

	void RewriteDeletedComponent(string elementId)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbStatements = vbRoot.DescendantNodes().OfType<VbSyntax.StatementSyntax>()
				.Where(statement => statement.DescendantNodesAndSelf().OfType<VbSyntax.IdentifierNameSyntax>()
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
		var statements = root.DescendantNodes().OfType<StatementSyntax>()
			.Where(statement => statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
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
		designSurface = new DesignSurface();
		designSurface.BeginLoad(new SnapshotDesignerLoader(snapshot, ResolveProjectType));
		if (!designSurface.IsLoaded) {
			var errors = designSurface.LoadErrors?.Cast<object>().Select(item => item?.ToString()).Where(item => !String.IsNullOrEmpty(item));
			var errStr = String.Join(" | ", errors ?? []);
			throw new InvalidOperationException("The child design surface failed to load: " + errStr);
		}
		if (designSurface.View is Control view) {
			view.CreateControl();
			view.PerformLayout();
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
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var rootControl = host?.RootComponent as Control;
		var render = Render(rootControl, rootDesignSize);
		return new DesignerSessionState {
			SessionId = current?.SessionId ?? sessionId ?? "",
			DocumentId = current?.DocumentId ?? "",
			Version = baseVersion,
			Accepted = true,
			RootType = host?.RootComponent?.GetType().FullName ?? "",
			ComponentCount = host?.Container?.Components.Count ?? 0,
			Render = render,
			Tree = rootControl == null ? null : BuildElementTree(rootControl, ""),
			Components = host?.Container?.Components.Cast<IComponent>().Select(component => {
				var properties = DescribeProperties(component);
				if (component == host.RootComponent && rootAutoScaleDimensions.HasValue) {
					var scale = properties.FirstOrDefault(item => item.Name == "AutoScaleDimensions");
					if (scale != null)
						scale.Value = $"{rootAutoScaleDimensions.Value.Width.ToString(CultureInfo.InvariantCulture)}, {rootAutoScaleDimensions.Value.Height.ToString(CultureInfo.InvariantCulture)}";
				}
				return new DesignerComponentInfo {
				Name = component.Site?.Name ?? "",
				Type = component.GetType().FullName ?? component.GetType().Name,
				Parent = component is Control control ? control.Parent?.Site?.Name ?? "" : "",
				Text = component is Control textControl ? textControl.Text ?? "" : "",
				AccessibleName = PropertyText(component, "AccessibleName") is { Length: > 0 } accessibleName
					? accessibleName : component is Control namedControl && !String.IsNullOrEmpty(namedControl.Text)
						? namedControl.Text : component.Site?.Name ?? "",
				AccessibleDescription = PropertyText(component, "AccessibleDescription"),
				// "Default" is AccessibleRole's own sentinel for "no explicit role assigned" - the
				// effective role is only resolved later by the control's AccessibleObject - so it
				// must fall through to the type name exactly like an empty value does. Microsoft
				// WinForms reports that sentinel for an untouched Button (LibreWinForms reports
				// nothing at all), which is why leaving it in surfaced a bogus role of "Default"
				// instead of "Button" there and nowhere else.
				AccessibleRole = PropertyText(component, "AccessibleRole") is { Length: > 0 } accessibleRole
					&& accessibleRole != "Default"
					? accessibleRole : component.GetType().Name,
				X = component is Control boundsControl ? boundsControl.Left : 0,
				Y = component is Control boundsControl2 ? boundsControl2.Top : 0,
				SurfaceX = component is Control surfaceControl ? SurfaceLocation(surfaceControl).X : 0,
				SurfaceY = component is Control surfaceControl2 ? SurfaceLocation(surfaceControl2).Y : 0,
				// Microsoft Form.DrawToBitmap includes the non-client frame (caption and borders),
				// unlike the portable painter which renders only the client design surface. Keep
				// the root metadata in the same coordinate space as the returned bitmap.
				Width = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
					? (component as Control)?.Width ?? rootDesignSize.Value.Width
#else
					? rootDesignSize.Value.Width
#endif
					: component is Control sizeControl ? sizeControl.Width : 0,
				Height = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
					? (component as Control)?.Height ?? rootDesignSize.Value.Height
#else
					? rootDesignSize.Value.Height
#endif
					: component is Control sizeControl2 ? sizeControl2.Height : 0,
				Properties = properties,
				Events = DescribeEvents(component)
				};
			}).ToList() ?? []
		};
	}

	static string PropertyText(IComponent component, string propertyName)
	{
		try {
			var property = TypeDescriptor.GetProperties(component)[propertyName];
			var value = property?.GetValue(component);
			return value == null ? "" : property!.Converter.ConvertToInvariantString(value) ?? "";
		} catch { return ""; }
	}

	/// <summary>Builds the element tree for the Document Outline pad (the protocol's
	/// <c>Tree</c> shape), mirroring the flat <c>Components</c> list's control hierarchy.</summary>
	static DesignerElementNode BuildElementTree(Control control, string path)
	{
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
			Children = control.Controls.Cast<Control>()
				.Select((child, index) => BuildElementTree(child, path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "," + index.ToString(CultureInfo.InvariantCulture)))
				.ToList()
		};
	}

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
			object? value;
			string serialized;
			try {
				value = property.GetValue(component);
				if (value == null) serialized = "";
				else if (value is Image) serialized = "[binary]";
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
				IsNull = value == null,
				IsReadOnly = property.IsReadOnly || (!property.Converter.CanConvertFrom(typeof(string))
					&& property.PropertyType != typeof(Padding) && property.PropertyType != typeof(Font)
					&& property.PropertyType != typeof(SizeF)),
				// The source assignment is authoritative. Some LibreWinForms
				// descriptors keep returning true after ResetValue.
				ShouldSerialize = IsVisualBasic
					? vbDesignerRoot!.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>().Any(assignment =>
						NormalizeTarget(assignment.Left.ToString()) == elementId + "." + property.Name)
					: designerRoot!.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
						assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
						&& NormalizeTarget(assignment.Left.ToString()) == elementId + "." + property.Name),
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
	Point SurfaceLocation(Control control)
	{
		var root = (designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost)?.RootComponent as Control;
		if (control == root) return Point.Empty;
		var point = control.Location;
		for (var parent = control.Parent; parent != null && parent != root; parent = parent.Parent)
			point.Offset(parent.Location);
		// Native Form.DrawToBitmap paints the outer window. Child Locations are client-space,
		// so translate them into bitmap coordinates before the parent draws selection adorners.
#if MICROSOFT_WINFORMS
		if (root is Form form) {
			var border = Math.Max(0, (form.Width - form.ClientSize.Width) / 2);
			point.Offset(border, Math.Max(border, form.Height - form.ClientSize.Height - border));
		}
#endif
		return point;
	}

	DesignerRenderFrame? Render(Control? root, Size? designSize)
	{
		if (root == null) return null;
		if (root.Width <= 0 || root.Height <= 0) root.Size = new Size(300, 200);
		root.CreateControl();
		root.PerformLayout();
		var renderSize = designSize ?? root.Size;
#if MICROSOFT_WINFORMS
		// Do not crop the non-client frame that DrawToBitmap actually paints. Its dimensions
		// must match the root selection rectangle and the child SurfaceLocation offsets above.
		if (root is Form)
			renderSize = root.Size;
#endif
		using var bitmap = new Bitmap(Math.Max(1, renderSize.Width), Math.Max(1, renderSize.Height));
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
		using var stream = new MemoryStream();
		bitmap.Save(stream, ImageFormat.Png);
		return new DesignerRenderFrame {
			Sequence = Interlocked.Increment(ref frameSequence),
			Width = bitmap.Width,
			Height = bitmap.Height,
			// The portable renderer paints in WinForms logical pixels. ProGPU's
			// Bitmap does not expose a device resolution on macOS.
			Dpi = 1,
			PngBase64 = Convert.ToBase64String(stream.ToArray())
		};
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
