#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;

using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// Adapts a DDP <see cref="DesignerElementNode"/> (specifically its
	/// <see cref="DesignerElementNode.Properties"/> list, populated by the child's own
	/// <c>DesignItem</c> reflection - see <c>WpfSurfaceHostService.BuildProperties</c>) to the
	/// shared Properties pad, exactly the role <c>FormsDesignerViewContent.RemoteComponentPropertyProxy</c>
	/// plays for WinForms. Edits go out through <see cref="WpfSurfaceHostClient.SetPropertyAsync"/>
	/// through the surface-owned serialized edit queue. <see cref="PropertyDescriptor.SetValue"/>
	/// remains a synchronous framework callback, but it only submits the edit; it never waits for
	/// the remote host. The surface applies accepted state on its dispatcher.
	/// </summary>
	/// <summary>A double-clicked event that now names a handler. <see cref="XamlChanged"/> is true when
	/// this double-click wrote the attribute (and so pushed a designer undo entry) and false when it
	/// found the event already bound.</summary>
	public sealed record WpfEventBinding(DesignerEventInfo Event, bool XamlChanged);

	public sealed class WpfSurfaceElementPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost, IItemsCollectionEditorHost, ITreeItemsEditorHost, IBrushEditorHost, ITransformEditorHost, IDesignFrameSource, IImageSourceCandidates
	{
		readonly WpfSurfaceHostClient client;
		readonly DesignerElementNode node;
		readonly Func<Func<long, Task<DesignerSessionState>>, Task<WpfPropertyEditOutcome>> queueEdit;
		readonly Action<WpfEventBinding>? onEventHandlerBound;

		internal WpfSurfaceElementPropertyAdapter(WpfSurfaceHostClient client,
			DesignerElementNode node, Func<Func<long, Task<DesignerSessionState>>, Task<WpfPropertyEditOutcome>> queueEdit, Action<WpfEventBinding>? onEventHandlerBound = null)
		{
			this.onEventHandlerBound = onEventHandlerBound;
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			this.node = node ?? throw new ArgumentNullException(nameof(node));
			this.queueEdit = queueEdit ?? throw new ArgumentNullException(nameof(queueEdit));
		}

		/// <summary>The project images an <c>ImageSource</c> row offers, relative to the designed
		/// file (see <see cref="WpfImageSourceEditor"/>); null when the document has no project.</summary>
		public Func<System.Collections.Generic.IReadOnlyList<string>>? ImageSourceCandidates { get; init; }

		/// <summary>The designer's last rendered frame, for the brush editor's eyedropper.</summary>
		public Func<System.Windows.Media.Imaging.BitmapSource?>? DesignFrameProvider { get; init; }

		System.Windows.Media.Imaging.BitmapSource? IDesignFrameSource.DesignFrame => DesignFrameProvider?.Invoke();

		System.Collections.Generic.IReadOnlyList<string> IImageSourceCandidates.ImageSourceCandidates
			=> ImageSourceCandidates?.Invoke() ?? Array.Empty<string>();

		internal void SetProperty(string propertyName, string value)
			=> QueueEdit(version => client.SetPropertyAsync(version, node.Id, propertyName, value));

		internal Task<WpfPropertyEditOutcome> SetRichText(System.Collections.Generic.IReadOnlyList<DesignerRichTextRun> runs)
			=> QueueEdit(version => client.SetRichTextAsync(version, node.Id, runs));

		internal void ResetProperty(string propertyName)
			=> QueueEdit(version => client.ResetPropertyAsync(version, node.Id, propertyName));

		internal Task<WpfPropertyEditOutcome> SetEvent(string eventName, string handlerName)
			=> QueueEdit(version => client.SetEventAsync(version, node.Id, eventName, handlerName));

		internal void SetBindingPath(string propertyName, string path)
			=> QueueEdit(version => client.SetBindingPathAsync(version, node.Id, propertyName, path));

		internal void SetBindingChildPath(string propertyName, int childIndex, string path)
			=> QueueEdit(version => client.SetBindingChildPathAsync(version, node.Id, propertyName, childIndex, path));

		internal void SetBindingMode(string propertyName, string mode)
			=> QueueEdit(version => client.SetBindingModeAsync(version, node.Id, propertyName, mode));

		internal void SetBindingChildMode(string propertyName, int childIndex, string mode)
			=> QueueEdit(version => client.SetBindingModeAsync(version, node.Id, propertyName, mode, childIndex));

		internal void SetBindingUpdateSourceTrigger(string propertyName, string trigger)
			=> QueueEdit(version => client.SetBindingUpdateSourceTriggerAsync(version, node.Id, propertyName, trigger));

		internal void SetBindingChildUpdateSourceTrigger(string propertyName, int childIndex, string trigger)
			=> QueueEdit(version => client.SetBindingUpdateSourceTriggerAsync(version, node.Id, propertyName, trigger, childIndex));

		internal void SetBindingElementName(string propertyName, string elementName)
			=> QueueEdit(version => client.SetBindingElementNameAsync(version, node.Id, propertyName, elementName));

		internal void SetBindingChildElementName(string propertyName, int childIndex, string elementName)
			=> QueueEdit(version => client.SetBindingElementNameAsync(version, node.Id, propertyName, elementName, childIndex));

		internal void SetBindingRelativeSource(string propertyName, string relativeSource)
			=> QueueEdit(version => client.SetBindingRelativeSourceAsync(version, node.Id, propertyName, relativeSource));

		internal void SetBindingChildRelativeSource(string propertyName, int childIndex, string relativeSource)
			=> QueueEdit(version => client.SetBindingRelativeSourceAsync(version, node.Id, propertyName, relativeSource, childIndex));

		internal void SetBindingAncestorLevel(string propertyName, int level)
			=> QueueEdit(version => client.SetBindingAncestorLevelAsync(version, node.Id, propertyName, level));

		internal void SetBindingSource(string propertyName, string resourceKey)
			=> QueueEdit(version => client.SetBindingSourceAsync(version, node.Id, propertyName, resourceKey));

		internal void SetBindingChildSource(string propertyName, int childIndex, string resourceKey)
			=> QueueEdit(version => client.SetBindingSourceAsync(version, node.Id, propertyName, resourceKey, childIndex));

		internal void SetBindingNotifyOnValidationError(string propertyName, bool notify)
			=> QueueEdit(version => client.SetBindingNotifyOnValidationErrorAsync(version, node.Id, propertyName, notify));

		internal void SetBindingValidationOption(string propertyName, string optionName, bool enabled)
			=> QueueEdit(version => client.SetBindingValidationOptionAsync(version, node.Id, propertyName, optionName, enabled));

		internal void SetBindingChildValidationOption(string propertyName, int childIndex, string optionName, bool enabled)
			=> QueueEdit(version => client.SetBindingValidationOptionAsync(version, node.Id, propertyName, optionName, enabled, childIndex));

		internal void SetBindingTextOption(string propertyName, string optionName, string value)
			=> QueueEdit(version => client.SetBindingTextOptionAsync(version, node.Id, propertyName, optionName, value));

		internal void SetBindingChildTextOption(string propertyName, int childIndex, string optionName, string value)
			=> QueueEdit(version => client.SetBindingTextOptionAsync(version, node.Id, propertyName, optionName, value, childIndex));

		/// <summary>Commits an edited plain item list (<c>design/set-items</c>); the property is the
		/// host-published ItemsCollection row, so only this element's Items can be targeted.</summary>
		void IItemsCollectionEditorHost.SetItems(string propertyName, System.Collections.Generic.IReadOnlyList<DesignerCollectionItemEdit> items)
		{
			var property = node.Properties.SingleOrDefault(candidate => candidate.Name == propertyName
				&& candidate.EditorKind is "ItemsCollection" or "DataGridColumns" or "BindingCollection" or "GridViewColumns" or "StripItems");
			if (property == null)
				throw new InvalidOperationException("Not an editable items collection: " + propertyName);
			QueueEdit(version => property.EditorKind == "BindingCollection"
				? client.SetBindingItemsAsync(version, node.Id, property.BindingPropertyName, items.ToArray())
				: property.EditorKind == "GridViewColumns"
					? client.SetGridViewColumnsAsync(version, node.Id, items)
					: property.EditorKind == "StripItems"
						? client.SetStripItemsAsync(version, node.Id, items)
						: client.SetItemsAsync(version, node.Id, items));
		}

		void ITreeItemsEditorHost.SetTreeItems(string propertyName, System.Collections.Generic.IReadOnlyList<DesignerTreeItemEdit> items)
		{
			var editorKind = node.Properties.SingleOrDefault(property => property.Name == propertyName)?.EditorKind;
			if (editorKind is not ("TreeItems" or "MenuItems"))
				throw new InvalidOperationException("Not an editable tree items collection: " + propertyName);
			QueueEdit(version => editorKind == "MenuItems" ? client.SetMenuItemsAsync(version, node.Id, items) : client.SetTreeItemsAsync(version, node.Id, items));
		}

		/// <summary>Commits an edited brush (<c>design/set-brush</c>); only a host-published Brush
		/// editor row of this element can be targeted.</summary>
		void IBrushEditorHost.SetBrush(string propertyName, DesignerBrushInfo brush)
		{
			if (!node.Properties.Any(property => property.Name == propertyName && property.EditorKind == "Brush"))
				throw new InvalidOperationException("Not an editable brush: " + propertyName);
			QueueEdit(version => client.SetBrushAsync(version, node.Id, propertyName, brush));
		}

		/// <summary>Points a property at a resource, or clears the reference (<c>design/set-resource-reference</c>); only a
		/// host-published ResourceReference row of this element can be targeted.</summary>
		internal Task<WpfPropertyEditOutcome> SetResourceReference(string propertyName, string kind, string key)
		{
			if (!node.Properties.Any(property => property.Name == propertyName && property.EditorKind == "ResourceReference"))
				throw new InvalidOperationException("Not an editable resource reference: " + propertyName);
			return QueueEdit(version => client.SetResourceReferenceAsync(version, node.Id, propertyName, kind, key));
		}

		/// <summary>Commits an edited transform (<c>design/set-transform</c>); only a host-published Transform
		/// editor row of this element can be targeted.</summary>
		void ITransformEditorHost.SetTransform(string propertyName, System.Collections.Generic.IReadOnlyList<DesignerTransformOp> ops)
		{
			if (!node.Properties.Any(property => property.Name == propertyName && property.EditorKind == "Transform"))
				throw new InvalidOperationException("Not an editable transform: " + propertyName);
			QueueEdit(version => client.SetTransformAsync(version, node.Id, propertyName, ops));
		}

		internal void SetBindingConverter(string propertyName, string resourceKey)
			=> QueueEdit(version => client.SetBindingConverterAsync(version, node.Id, propertyName, resourceKey));

		internal void SetBindingChildConverter(string propertyName, int childIndex, string resourceKey)
			=> QueueEdit(version => client.SetBindingConverterAsync(version, node.Id, propertyName, resourceKey, childIndex));

		internal void SetBindingDelay(string propertyName, int delay)
			=> QueueEdit(version => client.SetBindingDelayAsync(version, node.Id, propertyName, delay));

		internal void SetBindingChildDelay(string propertyName, int childIndex, int delay)
			=> QueueEdit(version => client.SetBindingDelayAsync(version, node.Id, propertyName, delay, childIndex));

		Task<WpfPropertyEditOutcome> QueueEdit(Func<long, Task<DesignerSessionState>> edit) => queueEdit(edit);

		string IPropertyGridEventSource.GetEventHandler(string eventName)
			=> node.Events.FirstOrDefault(@event => @event.Name == eventName)?.Handler ?? "";

		void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
		{
			var @event = node.Events.FirstOrDefault(candidate => candidate.Name == eventName);
			if (@event == null || @event.Handler == (handlerName ?? ""))
				return;
			_ = SetEvent(eventName, handlerName ?? "");
		}

		internal string GetEventBindingHandler(string eventName) => (node.Name ?? node.Type) + "_" + eventName;

		// Double-click follows the in-process designer: bind the XAML attribute if the event has no
		// handler yet, then let the view create (or find) the method in the IDE-owned code-behind
		// and open it. The isolated host only ever writes the attribute; project code is the IDE's.
		void IEventBindingHost.BindEvent(string eventName)
		{
			var bindsXaml = string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName));
			Task<WpfPropertyEditOutcome>? pending = null;
			DesignerEventInfo? bound = node.Events.FirstOrDefault(candidate => candidate.Name == eventName);
			if (bindsXaml) {
				var handler = GetEventBindingHandler(eventName);
				pending = SetEvent(eventName, handler);
				if (bound != null)
					bound = new DesignerEventInfo { Name = bound.Name, Category = bound.Category, HandlerTypeName = bound.HandlerTypeName, Handler = handler };
			}
			if (bound is { Handler.Length: > 0 })
				NotifyEventHandlerBound(pending, new WpfEventBinding(bound, bindsXaml));
		}

		async void NotifyEventHandlerBound(Task<WpfPropertyEditOutcome>? pending, WpfEventBinding binding)
		{
			try {
				if (pending != null) {
					var outcome = await pending.ConfigureAwait(true);
					if (!outcome.Accepted) {
						LoggingService.Warn("WPF designer event binding rejected: " + (outcome.Error ?? "Unknown host error."));
						return;
					}
				}
				onEventHandlerBound?.Invoke(binding);
			} catch (Exception exception) {
				LoggingService.Error(exception);
			}
		}

		public string GetClassName() => node.Type;
		public string GetComponentName() => node.Name ?? node.Path;
		public TypeConverter? GetConverter() => null;
		public EventDescriptor? GetDefaultEvent() => null;
		public PropertyDescriptor? GetDefaultProperty() => null;
		public object? GetEditor(Type editorBaseType) => null;
		public EventDescriptorCollection GetEvents() => new(node.Events
			.Select(e => (EventDescriptor)new WpfSurfaceEventDescriptor(this, e)).ToArray(), true);
		public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents();
		public AttributeCollection GetAttributes() => AttributeCollection.Empty;
		public object GetPropertyOwner(PropertyDescriptor pd) => this;

		public PropertyDescriptorCollection GetProperties() =>
			new(node.Properties.Select(p => (PropertyDescriptor)new WpfSurfacePropertyDescriptor(this, p)).ToArray(), true);

		public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
}

sealed class WpfSurfaceEventDescriptor : EventDescriptor, IPropertyGridEventTypeName
{
	readonly WpfSurfaceElementPropertyAdapter owner;
	readonly DesignerEventInfo @event;

	public WpfSurfaceEventDescriptor(WpfSurfaceElementPropertyAdapter owner, DesignerEventInfo @event)
		: base(@event.Name, new Attribute[] { new CategoryAttribute(string.IsNullOrEmpty(@event.Category) ? "Events" : @event.Category) })
	{
		this.owner = owner;
		this.@event = @event;
	}

	public override string DisplayName => @event.Name;
	public override string Description => @event.HandlerTypeName;
	public override Type ComponentType => typeof(WpfSurfaceElementPropertyAdapter);
	public override Type EventType => typeof(EventHandler);
	public override bool IsMulticast => true;
	public string HandlerTypeName => string.IsNullOrEmpty(@event.HandlerTypeName) ? "EventHandler" : @event.HandlerTypeName;

	public override void AddEventHandler(object component, Delegate handler)
		=> SetHandlerName(handler?.Method.Name ?? "");

	public override void RemoveEventHandler(object component, Delegate handler)
		=> SetHandlerName("");

	void SetHandlerName(string handlerName)
	{
		_ = owner.SetEvent(@event.Name, handlerName);
	}
}

sealed class WpfSurfacePropertyDescriptor : PropertyDescriptor, IItemsCollectionProperty, IItemsCollectionPropertyName, ITreeItemsProperty, IBrushProperty, ITransformProperty
	{
		readonly WpfSurfaceElementPropertyAdapter owner;
		readonly DesignerPropertyInfo property;
		readonly Type propertyType;

		public WpfSurfacePropertyDescriptor(WpfSurfaceElementPropertyAdapter owner, DesignerPropertyInfo property)
			: base(ToBindingSafeName(property.Name), CreateAttributes(property))
		{
			this.owner = owner;
			this.property = property;
			// Binding.Delay is an integer millisecond contract. Keeping the generic Number mapping
			// would expose it as a double and silently round a fractional Properties-pad value on
			// the way to the typed child RPC.
			propertyType = property.EditorKind == "BindingDelay" ? typeof(int) : property.Kind switch {
				"Boolean" => typeof(bool),
				"Number" => typeof(double),
				"Color" => typeof(Color),
				"Brush" => typeof(Brush),
				"Uri" => typeof(Uri),
				"Point" => typeof(Point),
				"Size" => typeof(Size),
				"Rect" => typeof(Rect),
				"Thickness" => typeof(Thickness),
				_ => typeof(string)
			};
		}

		// Xceed binds each PropertyItem with new Binding(descriptor.Name). A protocol name such as
		// "Text.Mode" (a nested Binding editor), "Grid.Row", or an indexed composite-Binding
		// child such as "Text.Bindings[0].Mode" would otherwise be parsed as a property path
		// (including an indexer), resolve to nothing, and be omitted from the grid. The protocol
		// name stays intact in ProtocolName for RPCs and DevFlow lookups.
		static string ToBindingSafeName(string name) => name.Replace('.', '_').Replace('[', '_').Replace("]", "");

		internal string ProtocolName => property.Name;

		string IItemsCollectionPropertyName.PropertyName => ProtocolName;

		static Attribute[] CreateAttributes(DesignerPropertyInfo property)
		{
			var attributes = new System.Collections.Generic.List<Attribute> {
				new CategoryAttribute(string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category),
				new DescriptionAttribute(property.Description ?? ""),
				new ReadOnlyAttribute(IsProtocolReadOnly(property))
			};
			// A child cannot safely send its runtime enum Type across the process boundary.
			// Keep the wire value as text and use the protocol's finite choice list instead.
			// This is the same PropertyDescriptor pattern used by the GTK designer.
			if (property.Kind == "Enum" && property.AllowedValues.Count > 0)
				attributes.Add(new TypeConverterAttribute(typeof(WpfDesignerEnumConverter)));
			// The row stays read-only to generic editing; the "…" editor commits the whole list.
			if (property.EditorKind is "ItemsCollection" or "DataGridColumns" or "BindingCollection" or "GridViewColumns" or "StripItems")
				attributes.Add(new EditorAttribute(typeof(WpfItemsCollectionEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			else if (property.EditorKind is "TreeItems" or "MenuItems")
				attributes.Add(new EditorAttribute(typeof(WpfTreeItemsEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			else if (property.EditorKind == "RichText")
				attributes.Add(new EditorAttribute(typeof(WpfRichTextEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			// The brush editor replaces Xceed's own: it reaches gradients and "No brush", which a
			// text or color-only editor cannot express.
			else if (property.EditorKind == "Brush")
				attributes.Add(new EditorAttribute(typeof(WpfBrushEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			else if (property.EditorKind == "ResourceReference")
				attributes.Add(new EditorAttribute(typeof(WpfResourceReferenceEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			else if (property.EditorKind == "Transform")
				attributes.Add(new EditorAttribute(typeof(WpfTransformEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			else if (property.EditorKind == "ImageSource")
				attributes.Add(new EditorAttribute(typeof(WpfImageSourceEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor)));
			return attributes.ToArray();
		}

		// Kind is the cross-process edit contract.  Do not turn a nested DesignItem or an
		// opaque diagnostic display value into an editable string if a child omitted the
		// redundant IsReadOnly flag; Forms applies the same defensive rule.
		static bool IsProtocolReadOnly(DesignerPropertyInfo property)
			=> DesignerPropertySemantics.IsReadOnly(property);

		internal System.Collections.Generic.IReadOnlyList<string> AllowedValues => property.AllowedValues;
		internal System.Collections.Generic.IReadOnlyList<DesignerRichTextRun> RichTextRuns => property.RichTextRuns is { } runs ? runs : Array.Empty<DesignerRichTextRun>();

		System.Collections.Generic.IReadOnlyList<string> IItemsCollectionProperty.CollectionItems => property.CollectionItems;
		System.Collections.Generic.IReadOnlyList<string> IItemsCollectionProperty.CollectionItemKinds => property.CollectionItemKinds;
		bool IItemsCollectionProperty.SupportsDataGridColumnKinds => property.EditorKind == "DataGridColumns";
		System.Collections.Generic.IReadOnlyList<DesignerTreeItem> ITreeItemsProperty.TreeItems => property.TreeItems;

		internal System.Collections.Generic.List<string> ResourceReferenceKeys => property.ResourceReferenceKeys;

		System.Collections.Generic.IReadOnlyList<DesignerTransformOp> ITransformProperty.Transform => property.Transform ?? new System.Collections.Generic.List<DesignerTransformOp>();

		DesignerBrushInfo? IBrushProperty.Brush => property.Brush;

		System.Collections.Generic.IReadOnlyList<string> IBrushProperty.BrushResourceKeys => property.BrushResourceKeys;

		System.Collections.Generic.IReadOnlyList<string> IBrushProperty.ColorResourceKeys => property.ColorResourceKeys;

		public override Type ComponentType => typeof(WpfSurfaceElementPropertyAdapter);
		public override string DisplayName => string.IsNullOrEmpty(property.DisplayName) ? property.Name : property.DisplayName;
		public override bool IsReadOnly => IsProtocolReadOnly(property);
		public override Type PropertyType => propertyType;
		public override bool CanResetValue(object component) => property.ShouldSerialize && !IsReadOnly;
		public override void ResetValue(object component)
		{
			if (CanResetValue(component))
				owner.ResetProperty(property.Name);
		}
		public override bool ShouldSerializeValue(object component) => property.ShouldSerialize;

		public override object? GetValue(object component)
		{
			if (property.IsNull)
				return null;
			if (propertyType == typeof(string))
				return property.Value;
			try
			{
				var converter = TypeDescriptor.GetConverter(propertyType);
				if (converter.CanConvertFrom(typeof(string)))
					return converter.ConvertFromInvariantString(property.Value);
				return Convert.ChangeType(property.Value, propertyType, CultureInfo.InvariantCulture);
			}
			catch (Exception) when (propertyType != typeof(string))
			{
				// A value this backend reported as convertible but that doesn't actually parse
				// back (shouldn't happen - the child produced Value with the same converter it
				// would use to parse it) falls back to the raw text rather than throwing out of
				// the property grid's own binding.
				return property.Value;
			}
		}

		public override void SetValue(object component, object value)
		{
			if (IsReadOnly)
				return;
			var converter = TypeDescriptor.GetConverter(propertyType);
			var serialized = converter.CanConvertTo(typeof(string))
				? converter.ConvertToInvariantString(value) ?? ""
				: Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
			if (property.EditorKind == "BindingPath")
				owner.SetBindingPath(property.Name, serialized);
			else if (property.EditorKind == "BindingChildPath")
				owner.SetBindingChildPath(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingMode")
				owner.SetBindingMode(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildMode")
				owner.SetBindingChildMode(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingUpdateSourceTrigger")
				owner.SetBindingUpdateSourceTrigger(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildUpdateSourceTrigger")
				owner.SetBindingChildUpdateSourceTrigger(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingElementName")
				owner.SetBindingElementName(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildElementName")
				owner.SetBindingChildElementName(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingRelativeSource")
				owner.SetBindingRelativeSource(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildRelativeSource")
				owner.SetBindingChildRelativeSource(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingAncestorLevel")
				owner.SetBindingAncestorLevel(property.BindingPropertyName, Convert.ToInt32(value, CultureInfo.InvariantCulture));
			else if (property.EditorKind == "BindingSource")
				owner.SetBindingSource(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildSource")
				owner.SetBindingChildSource(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingNotifyOnValidationError")
				owner.SetBindingNotifyOnValidationError(property.BindingPropertyName, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
			else if (property.EditorKind == "BindingValidationOption")
				owner.SetBindingValidationOption(property.BindingPropertyName, property.BindingOptionName, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
			else if (property.EditorKind == "BindingChildValidationOption")
				owner.SetBindingChildValidationOption(property.BindingPropertyName, property.BindingChildIndex, property.BindingOptionName, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
			else if (property.EditorKind == "BindingTextOption")
				owner.SetBindingTextOption(property.BindingPropertyName, property.BindingOptionName, serialized);
			else if (property.EditorKind == "BindingChildTextOption")
				owner.SetBindingChildTextOption(property.BindingPropertyName, property.BindingChildIndex, property.BindingOptionName, serialized);
			else if (property.EditorKind == "BindingConverter")
				owner.SetBindingConverter(property.BindingPropertyName, serialized);
			else if (property.EditorKind == "BindingChildConverter")
				owner.SetBindingChildConverter(property.BindingPropertyName, property.BindingChildIndex, serialized);
			else if (property.EditorKind == "BindingDelay")
				owner.SetBindingDelay(property.BindingPropertyName, Convert.ToInt32(value, CultureInfo.InvariantCulture));
			else if (property.EditorKind == "BindingChildDelay")
				owner.SetBindingChildDelay(property.BindingPropertyName, property.BindingChildIndex, Convert.ToInt32(value, CultureInfo.InvariantCulture));
			else
				owner.SetProperty(property.Name, serialized);
			// Do not mutate the node snapshot optimistically: it remains the last accepted host
			// state, so a rejected queued edit can safely rebind the Properties pad from it.
			OnValueChanged(component, EventArgs.Empty);
		}
	}

	/// <summary>Supplies a native property-grid drop-down for a remote enum without requiring
	/// the IDE process to load the design assembly that declared it.</summary>
	sealed class WpfDesignerEnumConverter : StringConverter
	{
		public override bool GetStandardValuesSupported(ITypeDescriptorContext? context)
			=> context?.PropertyDescriptor is WpfSurfacePropertyDescriptor;

		public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context)
			=> context?.PropertyDescriptor is WpfSurfacePropertyDescriptor;

		public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
			=> new((context?.PropertyDescriptor as WpfSurfacePropertyDescriptor)?.AllowedValues.ToArray()
				?? Array.Empty<string>());
	}
}
