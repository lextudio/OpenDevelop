// XAML-facing assembly attributes for the Microsoft WPF build of ICSharpCode.WpfDesign.
//
// The upstream WpfDesign/Project/Configuration/AssemblyInfo.cs is excluded from the compile glob
// because it also carries identity/version attributes that the SDK generates itself (CS0579).
// The XmlnsDefinition below is NOT optional though: PropertyGridView.xaml in the Designer project
// maps xmlns:PropertyGridBase="http://sharpdevelop.net" onto Category, and without this attribute
// the markup compiler fails with MC3066.

using System.Windows.Markup;

[assembly: XmlnsDefinition("http://sharpdevelop.net", "ICSharpCode.WpfDesign.PropertyGrid")]
