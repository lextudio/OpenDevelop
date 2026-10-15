namespace OpenDevelop.XamlDom.Tests;

/// <summary>Representative XAML the designer must load as faithfully as Microsoft's loader. Each entry is the
/// content of a StackPanel root that already declares the usual namespaces.</summary>
static class XamlCorpus
{
	public const string Prefix = """
		<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
		            xmlns:sys="clr-namespace:System;assembly=System.Runtime"
		            xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
		            xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
		            mc:Ignorable="d">
		""";

	public static readonly (string Name, string Body)[] Cases = {
		("x:Double/String/Boolean/Int32 resources", """
			<StackPanel.Resources>
			  <x:Double x:Key="D">24.5</x:Double><x:String x:Key="S">Hello</x:String>
			  <x:Boolean x:Key="B">True</x:Boolean><x:Int32 x:Key="I">7</x:Int32>
			</StackPanel.Resources>
			<Button FontSize="{StaticResource D}" Content="{StaticResource S}"/>
			"""),
		("sys:* CLR resources", """
			<StackPanel.Resources>
			  <sys:Double x:Key="D">3</sys:Double><sys:String x:Key="S">x</sys:String><sys:Int32 x:Key="I">2</sys:Int32>
			</StackPanel.Resources>
			<Button Width="{StaticResource D}"/>
			"""),
		("x:Static and x:Type", """
			<Button Foreground="{x:Static SystemColors.HighlightBrush}" Tag="{x:Type Button}"/>
			"""),
		("x:Null", """<Button Background="{x:Null}" Content="{x:Null}"/>"""),
		("x:Array", """
			<ListBox><ListBox.Tag><x:Array Type="sys:String"><sys:String>a</sys:String><sys:String>b</sys:String></x:Array></ListBox.Tag></ListBox>
			"""),
		("DynamicResource and StaticResource", """
			<StackPanel.Resources><SolidColorBrush x:Key="Accent" Color="Red"/></StackPanel.Resources>
			<Button Background="{StaticResource Accent}" Foreground="{DynamicResource Accent}"/>
			"""),
		("Style with setters and implicit key", """
			<StackPanel.Resources>
			  <Style TargetType="Button"><Setter Property="Width" Value="80"/><Setter Property="Content" Value="Go"/></Style>
			  <Style x:Key="Named" TargetType="{x:Type TextBox}"><Setter Property="FontSize" Value="14"/></Style>
			</StackPanel.Resources>
			<Button/><TextBox Style="{StaticResource Named}"/>
			"""),
		("ControlTemplate and DataTemplate", """
			<StackPanel.Resources>
			  <ControlTemplate x:Key="T" TargetType="Button"><Border x:Name="b" Background="Red"><ContentPresenter/></Border></ControlTemplate>
			  <DataTemplate DataType="{x:Type sys:String}"><TextBlock Text="{Binding}"/></DataTemplate>
			</StackPanel.Resources>
			<Button Template="{StaticResource T}"/>
			"""),
		("Binding with parameters", """
			<TextBox Text="{Binding Path=Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged, FallbackValue='none'}"/>
			<TextBlock Text="{Binding RelativeSource={RelativeSource AncestorType=StackPanel}, Path=Tag, StringFormat={}{0:N2}}"/>
			"""),
		("Attached properties", """
			<Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition/></Grid.RowDefinitions>
			<Button Grid.Row="1" Grid.Column="0" Panel.ZIndex="3"/></Grid>
			"""),
		("Property elements and brushes", """
			<Border><Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
			  <GradientStop Color="Red" Offset="0"/><GradientStop Color="#FF0000FF" Offset="1"/></LinearGradientBrush></Border.Background>
			  <Border.BorderThickness><Thickness Left="1" Top="2" Right="3" Bottom="4"/></Border.BorderThickness></Border>
			"""),
		("Type converters", """
			<Button Margin="1,2,3,4" Padding="5" HorizontalAlignment="Right" Visibility="Collapsed" Background="#80FF0000" Width="NaN" FontFamily="Segoe UI" FontWeight="Bold"/>
			<Border CornerRadius="3,4,5,6" BorderBrush="Navy"/>
			"""),
		("Content property and text", """
			<Button>Click   me</Button><TextBlock xml:space="preserve">  keep   spaces  </TextBlock>
			<TextBlock>Hello <Run FontWeight="Bold">bold</Run> <Italic>it</Italic> end</TextBlock>
			"""),
		("ListBox items", """
			<ListBox><ListBoxItem>One</ListBoxItem><ListBoxItem>Two</ListBoxItem></ListBox>
			"""),
		("ListBox of strings", """
			<ListBox><sys:String>a</sys:String><sys:String>b</sys:String></ListBox>
			"""),
		("x:Name x:Uid x:Key forms", """
			<Button x:Name="go" x:Uid="u1"/><TextBox Name="legacy"/>
			<StackPanel.Resources><x:Static x:Key="K" Member="SystemColors.ControlBrush"/></StackPanel.Resources>
			"""),
		("Non-string keys", """
			<StackPanel.Resources>
			  <SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}" Color="Red"/>
			  <Style x:Key="{x:Type Button}" TargetType="Button"><Setter Property="Width" Value="9"/></Style>
			</StackPanel.Resources>
			"""),
		("Merged dictionaries", """
			<StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
			  <ResourceDictionary><SolidColorBrush x:Key="M" Color="Blue"/></ResourceDictionary>
			</ResourceDictionary.MergedDictionaries><SolidColorBrush x:Key="Own" Color="Green"/></ResourceDictionary></StackPanel.Resources>
			"""),
		("Designer attributes", """
			<Button d:DesignWidth="10" d:Background="Red" Content="x"/>
			<mc:AlternateContent><mc:Choice Requires="d"><Button/></mc:Choice><mc:Fallback><TextBox/></mc:Fallback></mc:AlternateContent>
			"""),
		("Triggers", """
			<Button><Button.Style><Style TargetType="Button"><Style.Triggers>
			  <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="Red"/></Trigger>
			  <DataTrigger Binding="{Binding Ok}" Value="True"><Setter Property="Width" Value="5"/></DataTrigger>
			</Style.Triggers></Style></Button.Style></Button>
			"""),
		("Markup extension escapes", """
			<TextBlock Tag="{}{literal}" Text="{Binding Converter={x:Null}}"/>
			"""),
		("Enum and flags", """
			<TextBlock TextWrapping="Wrap" TextTrimming="CharacterEllipsis"/><Button FocusVisualStyle="{x:Null}" Cursor="Hand"/>
			"""),
		("Uri and Key as attribute order", """
			<Image Source="pack://application:,,,/a.png"/><Button x:Name="n" Width="3" x:Uid="z"/>
			"""),
	};
}
