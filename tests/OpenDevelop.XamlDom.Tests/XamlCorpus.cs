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

	/// <summary>Cases XamlDom cannot load at all, with why. Both the load comparison and the round-trip test use this
	/// one list, and the load comparison verifies it against what XamlDom really does, so it cannot go stale.</summary>
	public static readonly Dictionary<string, string> NotLoadableByXamlDom = new() {
		["Merged dictionaries"] = "a ResourceDictionary with MergedDictionaries and its own entries throws ArgumentException",
		["Designer attributes"] = "mc:AlternateContent is not supported",
	};

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
		("Style BasedOn and TemplateBinding", """
			<StackPanel.Resources>
			  <Style x:Key="Base" TargetType="Control"><Setter Property="Padding" Value="3"/></Style>
			  <Style x:Key="Derived" TargetType="Button" BasedOn="{StaticResource Base}">
			    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
			      <Border Background="{TemplateBinding Background}" Width="{TemplateBinding Width}"><ContentPresenter/></Border>
			    </ControlTemplate></Setter.Value></Setter>
			  </Style>
			</StackPanel.Resources>
			<Button Style="{StaticResource Derived}"/>
			"""),
		("ControlTemplate triggers with TargetName", """
			<StackPanel.Resources><ControlTemplate x:Key="T" TargetType="Button">
			  <Border x:Name="bd" Background="Red"><ContentPresenter/></Border>
			  <ControlTemplate.Triggers><Trigger Property="IsPressed" Value="True"><Setter TargetName="bd" Property="Background" Value="Blue"/></Trigger></ControlTemplate.Triggers>
			</ControlTemplate></StackPanel.Resources>
			<Button Template="{StaticResource T}"/>
			"""),
		("Transforms", """
			<Button><Button.RenderTransform><TransformGroup>
			  <RotateTransform Angle="30"/><ScaleTransform ScaleX="2" ScaleY="3"/><TranslateTransform X="4" Y="5"/>
			</TransformGroup></Button.RenderTransform></Button>
			<Border RenderTransform="1,0,0,1,10,20"/>
			"""),
		("Geometry mini-language and shapes", """
			<Path Data="M 0,0 L 10,10 L 0,10 Z" Stroke="Black" StrokeThickness="2" Fill="Red"/>
			<Rectangle Width="10" Height="5" RadiusX="2" Stroke="Blue" StrokeDashArray="2 1"/>
			<Ellipse><Ellipse.Fill><RadialGradientBrush GradientOrigin="0.3,0.3"><GradientStop Color="White" Offset="0"/><GradientStop Color="Black" Offset="1"/></RadialGradientBrush></Ellipse.Fill></Ellipse>
			"""),
		("Drawing and tile brushes", """
			<Border><Border.Background><DrawingBrush TileMode="Tile" Viewport="0,0,10,10" ViewportUnits="Absolute"><DrawingBrush.Drawing>
			  <GeometryDrawing Brush="Red"><GeometryDrawing.Geometry><RectangleGeometry Rect="0,0,5,5"/></GeometryDrawing.Geometry></GeometryDrawing>
			</DrawingBrush.Drawing></DrawingBrush></Border.Background></Border>
			"""),
		("Animations and storyboards", """
			<Button><Button.Triggers><EventTrigger RoutedEvent="Button.Loaded"><BeginStoryboard><Storyboard>
			  <DoubleAnimation Storyboard.TargetProperty="Width" From="0" To="100" Duration="0:0:1.5" AutoReverse="True" RepeatBehavior="Forever"/>
			  <ColorAnimationUsingKeyFrames Storyboard.TargetProperty="(Button.Background).(SolidColorBrush.Color)"><LinearColorKeyFrame KeyTime="0:0:1" Value="Red"/></ColorAnimationUsingKeyFrames>
			</Storyboard></BeginStoryboard></EventTrigger></Button.Triggers></Button>
			"""),
		("MultiBinding and RelativeSource", """
			<TextBlock><TextBlock.Text><MultiBinding StringFormat="{}{0} {1}"><Binding Path="A"/><Binding Path="B" Mode="OneWay"/></MultiBinding></TextBlock.Text></TextBlock>
			<TextBlock Tag="{Binding RelativeSource={RelativeSource Self}, Path=Name}" Text="{Binding RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type StackPanel}, AncestorLevel=1}}"/>
			"""),
		("Input and command bindings", """
			<StackPanel.InputBindings><KeyBinding Key="F5" Modifiers="Control" Command="ApplicationCommands.Open"/></StackPanel.InputBindings>
			<StackPanel.CommandBindings><CommandBinding Command="ApplicationCommands.Copy"/></StackPanel.CommandBindings>
			<Button Command="ApplicationCommands.Paste"/>
			"""),
		("ToolTip, ContextMenu and Menu", """
			<Button ToolTip="tip"><Button.ContextMenu><ContextMenu><MenuItem Header="A" InputGestureText="Ctrl+A"/><Separator/><MenuItem Header="B" IsCheckable="True"/></ContextMenu></Button.ContextMenu></Button>
			<Menu><MenuItem Header="File"><MenuItem Header="Open"/></MenuItem></Menu>
			"""),
		("TabControl and Expander", """
			<TabControl><TabItem Header="One"><TextBlock Text="a"/></TabItem><TabItem><TabItem.Header><TextBlock Text="two"/></TabItem.Header></TabItem></TabControl>
			<Expander Header="E" IsExpanded="True"><Button/></Expander>
			"""),
		("Grid definitions and DockPanel", """
			<Grid><Grid.ColumnDefinitions><ColumnDefinition Width="2*"/><ColumnDefinition Width="Auto" MinWidth="10"/><ColumnDefinition Width="100"/></Grid.ColumnDefinitions>
			<Button Grid.ColumnSpan="2" Grid.Column="1"/></Grid>
			<DockPanel LastChildFill="False"><Button DockPanel.Dock="Right"/><Button DockPanel.Dock="Top"/></DockPanel>
			"""),
		("Fonts, cursors and enums", """
			<TextBlock FontFamily="Arial, Segoe UI" FontStyle="Italic" FontStretch="Condensed" TextDecorations="Underline" Foreground="Transparent" Cursor="Wait" FlowDirection="RightToLeft"/>
			<Button Width="1e2" Height="  7  " IsEnabled="false" ClipToBounds="TRUE"/>
			"""),
		("Freezable and value-type resources", """
			<StackPanel.Resources>
			  <SolidColorBrush x:Key="F" Color="Red"/>
			  <Color x:Key="C">#FF102030</Color>
			  <Thickness x:Key="Th">1,2,3,4</Thickness>
			  <CornerRadius x:Key="Cr">5</CornerRadius>
			  <FontFamily x:Key="Ff">Arial</FontFamily>
			  <sys:TimeSpan x:Key="Ts">0:0:2</sys:TimeSpan>
			  <GridLength x:Key="Gl">Auto</GridLength>
			</StackPanel.Resources>
			<Border BorderThickness="{StaticResource Th}" CornerRadius="{StaticResource Cr}"/>
			"""),
		("Collections of non-UI elements", """
			<StackPanel.Resources><x:Array x:Key="A" Type="sys:Int32"><sys:Int32>1</sys:Int32><sys:Int32>2</sys:Int32></x:Array></StackPanel.Resources>
			<ListBox><ListBox.Resources><DataTemplate x:Key="I"><TextBlock Text="{Binding}"/></DataTemplate></ListBox.Resources><ListBox.ItemTemplate><StaticResource ResourceKey="I"/></ListBox.ItemTemplate></ListBox>
			"""),
		("Element syntax for markup extensions", """
			<StackPanel.Resources><SolidColorBrush x:Key="B" Color="Red"/></StackPanel.Resources>
			<Button><Button.Background><StaticResource ResourceKey="B"/></Button.Background><Button.Tag><x:Null/></Button.Tag><Button.Content><Binding Path="X"/></Button.Content></Button>
			"""),
		("Whitespace and comments", """
			<!-- a comment before -->
			<Button>
			   <!-- inside -->
			   Padded text
			</Button>
			<TextBlock Text="  attr  spaces  "/>
			<TextBox>&lt;escaped&gt; &amp; entity &#169;</TextBox>
			"""),
		("Inline elements", """
			<TextBlock><Run Text="a"/><LineBreak/><Hyperlink NavigateUri="http://example.com">link</Hyperlink><Span FontSize="9">s<Bold>b</Bold></Span></TextBlock>
			"""),
		("Image and media-free pack URIs", """
			<Image Stretch="UniformToFill" StretchDirection="DownOnly"/>
			<Border><Border.Background><ImageBrush Stretch="Fill" TileMode="None" Opacity="0.5"/></Border.Background></Border>
			"""),
		("Attached properties on property elements", """
			<Canvas><Button Canvas.Left="5" Canvas.Top="6"/><Button><Canvas.Left>9</Canvas.Left></Button></Canvas>
			<TextBlock TextOptions.TextFormattingMode="Display" ScrollViewer.CanContentScroll="True" ToolTipService.ShowDuration="500"/>
			"""),
		("Clr-namespace types and generic-free objects", """
			<StackPanel.Resources>
			  <sys:Uri x:Key="U">http://a/b</sys:Uri><sys:Guid x:Key="G">00000000-0000-0000-0000-000000000001</sys:Guid>
			  <sys:DateTime x:Key="Dt">2020-01-02</sys:DateTime><sys:Char x:Key="Ch">x</sys:Char><sys:Decimal x:Key="De">1.5</sys:Decimal>
			</StackPanel.Resources>
			"""),
	};
}
