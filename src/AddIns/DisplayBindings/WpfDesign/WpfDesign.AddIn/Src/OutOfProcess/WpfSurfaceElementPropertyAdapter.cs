#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

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
	/// (blocking - <see cref="PropertyDescriptor.SetValue"/> is inherently synchronous, matching
	/// <c>FormsDesignerViewContent.ExecuteRemoteEdit</c>'s established blocking-edit pattern in
	/// this codebase) and the result is handed back through <paramref name="onStateChanged"/> so
	/// the caller can refresh the surface/tree the same way a mutation RPC always does.
	/// </summary>
	public sealed class WpfSurfaceElementPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
	{
		readonly WpfSurfaceHostClient client;
		readonly Func<long> currentBaseVersion;
		readonly DesignerElementNode node;
		readonly Action<DesignerSessionState> onStateChanged;

		public WpfSurfaceElementPropertyAdapter(WpfSurfaceHostClient client, Func<long> currentBaseVersion,
			DesignerElementNode node, Action<DesignerSessionState> onStateChanged)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			this.currentBaseVersion = currentBaseVersion ?? throw new ArgumentNullException(nameof(currentBaseVersion));
			this.node = node ?? throw new ArgumentNullException(nameof(node));
			this.onStateChanged = onStateChanged ?? throw new ArgumentNullException(nameof(onStateChanged));
		}

		internal void SetProperty(string propertyName, string value)
		{
			var state = client.SetPropertyAsync(currentBaseVersion(), node.Id, propertyName, value)
				.GetAwaiter().GetResult();
			EnsureAccepted(state);
			onStateChanged(state);
		}

		internal void ResetProperty(string propertyName)
		{
			var state = client.ResetPropertyAsync(currentBaseVersion(), node.Id, propertyName)
				.GetAwaiter().GetResult();
			EnsureAccepted(state);
			onStateChanged(state);
		}

		internal void SetEvent(string eventName, string handlerName)
		{
			var state = client.SetEventAsync(currentBaseVersion(), node.Id, eventName, handlerName)
				.GetAwaiter().GetResult();
			EnsureAccepted(state);
			onStateChanged(state);
		}

		// A rejected DDP mutation is not an edit.  In particular, do not send it through
		// OnPropertyEdited: that path marks the document dirty and refreshes the pad from a
		// state which the host explicitly says was not applied.  Forms follows the same rule
		// through ExecuteRemoteEdit.
		static void EnsureAccepted(DesignerSessionState state)
		{
			if (!state.Accepted)
				throw new InvalidOperationException(string.IsNullOrWhiteSpace(state.Error)
					? "The WPF designer rejected the edit."
					: state.Error);
		}

		string IPropertyGridEventSource.GetEventHandler(string eventName)
			=> node.Events.FirstOrDefault(@event => @event.Name == eventName)?.Handler ?? "";

		void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
		{
			var @event = node.Events.FirstOrDefault(candidate => candidate.Name == eventName);
			if (@event == null || @event.Handler == (handlerName ?? ""))
				return;
			SetEvent(eventName, handlerName ?? "");
			@event.Handler = handlerName ?? "";
		}

		// Double-click follows the common designer convention by creating the attribute reference.
		// Method generation is deliberately not attempted here: code-behind edits need their own
		// transactional source contract rather than a child-host side effect.
		void IEventBindingHost.BindEvent(string eventName)
		{
			if (!string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName)))
				return;
			((IPropertyGridEventSource)this).SetEventHandler(eventName,
				(node.Name ?? node.Type) + "_" + eventName);
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
		owner.SetEvent(@event.Name, handlerName);
		@event.Handler = handlerName;
	}
}

sealed class WpfSurfacePropertyDescriptor : PropertyDescriptor
	{
		readonly WpfSurfaceElementPropertyAdapter owner;
		readonly DesignerPropertyInfo property;
		readonly Type propertyType;

		public WpfSurfacePropertyDescriptor(WpfSurfaceElementPropertyAdapter owner, DesignerPropertyInfo property)
			: base(property.Name, CreateAttributes(property))
		{
			this.owner = owner;
			this.property = property;
			propertyType = property.Kind switch {
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
			return attributes.ToArray();
		}

		// Kind is the cross-process edit contract.  Do not turn a nested DesignItem or an
		// opaque diagnostic display value into an editable string if a child omitted the
		// redundant IsReadOnly flag; Forms applies the same defensive rule.
		static bool IsProtocolReadOnly(DesignerPropertyInfo property)
			=> property.IsReadOnly || property.Kind is "Unsupported" or "Reference" or "ReadOnly";

		internal System.Collections.Generic.IReadOnlyList<string> AllowedValues => property.AllowedValues;

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
			owner.SetProperty(property.Name, serialized);
			// The WPF TwoWay Value binding re-reads GetValue() right after a successful commit to
			// refresh its target - but property (this DesignerPropertyInfo) is the snapshot
			// captured when this descriptor was built, at SELECTION time, not a live view of the
			// child's document. Without updating it here, that immediate re-read reports the
			// PRE-edit value (a real, observed bug: WaitForPropertiesPadEditAsync's own "after"
			// read back the unedited original instead of the just-set value) until the next full
			// selection rebuilds a fresh adapter. Setting it directly is correct here specifically
			// because the RPC above already succeeded (an exception would have thrown out of this
			// method before reaching this line, leaving the stale snapshot in place, which is right).
			property.Value = serialized;
			property.IsNull = false;
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
