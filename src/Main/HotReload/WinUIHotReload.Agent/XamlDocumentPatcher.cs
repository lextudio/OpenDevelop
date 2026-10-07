using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace WinUIHotReload;

/// <summary>
/// Applies an edited XAML document to the running application in the smallest way that is still
/// correct:
/// <list type="bullet">
/// <item>An attribute change on an element that is live gets a property patch through XAML
/// Diagnostics; nothing else in the tree is touched.</item>
/// <item>A change to an element's child list makes that element a <em>region</em>: its children
/// are rebuilt from the new markup with WinUI's own XamlReader and swapped into its content
/// property. State inside a region (focus, text typed into a TextBox) is lost.</item>
/// <item>Anything only generated code could honour - x:Class, events, x:Bind - is answered with
/// "restart required" rather than applied halfway or reported as a false success.</item>
/// </list>
/// Each file's last accepted document is kept with every element annotated with the live objects
/// it maps to, so later edits keep finding them even after line numbers moved or regions were
/// rebuilt.
/// </summary>
internal sealed class XamlDocumentPatcher
{
	const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>Namespaces whose attributes never reach the running application.</summary>
	static readonly HashSet<string> DesignTimeNamespaces = new(StringComparer.Ordinal) {
		"http://schemas.microsoft.com/expression/blend/2008",
		"http://schemas.openxmlformats.org/markup-compatibility/2006",
	};

	/// <summary>What one element of an accepted document is in the running application.</summary>
	sealed class LiveAnnotation
	{
		/// <summary>Live instances; several when the same markup is shown more than once.</summary>
		public List<ulong> Handles = new();

		/// <summary>Built by a region reload, so it has no XAML source info and no handle of its own.</summary>
		public bool Dynamic;

		/// <summary>Its children were rebuilt from markup; a later edit inside is applied the same way.</summary>
		public bool RegionOwner;
	}

	sealed class RefusedException : Exception
	{
		public RefusedException(string message) : base(message) { }
	}

	readonly Dictionary<string, XDocument> accepted = new(StringComparer.OrdinalIgnoreCase);
	readonly object gate = new();

	public string Apply(TapInterop tap, string filePath, string? previousText, string currentText)
	{
		lock (gate) {
			try {
				return ApplyCore(tap, filePath, previousText, currentText);
			} catch (RefusedException ex) {
				return "error: restart required: " + ex.Message;
			}
		}
	}

	string ApplyCore(TapInterop tap, string filePath, string? previousText, string currentText)
	{
		XDocument after;
		try {
			after = XDocument.Parse(currentText, LoadOptions.SetLineInfo);
		} catch (XmlException ex) {
			// Not a restart: the user is mid-edit. The IDE keeps the last accepted text.
			return $"error: the XAML does not parse ({ex.Message})";
		}

		if (!accepted.TryGetValue(filePath, out var before)) {
			if (string.IsNullOrEmpty(previousText))
				throw new RefusedException("no previous text to diff against");
			try {
				// The first text the IDE offers for a file is what is on disk - what the application
				// was built from - so its positions are the ones WinUI recorded as SrcInfo.
				before = XDocument.Parse(previousText, LoadOptions.SetLineInfo);
			} catch (XmlException ex) {
				return $"error: the previous XAML does not parse ({ex.Message})";
			}
			AnnotateFromSourceInfo(before, tap.GetElements().Where(e => SameFile(e.File, filePath)).ToList());
		}

		var plan = new Plan();
		if (before.Root!.Name != after.Root!.Name)
			throw new RefusedException("the root element changed");
		Diff(before.Root, after.Root, plan, filePath);

		var regions = plan.Regions.Where(r => !plan.Regions.Any(other => other != r && r.Owner.Ancestors().Contains(other.Owner))).ToList();
		if (plan.Patches.Count == 0 && regions.Count == 0) {
			accepted[filePath] = after;
			return "ok: no changes to apply";
		}

		// Refuse before changing anything, so a refused edit never leaves the UI half-applied.
		foreach (var child in regions.SelectMany(r => r.Children))
			EnsureLoadableAtRunTime(child);

		var failures = new List<string>();
		foreach (var patch in plan.Patches) {
			foreach (var handle in patch.Handles) {
				var element = tap.GetElements().FirstOrDefault(e => e.Handle == handle);
				var property = WinUIPropertyResolver.Resolve(element?.Type ?? "", patch.Name);
				if (property.IsEvent)
					throw new RefusedException($"'{patch.Name}' is an event; handlers need a rebuild");
				var hr = tap.SetProperty(handle, property.FullName, property.WinRtValueType, patch.Value);
				if (hr < 0)
					failures.Add($"{property.FullName}=\"{patch.Value}\" (hr=0x{hr:X8})");
			}
		}

		foreach (var region in regions) {
			try {
				ReloadRegion(tap, region);
			} catch (RefusedException) {
				throw;
			} catch (Exception ex) {
				// XamlReader rejects what only compiled XAML supports (events, x:Bind, x:Class);
				// those need a rebuild, everything else is reported as it is.
				var message = $"{ex.GetType().Name}: {ex.Message}";
				if (ex.GetType().Name.Contains("XamlParse", StringComparison.Ordinal))
					throw new RefusedException($"the new markup for <{region.Owner.Name.LocalName}> cannot be loaded at run time ({message})");
				failures.Add($"<{region.Owner.Name.LocalName}> children: {message}");
			}
		}

		if (failures.Count > 0)
			return "error: could not apply " + string.Join(", ", failures);

		accepted[filePath] = after;
		var parts = new List<string>();
		if (plan.Patches.Count > 0)
			parts.Add($"{plan.Patches.Sum(p => p.Handles.Count)} property update(s)");
		if (regions.Count > 0)
			parts.Add($"{regions.Count} subtree(s) rebuilt");
		return "ok: " + string.Join(", ", parts);
	}

	sealed class Plan
	{
		public List<(List<ulong> Handles, string Name, string Value)> Patches = new();
		public List<Region> Regions = new();
	}

	/// <summary>Rebuild <see cref="Children"/> into <see cref="Property"/> of every live <see cref="Owner"/>.</summary>
	sealed record Region(XElement Owner, string? Property, List<XElement> Children);

	void Diff(XElement before, XElement after, Plan plan, string filePath)
	{
		var annotation = before.Annotation<LiveAnnotation>() ?? new LiveAnnotation();
		after.AddAnnotation(annotation);

		var changes = ChangedAttributes(before, after);
		if (changes.Count > 0) {
			if (IsPropertyElement(after))
				throw new RefusedException($"attributes on the property element <{after.Name.LocalName}> changed");
			if (annotation.Dynamic) {
				// Built by an earlier region reload: it has no handle, so rebuild its region again.
				AddRegion(plan, NearestRegionOwner(after));
			} else if (annotation.Handles.Count == 0) {
				throw new InvalidOperationException(
					$"no live element for <{after.Name.LocalName}> at line {((IXmlLineInfo)before).LineNumber} of {Path.GetFileName(filePath)} (is it on screen?)");
			} else {
				foreach (var (name, value) in changes)
					plan.Patches.Add((annotation.Handles, name, value));
			}
		}

		var oldChildren = before.Elements().ToList();
		var newChildren = after.Elements().ToList();
		if (!oldChildren.Select(e => e.Name).SequenceEqual(newChildren.Select(e => e.Name))) {
			// The child list itself changed: this element is where the subtree is rebuilt. Its new
			// descendants carry over nothing, because none of them is the old object any more.
			foreach (var descendant in after.Descendants())
				descendant.AddAnnotation(new LiveAnnotation { Dynamic = true });
			AddRegion(plan, after);
			return;
		}
		for (var i = 0; i < newChildren.Count; i++)
			Diff(oldChildren[i], newChildren[i], plan, filePath);
	}

	static void AddRegion(Plan plan, XElement changed)
	{
		// A property element (<Grid.RowDefinitions>) is rebuilt into that property of its parent;
		// an object element's children go into its content property.
		Region region;
		if (IsPropertyElement(changed)) {
			var owner = changed.Parent ?? throw new RefusedException("a property element has no owner");
			region = new Region(owner, changed.Name.LocalName.Substring(changed.Name.LocalName.IndexOf('.') + 1), changed.Elements().ToList());
		} else {
			region = new Region(changed, null, changed.Elements().Where(e => !IsPropertyElement(e)).ToList());
		}

		var ownerAnnotation = region.Owner.Annotation<LiveAnnotation>();
		if (ownerAnnotation == null || ownerAnnotation.Handles.Count == 0) {
			// The owner is itself rebuilt markup (no handle of its own): widen to its region.
			if (ownerAnnotation?.Dynamic == true) {
				AddRegion(plan, NearestRegionOwner(region.Owner));
				return;
			}
			throw new RefusedException($"<{region.Owner.Name.LocalName}> is not live, so its children cannot be rebuilt in place");
		}
		ownerAnnotation.RegionOwner = true;
		if (!plan.Regions.Any(r => r.Owner == region.Owner && r.Property == region.Property))
			plan.Regions.Add(region);
	}

	static XElement NearestRegionOwner(XElement element)
	{
		foreach (var ancestor in element.Ancestors())
			if (ancestor.Annotation<LiveAnnotation>() is { RegionOwner: true, Handles.Count: > 0 })
				return ancestor;
		throw new RefusedException($"<{element.Name.LocalName}> was rebuilt earlier and its region could not be found");
	}

	/// <summary>x: directives XamlReader cannot honour, because they live in generated code.</summary>
	static readonly HashSet<string> CompiledOnlyDirectives = new(StringComparer.Ordinal) {
		"Class", "Load", "Phase", "DeferLoadStrategy", "FieldModifier", "ConnectionId",
	};

	/// <summary>
	/// XamlReader accepts some markup it cannot actually honour - an event attribute loads
	/// without error and leaves a button that does nothing - so check before loading.
	/// </summary>
	static void EnsureLoadableAtRunTime(XElement fragmentRoot)
	{
		foreach (var element in fragmentRoot.DescendantsAndSelf().Where(e => !IsPropertyElement(e))) {
			var type = WinUIPropertyResolver.ResolveElementType(element.Name);
			foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) {
				var name = attribute.Name;
				if (name.NamespaceName == XamlLanguageNamespace && CompiledOnlyDirectives.Contains(name.LocalName))
					throw new RefusedException($"x:{name.LocalName} on <{element.Name.LocalName}> needs generated code");
				if (attribute.Value.Contains("{x:Bind", StringComparison.Ordinal))
					throw new RefusedException($"{{x:Bind}} on <{element.Name.LocalName}> is compiled into generated code");
				if (type != null && name.NamespaceName.Length == 0 && !name.LocalName.Contains('.')
					&& WinUIPropertyResolver.Resolve(type.FullName!, name.LocalName).IsEvent)
					throw new RefusedException($"'{name.LocalName}' on <{element.Name.LocalName}> is an event; handlers need a rebuild");
			}
		}
	}

	static void ReloadRegion(TapInterop tap, Region region)
	{
		var handles = region.Owner.Annotation<LiveAnnotation>()!.Handles;
		var fragments = region.Children.Select(Fragment).ToList();
		foreach (var handle in handles) {
			tap.RunOnUiThread(() => {
				var owner = tap.GetObject(handle);
				var property = region.Property != null
					? owner.GetType().GetProperty(region.Property, BindingFlags.Public | BindingFlags.Instance)
					: WinRTBridge.ContentProperty(owner);
				if (property == null)
					throw new RefusedException($"{owner.GetType().Name} has no {(region.Property ?? "content")} property this agent can rebuild");
				// Load everything before touching the live tree, so a markup error leaves it intact.
				var children = fragments.Select(WinRTBridge.LoadXaml).ToList();
				WinRTBridge.ReplaceContent(owner, property, children);
				return true;
			});
		}
	}

	/// <summary>
	/// One child as standalone XAML: the element plus every namespace declaration in scope where it
	/// sat, so prefixes (x:, local:, using:) resolve exactly as they did in the document.
	/// </summary>
	static string Fragment(XElement element)
	{
		var copy = new XElement(element);
		var declared = new HashSet<XName>(copy.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Name));
		foreach (var ancestor in element.Ancestors()) {
			foreach (var declaration in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration)) {
				if (declared.Add(declaration.Name))
					copy.Add(new XAttribute(declaration.Name, declaration.Value));
			}
		}
		return copy.ToString(SaveOptions.DisableFormatting);
	}

	/// <summary>
	/// Maps every element of the compiled document to its live instances by XAML source position.
	/// Line alone decides unless several elements share a line; then the column does.
	/// </summary>
	static void AnnotateFromSourceInfo(XDocument document, List<LiveElement> live)
	{
		var elements = document.Descendants().ToList();
		foreach (var element in elements) {
			var info = (IXmlLineInfo)element;
			var onLine = live.Where(e => e.Line == info.LineNumber).ToList();
			if (elements.Count(other => ((IXmlLineInfo)other).LineNumber == info.LineNumber) > 1)
				// XmlReader reports the column of the element name; SrcInfo has been seen to point
				// either there or at the '<' before it.
				onLine = onLine.Where(e => e.Column == info.LinePosition || e.Column == info.LinePosition - 1).ToList();
			element.AddAnnotation(new LiveAnnotation { Handles = onLine.Select(e => e.Handle).ToList() });
		}
	}

	static bool IsPropertyElement(XElement element) => element.Name.LocalName.Contains('.');

	/// <summary>Attributes added or changed on one element; refuses what a property patch cannot do.</summary>
	static List<(string Name, string Value)> ChangedAttributes(XElement before, XElement after)
	{
		var result = new List<(string, string)>();
		var old = before.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name, a => a.Value);
		var current = after.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name, a => a.Value);

		foreach (var name in old.Keys.Except(current.Keys)) {
			if (!DesignTimeNamespaces.Contains(name.NamespaceName))
				throw new RefusedException($"removing '{name.LocalName}' is not supported yet");
		}

		foreach (var (name, value) in current) {
			if (old.TryGetValue(name, out var previous) && previous == value)
				continue;
			if (DesignTimeNamespaces.Contains(name.NamespaceName))
				continue;
			if (name.NamespaceName == XamlLanguageNamespace)
				throw new RefusedException($"x:{name.LocalName} is compiled into generated code");
			if (name.NamespaceName.Length != 0 || name.LocalName.Contains('.'))
				throw new RefusedException($"'{name.LocalName}' is an attached property, which is not supported yet");
			if (value.StartsWith("{", StringComparison.Ordinal) && !value.StartsWith("{}", StringComparison.Ordinal))
				throw new RefusedException($"'{name.LocalName}' uses a markup extension ({value})");
			result.Add((name.LocalName, value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value));
		}
		return result;
	}

	/// <summary>
	/// SrcInfo names a file as an ms-appx URI relative to the project ("ms-appx:///Views/Main.xaml");
	/// the IDE has the absolute path. Match on the trailing path segments.
	/// </summary>
	static bool SameFile(string sourceUri, string filePath)
	{
		if (string.IsNullOrEmpty(sourceUri))
			return false;
		var relative = sourceUri;
		var scheme = relative.IndexOf(":///", StringComparison.Ordinal);
		if (scheme >= 0)
			relative = relative.Substring(scheme + 4);
		relative = relative.Replace('\\', '/').TrimStart('/');
		var path = filePath.Replace('\\', '/');
		return path.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase)
			|| path.Equals(relative, StringComparison.OrdinalIgnoreCase);
	}
}
