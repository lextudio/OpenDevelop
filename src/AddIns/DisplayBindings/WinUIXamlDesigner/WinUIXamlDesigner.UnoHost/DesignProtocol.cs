namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Runtime-agnostic protocol between the OpenDevelop host and the out-of-process WinUI
	/// design surface. The wire DTOs now live in the shared <c>Designer.Remote</c> project
	/// (<c>ICSharpCode.SharpDevelop.Designer.Remote.DesignerProtocol</c> and friends -
	/// DesignHost.cs/Program.cs import that namespace directly) - this file keeps only the
	/// internal request-bundling classes below, which are plain method-parameter groupings
	/// (<c>LoadDesignAsync</c>/<c>LayoutAsync</c> etc.), never serialized over the wire
	/// themselves (the actual RPC methods in Program.cs take flat positional parameters, not
	/// these objects).
	/// </summary>
	public class LoadDesignRequest
	{
		public string SessionId { get; set; } = "";
		public string DocumentId { get; set; } = "";
		public long Version { get; set; }
		public string Xaml { get; set; } = "";
		public double Width { get; set; } = 640;
		public double Height { get; set; } = 480;
		public double Dpi { get; set; } = 1.0;
	}

	public class LayoutRequest
	{
		public double Width { get; set; } = 640;
		public double Height { get; set; } = 480;
		public double Dpi { get; set; } = 1.0;
	}

	public class ThemeRequest
	{
		/// <summary>"Light", "Dark" or "Default".</summary>
		public string Theme { get; set; } = "";
	}

	public class HitTestRequest
	{
		public double X { get; set; }
		public double Y { get; set; }
	}
}
