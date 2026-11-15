namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>Preview-only instruction retained while the loose-XAML parser materializes a
/// DataTemplate. The token is written to a host-only attached property in the parser copy of the
/// document and lets the host recover generated template instances without relying on x:Name scopes
/// or overwriting an AutomationId authored by the app.</summary>
internal sealed record TemplateXBindPlan(
	string Token,
	string TargetProperty,
	string Expression,
	string? BindBack,
	string? Mode,
	string? StaticType,
	string? Converter = null,
	string? ConverterParameter = null,
	string? ConverterLanguage = null,
	string? FallbackValue = null,
	string? TargetNullValue = null);
