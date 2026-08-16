using System;
using System.Collections.Generic;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	public static class FormsDesignerProtocol
	{
		public const int Version = 1;
	}

	public sealed class HostHandshake
	{
		public int ProtocolVersion { get; set; }
		public string Runtime { get; set; } = "";
		public int ProcessId { get; set; }
	}

	public sealed class DesignerDocumentSnapshot
	{
		public long Version { get; set; }
		public string ProjectFileName { get; set; } = "";
		public string TargetFramework { get; set; } = "";
		public string ProjectAssemblyPath { get; set; } = "";
		public string PrimaryFileName { get; set; } = "";
		public string DesignerFileName { get; set; } = "";
		public List<DesignerSourceFileSnapshot> Files { get; set; } = new List<DesignerSourceFileSnapshot>();
	}

	public sealed class DesignerSourceFileSnapshot
	{
		public string FileName { get; set; } = "";
		public string Kind { get; set; } = "Source";
		public string Text { get; set; } = "";
		public string Base64 { get; set; } = "";
	}

	public sealed class DesignerSessionState
	{
		public long Version { get; set; }
		public bool Accepted { get; set; }
		public string Error { get; set; } = "";
		public string RootType { get; set; } = "";
		public int ComponentCount { get; set; }
		public List<DesignerComponentInfo> Components { get; set; } = new List<DesignerComponentInfo>();
		public DesignerRenderFrame Render { get; set; }
	}

	public sealed class DesignerRenderFrame
	{
		public long Sequence { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		public double Dpi { get; set; } = 1;
		public string PngBase64 { get; set; } = "";
	}

	public sealed class DesignerComponentInfo
	{
		public string Name { get; set; } = "";
		public string Type { get; set; } = "";
		public string Parent { get; set; } = "";
		public string Text { get; set; } = "";
		public string AccessibleName { get; set; } = "";
		public string AccessibleDescription { get; set; } = "";
		public string AccessibleRole { get; set; } = "";
		public int X { get; set; }
		public int Y { get; set; }
		public int SurfaceX { get; set; }
		public int SurfaceY { get; set; }
		public int Width { get; set; }
		public int Height { get; set; }
		public List<DesignerPropertyInfo> Properties { get; set; } = new List<DesignerPropertyInfo>();
		public List<DesignerEventInfo> Events { get; set; } = new List<DesignerEventInfo>();
	}

	public sealed class DesignerEventInfo
	{
		public string Name { get; set; } = "";
		public string Category { get; set; } = "";
		public string HandlerTypeName { get; set; } = "";
		public string Handler { get; set; } = "";
	}

	public sealed class DesignerPropertyInfo
	{
		public string Name { get; set; } = "";
		public string DisplayName { get; set; } = "";
		public string Description { get; set; } = "";
		public string Category { get; set; } = "";
		public string TypeName { get; set; } = "";
		public string Value { get; set; } = "";
		public bool IsNull { get; set; }
		public bool IsReadOnly { get; set; }
		public bool ShouldSerialize { get; set; }
		public bool IsEnum { get; set; }
	}

	public sealed class DesignerEditSet
	{
		public long BaseVersion { get; set; }
		public List<DesignerSourceFileSnapshot> Files { get; set; } = new List<DesignerSourceFileSnapshot>();
	}

	public sealed class DesignerHitTestResult
	{
		public string ComponentName { get; set; } = "";
		public string ComponentType { get; set; } = "";
	}
}
