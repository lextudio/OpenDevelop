using System;
using Microsoft.UI.Xaml.Data;
using XamlStudio.Toolkit.Services;

namespace XamlStudio.Toolkit.Converters;

/// <summary>ProGPU's WinUI projection of XAML Studio's binding observation shim. The preview
/// compiler resolves this type from the toolkit assembly injected into a binding-debug render.</summary>
public sealed class XamlBindingWrapperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => ConvertCore(value, targetType, parameter, language, convertBack: false);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => ConvertCore(value, targetType, parameter, language, convertBack: true);

    static object ConvertCore(object value, Type targetType, object parameter, string language, bool convertBack)
    {
        if (!int.TryParse(parameter?.ToString(), out var id)
            || !XamlBindingWrapperManager.Instance.TryGetValue(id, out var binding))
            throw new ArgumentException("The binding observation id is missing or unknown.", nameof(parameter));
        if (binding.Converter == null)
            return binding.NewValue(value);
        try
        {
            var converted = convertBack
                ? binding.Converter.ConvertBack(value, targetType, binding.ConverterParameter, language)
                : binding.Converter.Convert(value, targetType, binding.ConverterParameter, language);
            return binding.NewConversion(value, converted);
        }
        catch (Exception exception)
        {
            return binding.NewException(value, exception);
        }
    }
}
