using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace FSharpWpfFixture.Lib
{
    // Types the F# project's XAML uses: the XAML tool sees only referenced assemblies, so what XAML names has to live in one.
    public class Person
    {
        public string Name { get; set; } = "Ada";

        public int Age { get; set; } = 36;
    }

    public class UpperConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString()?.ToUpperInvariant();

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
    }

    public class HelloExtension : MarkupExtension
    {
        public HelloExtension(string who) => Who = who;

        public string Who { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => "Hello, " + Who;
    }
}
