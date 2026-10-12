using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class EventHandlerSignatureTests
{
	[Theory]
	[InlineData("System.Windows.RoutedEventHandler", "global::System.Windows.RoutedEventArgs")]
	[InlineData("System.Windows.Input.MouseButtonEventHandler", "global::System.Windows.Input.MouseButtonEventArgs")]
	[InlineData("System.Windows.Controls.SelectionChangedEventHandler", "global::System.Windows.Controls.SelectionChangedEventArgs")]
	[InlineData("System.Windows.DependencyPropertyChangedEventHandler", "global::System.Windows.DependencyPropertyChangedEventArgs")]
	[InlineData("System.EventHandler", "global::System.EventArgs")]
	[InlineData("System.EventHandler`1[[System.Windows.Data.DataTransferEventArgs, PresentationFramework, Version=10.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35]]",
		"global::System.Windows.Data.DataTransferEventArgs")]
	[InlineData("System.Windows.RoutedPropertyChangedEventHandler`1[[System.Double, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]",
		"global::System.Windows.RoutedPropertyChangedEventArgs<global::System.Double>")]
	public void FollowsTheEventHandlerToEventArgsConvention(string delegateType, string expected)
		=> Assert.Equal(expected, EventHandlerSignature.EventArgsTypeName(delegateType));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("System.Action")]
	[InlineData("EventHandler")]
	[InlineData("Some.Outer+NestedEventHandler")]
	[InlineData("System.Collections.Generic.Dictionary`2[[System.String, x],[System.Int32, y]]")]
	public void FallsBackToEventArgsWhenTheDelegateIsNotConventional(string? delegateType)
		=> Assert.Equal(EventHandlerSignature.Fallback, EventHandlerSignature.EventArgsTypeName(delegateType));
}
