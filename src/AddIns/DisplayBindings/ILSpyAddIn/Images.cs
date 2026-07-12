// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Replaces ILSpy's own Images.cs (excluded, see ILSpyAddIn.csproj), which loads vector icons via
// pack URIs resolved against the *entry* assembly (SharpDevelop.exe) - fine for standalone
// ILSpy.exe, broken for a hosted addin (see doc/technotes/ilspy.md). This keeps the exact same
// public field/method surface every linked ILSpy call site expects (Images.Class, Images.Method,
// Images.GetIcon(TypeIcon, ...), etc.) so nothing else needs to change, but backs it with icons
// embedded straight into this assembly (Icons/*.xaml, sourced from the VS2017 Image Library's own
// pre-converted XAML vector format) via VsIconLoader instead.
//
// Scope cut: real ILSpy composites accessibility/static/extension/reference badges onto base
// icons (protected/internal/private lock overlays, a small "S" for static, etc.). VS2017's
// library doesn't ship equivalent badge glyphs for this icon set, so overlay compositing here is
// a no-op (base icon only) - CreateOverlayImage still exists so callers compile unchanged, it
// just never has a non-null overlay/static/extension source to draw.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

using ICSharpCode.ILSpyAddIn;

namespace ICSharpCode.ILSpy
{
	using ICSharpCode.Decompiler.TypeSystem;

	static class Images
	{
		private static readonly Rect iconRect = new Rect(0, 0, 16, 16);

		// Maps ILSpy's internal icon names to the embedded Icons/*.xaml key actually shipped.
		// Anything not listed (or listed but missing) falls back to a generic icon rather than
		// crashing - most callers only need *a* reasonable glyph, not a pixel-exact match.
		private static readonly Dictionary<string, string> nameMap = new(StringComparer.OrdinalIgnoreCase) {
			["ShowPublicOnly"] = "Type",
			["FieldReadOnly"] = "Field",
			["Constructor"] = "Method",
			["VirtualMethod"] = "Method",
			["PInvokeMethod"] = "Method",
			["SuperTypes"] = "SubTypes",
			["Folder.Open"] = "FolderOpen",
			["Folder.Closed"] = "FolderClosed",
			["ListFolder.Open"] = "ListFolderOpen",
			["MetadataTableGroup"] = "MetadataTable",
			["MetadataFile"] = "Metadata",
			["ResourceResourcesFile"] = "Binary",
			["ResourceImage"] = "Binary",
			["Resource"] = "Binary",
			["ResourceXslt"] = "ResourceXml",
			["AssemblyLoading"] = "Assembly",
			["FindAssembly"] = "Assembly",
			["WebAssembly"] = "Binary",
			["NuGet"] = "Library",
		};

		private const string Fallback = "Class";

		static ImageSource Load(string icon)
		{
			var key = nameMap.TryGetValue(icon, out var mapped) ? mapped : icon;
			return VsIconLoader.Load(key) ?? VsIconLoader.Load(Fallback);
		}

		public static readonly ImageSource ILSpyIcon = Load("ILSpyIcon");

		public static readonly ImageSource ViewCode = Load(Fallback);
		public static readonly ImageSource Save = Load("Save");
		public static readonly ImageSource OK = Load("OK");

		public static readonly ImageSource Delete = Load("Delete");
		public static readonly ImageSource Search = Load("Search");

		public static readonly ImageSource Assembly = Load("Assembly");
		public static readonly ImageSource AssemblyWarning = Load("AssemblyWarning");
		public static readonly ImageSource AssemblyLoading = Load("AssemblyLoading");
		public static readonly ImageSource FindAssembly = Load("FindAssembly");

		public static readonly ImageSource Library = Load("Library");
		public static readonly ImageSource Namespace = Load("Namespace");

		public static readonly ImageSource ReferenceFolder = Load("ReferenceFolder");
		public static readonly ImageSource NuGet = Load("NuGet");
		public static readonly ImageSource MetadataFile = Load("MetadataFile");
		public static readonly ImageSource WebAssemblyFile = Load("WebAssembly");
		public static readonly ImageSource ProgramDebugDatabase = Load("ProgramDebugDatabase");

		public static readonly ImageSource Metadata = Load("Metadata");
		public static readonly ImageSource Heap = Load("Heap");
		public static readonly ImageSource Header = Load("Header");
		public static readonly ImageSource MetadataTable = Load("MetadataTable");
		public static readonly ImageSource MetadataTableGroup = Load("MetadataTableGroup");
		public static readonly ImageSource ListFolder = Load("ListFolder");
		public static readonly ImageSource ListFolderOpen = Load("ListFolder.Open");

		public static readonly ImageSource SubTypes = Load("SubTypes");
		public static readonly ImageSource SuperTypes = Load("SuperTypes");

		public static readonly ImageSource FolderOpen = Load("Folder.Open");
		public static readonly ImageSource FolderClosed = Load("Folder.Closed");

		public static readonly ImageSource Resource = Load("Resource");
		public static readonly ImageSource ResourceImage = Load("ResourceImage");
		public static readonly ImageSource ResourceResourcesFile = Load("ResourceResourcesFile");
		public static readonly ImageSource ResourceXml = Load("ResourceXml");
		public static readonly ImageSource ResourceXsd = Load("ResourceXslt");
		public static readonly ImageSource ResourceXslt = Load("ResourceXslt");

		public static readonly ImageSource Class = Load("Class");
		public static readonly ImageSource Struct = Load("Struct");
		public static readonly ImageSource Interface = Load("Interface");
		public static readonly ImageSource Delegate = Load("Delegate");
		public static readonly ImageSource Enum = Load("Enum");
		public static readonly ImageSource Type = Load("ShowPublicOnly");

		public static readonly ImageSource Field = Load("Field");
		public static readonly ImageSource FieldReadOnly = Load("FieldReadOnly");
		public static readonly ImageSource Literal = Load("Literal");
		public static readonly ImageSource EnumValue = Load("EnumValue");

		public static readonly ImageSource Method = Load("Method");
		public static readonly ImageSource Constructor = Load("Constructor");
		public static readonly ImageSource VirtualMethod = Load("VirtualMethod");
		public static readonly ImageSource Operator = Load("Operator");
		public static readonly ImageSource ExtensionMethod = Load("ExtensionMethod");
		public static readonly ImageSource PInvokeMethod = Load("PInvokeMethod");

		public static readonly ImageSource Property = Load("Property");
		public static readonly ImageSource Indexer = Load("Indexer");

		public static readonly ImageSource Event = Load("Event");

		// Accessibility/modifier badges: no VS2017 equivalents wired up (see file header) - left
		// null, which CreateOverlayImage below treats as "draw no badge".
		private static readonly ImageSource OverlayProtected = null;
		private static readonly ImageSource OverlayInternal = null;
		private static readonly ImageSource OverlayProtectedInternal = null;
		private static readonly ImageSource OverlayPrivate = null;
		private static readonly ImageSource OverlayPrivateProtected = null;
		private static readonly ImageSource OverlayCompilerControlled = null;
		private static readonly ImageSource OverlayReference = null;

		private static readonly ImageSource OverlayStatic = null;
		private static readonly ImageSource OverlayExtension = null;

		public static readonly ImageSource TypeReference = GetIcon("ShowPublicOnly", "ReferenceOverlay");
		public static readonly ImageSource MethodReference = GetIcon("Method", "ReferenceOverlay");
		public static readonly ImageSource FieldReference = GetIcon("Field", "ReferenceOverlay");
		public static readonly ImageSource ExportedType = GetIcon("ShowPublicOnly", "ExportOverlay");

		public static ImageSource Load(object part, string icon)
		{
			if (icon == null)
				return null;
			// Real ILSpy distinguishes ".png" vs ".xaml" lookups and resolves relative to `part`'s
			// declaring assembly; we only have one flat embedded icon set, so just strip any
			// path/extension noise (e.g. "Images/Warning", "Warning.png") down to a bare name.
			var name = icon;
			int slash = name.LastIndexOfAny(['/', '\\']);
			if (slash >= 0)
				name = name[(slash + 1)..];
			if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
				name = name[..name.LastIndexOf('.')];
			return Load(name);
		}

		public static Drawing LoadDrawingGroup(object part, string icon)
		{
			return Load(part, icon) is DrawingImage drawingImage ? drawingImage.Drawing : null;
		}

		private static readonly TypeIconCache typeIconCache = new TypeIconCache();
		private static readonly MemberIconCache memberIconCache = new MemberIconCache();

		public static ImageSource GetIcon(TypeIcon icon, AccessOverlayIcon overlay, bool isStatic = false, bool isExtension = false)
		{
			lock (typeIconCache)
				return typeIconCache.GetIcon(icon, overlay, isStatic, isExtension);
		}

		public static ImageSource GetIcon(MemberIcon icon, AccessOverlayIcon overlay, bool isStatic, bool isExtension)
		{
			lock (memberIconCache)
				return memberIconCache.GetIcon(icon, overlay, isStatic, isExtension);
		}

		public static AccessOverlayIcon GetOverlayIcon(Accessibility accessibility)
		{
			switch (accessibility)
			{
				case Accessibility.Public:
					return AccessOverlayIcon.Public;
				case Accessibility.Internal:
					return AccessOverlayIcon.Internal;
				case Accessibility.ProtectedAndInternal:
					return AccessOverlayIcon.PrivateProtected;
				case Accessibility.Protected:
					return AccessOverlayIcon.Protected;
				case Accessibility.ProtectedOrInternal:
					return AccessOverlayIcon.ProtectedInternal;
				case Accessibility.Private:
					return AccessOverlayIcon.Private;
				default:
					return AccessOverlayIcon.CompilerControlled;
			}
		}

		private static ImageSource GetIcon(string baseImage, string overlay = null, bool isStatic = false, bool isExtension = false)
		{
			ImageSource baseImageSource = Load(baseImage);
			ImageSource overlayImageSource = overlay != null ? Load(overlay) : null;

			return CreateOverlayImage(baseImageSource, overlayImageSource, isStatic, isExtension);
		}

		private static ImageSource CreateOverlayImage(ImageSource baseImage, ImageSource overlay, bool isStatic, bool isExtension)
		{
			var group = new DrawingGroup();
			var baseDrawingGroup = new DrawingGroup();

			Drawing baseDrawing = new ImageDrawing(baseImage, iconRect);

			baseDrawingGroup.Children.Add(baseDrawing);

			if (isExtension && OverlayExtension != null)
			{
				var extensionGroup = new DrawingGroup();
				extensionGroup.Children.Add(baseDrawingGroup);
				baseDrawingGroup.Transform = new ScaleTransform(0.8, 0.8);
				extensionGroup.Children.Add(new ImageDrawing(OverlayExtension, iconRect));
				baseDrawingGroup = extensionGroup;
			}

			group.Children.Add(baseDrawingGroup);

			if (isStatic && OverlayStatic != null)
			{
				group.Children.Add(new ImageDrawing(OverlayStatic, iconRect));
			}

			if (overlay != null)
			{
				baseDrawingGroup.Transform = new ScaleTransform(0.8, 0.8);
				group.Children.Add(new ImageDrawing(overlay, iconRect));
			}

			var image = new DrawingImage(group);
			if (image.CanFreeze)
			{
				image.Freeze();
			}
			return image;
		}

		#region icon caches & overlay management

		private class TypeIconCache : IconCache<TypeIcon>
		{
			public TypeIconCache()
			{
				PreloadPublicIconToCache(TypeIcon.Class, Images.Class);
				PreloadPublicIconToCache(TypeIcon.Enum, Images.Enum);
				PreloadPublicIconToCache(TypeIcon.Struct, Images.Struct);
				PreloadPublicIconToCache(TypeIcon.Interface, Images.Interface);
				PreloadPublicIconToCache(TypeIcon.Delegate, Images.Delegate);
			}

			protected override ImageSource GetBaseImage(TypeIcon icon)
			{
				switch (icon)
				{
					case TypeIcon.Class:
						return Images.Class;
					case TypeIcon.Enum:
						return Images.Enum;
					case TypeIcon.Struct:
						return Images.Struct;
					case TypeIcon.Interface:
						return Images.Interface;
					case TypeIcon.Delegate:
						return Images.Delegate;
					default:
						throw new ArgumentOutOfRangeException(nameof(icon), $"TypeIcon.{icon} is not supported!");
				}
			}
		}

		private class MemberIconCache : IconCache<MemberIcon>
		{
			public MemberIconCache()
			{
				PreloadPublicIconToCache(MemberIcon.Field, Images.Field);
				PreloadPublicIconToCache(MemberIcon.FieldReadOnly, Images.FieldReadOnly);
				PreloadPublicIconToCache(MemberIcon.Literal, Images.Literal);
				PreloadPublicIconToCache(MemberIcon.EnumValue, Images.EnumValue);
				PreloadPublicIconToCache(MemberIcon.Property, Images.Property);
				PreloadPublicIconToCache(MemberIcon.Indexer, Images.Indexer);
				PreloadPublicIconToCache(MemberIcon.Method, Images.Method);
				PreloadPublicIconToCache(MemberIcon.Constructor, Images.Constructor);
				PreloadPublicIconToCache(MemberIcon.VirtualMethod, Images.VirtualMethod);
				PreloadPublicIconToCache(MemberIcon.Operator, Images.Operator);
				PreloadPublicIconToCache(MemberIcon.PInvokeMethod, Images.PInvokeMethod);
				PreloadPublicIconToCache(MemberIcon.Event, Images.Event);
			}

			protected override ImageSource GetBaseImage(MemberIcon icon)
			{
				switch (icon)
				{
					case MemberIcon.Field:
						return Images.Field;
					case MemberIcon.FieldReadOnly:
						return Images.FieldReadOnly;
					case MemberIcon.Literal:
						return Images.Literal;
					case MemberIcon.EnumValue:
						return Images.EnumValue;
					case MemberIcon.Property:
						return Images.Property;
					case MemberIcon.Indexer:
						return Images.Indexer;
					case MemberIcon.Method:
						return Images.Method;
					case MemberIcon.Constructor:
						return Images.Constructor;
					case MemberIcon.VirtualMethod:
						return Images.VirtualMethod;
					case MemberIcon.Operator:
						return Images.Operator;
					case MemberIcon.PInvokeMethod:
						return Images.PInvokeMethod;
					case MemberIcon.Event:
						return Images.Event;
					default:
						throw new ArgumentOutOfRangeException(nameof(icon), $"MemberIcon.{icon} is not supported!");
				}
			}
		}

		private abstract class IconCache<T>
		{
			private readonly Dictionary<(T, AccessOverlayIcon, bool, bool), ImageSource> cache = new Dictionary<(T, AccessOverlayIcon, bool, bool), ImageSource>();

			protected void PreloadPublicIconToCache(T icon, ImageSource image)
			{
				var iconKey = (icon, AccessOverlayIcon.Public, false, false);
				cache.Add(iconKey, image);
			}

			public ImageSource GetIcon(T icon, AccessOverlayIcon overlay, bool isStatic, bool isExtension)
			{
				var iconKey = (icon, overlay, isStatic, isExtension);
				if (cache.TryGetValue(iconKey, out var cached))
				{
					return cached;
				}
				else
				{
					ImageSource result = BuildMemberIcon(icon, overlay, isStatic, isExtension);
					cache.Add(iconKey, result);
					return result;
				}
			}

			private ImageSource BuildMemberIcon(T icon, AccessOverlayIcon overlay, bool isStatic, bool isExtension)
			{
				ImageSource baseImage = GetBaseImage(icon);
				ImageSource overlayImage = GetOverlayImage(overlay);

				return CreateOverlayImage(baseImage, overlayImage, isStatic, isExtension);
			}

			protected abstract ImageSource GetBaseImage(T icon);

			private static ImageSource GetOverlayImage(AccessOverlayIcon overlay)
			{
				switch (overlay)
				{
					case AccessOverlayIcon.Public:
						return null;
					case AccessOverlayIcon.Protected:
						return Images.OverlayProtected;
					case AccessOverlayIcon.Internal:
						return Images.OverlayInternal;
					case AccessOverlayIcon.ProtectedInternal:
						return Images.OverlayProtectedInternal;
					case AccessOverlayIcon.Private:
						return Images.OverlayPrivate;
					case AccessOverlayIcon.PrivateProtected:
						return Images.OverlayPrivateProtected;
					case AccessOverlayIcon.CompilerControlled:
						return Images.OverlayCompilerControlled;
					default:
						throw new ArgumentOutOfRangeException(nameof(overlay), $"AccessOverlayIcon.{overlay} is not supported!");
				}
			}
		}

		#endregion
	}
}
