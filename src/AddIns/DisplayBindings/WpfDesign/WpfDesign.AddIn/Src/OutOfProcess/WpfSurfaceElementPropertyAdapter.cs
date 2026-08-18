#nullable enable
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WpfDesign.SurfaceHost;

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
	public sealed class WpfSurfaceElementPropertyAdapter : ICustomTypeDescriptor
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
			onStateChanged(state);
		}

		public string GetClassName() => node.Type;
		public string GetComponentName() => node.Name ?? node.Path;
		public TypeConverter? GetConverter() => null;
		public EventDescriptor? GetDefaultEvent() => null;
		public PropertyDescriptor? GetDefaultProperty() => null;
		public object? GetEditor(Type editorBaseType) => null;
		public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
		public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;
		public AttributeCollection GetAttributes() => AttributeCollection.Empty;
		public object GetPropertyOwner(PropertyDescriptor pd) => this;

		public PropertyDescriptorCollection GetProperties() =>
			new(node.Properties.Select(p => (PropertyDescriptor)new WpfSurfacePropertyDescriptor(this, p)).ToArray(), true);

		public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
	}

	sealed class WpfSurfacePropertyDescriptor : PropertyDescriptor
	{
		readonly WpfSurfaceElementPropertyAdapter owner;
		readonly DesignerPropertyInfo property;
		readonly Type propertyType;

		public WpfSurfacePropertyDescriptor(WpfSurfaceElementPropertyAdapter owner, DesignerPropertyInfo property)
			: base(property.Name, new Attribute[] {
				new CategoryAttribute(string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category),
				new DescriptionAttribute(property.Description ?? ""),
				new ReadOnlyAttribute(property.IsReadOnly)
			})
		{
			this.owner = owner;
			this.property = property;
			propertyType = property.Kind switch {
				"Boolean" => typeof(bool),
				"Number" => typeof(double),
				_ => typeof(string)
			};
		}

		public override Type ComponentType => typeof(WpfSurfaceElementPropertyAdapter);
		public override string DisplayName => string.IsNullOrEmpty(property.DisplayName) ? property.Name : property.DisplayName;
		public override bool IsReadOnly => property.IsReadOnly || property.Kind == "Unsupported";
		public override Type PropertyType => propertyType;
		public override bool CanResetValue(object component) => false;
		public override void ResetValue(object component) { }
		public override bool ShouldSerializeValue(object component) => property.ShouldSerialize;

		public override object? GetValue(object component)
		{
			if (property.IsNull)
				return null;
			if (propertyType == typeof(string))
				return property.Value;
			try
			{
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
			var serialized = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
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
}
