// XAML-facing assembly attributes for the Microsoft WPF build of ICSharpCode.WpfDesign.Designer.
//
// The upstream Configuration/AssemblyInfo.cs is excluded from the compile glob because it also
// carries identity/version attributes that the SDK generates itself (CS0579). The XAML-facing
// subset is NOT optional: the linked .xaml files resolve xmlns="http://sharpdevelop.net" through
// these XmlnsDefinition attributes. Keep in sync with the upstream AssemblyInfo.cs if its mappings
// change. The matching mapping owned by WpfDesign itself lives in CoreXamlAssemblyAttributes.cs.

using System.Windows;
using System.Windows.Markup;

// generic.xaml ships inside this same assembly, exactly as it does upstream.
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

[assembly: XmlnsPrefix("http://sharpdevelop.net", "sd")]

[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.Designer")]
[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.Designer.Controls")]
[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.Designer.PropertyGrid")]
[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.Designer.PropertyGrid.Editors")]
[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.Designer.ThumbnailView")]
