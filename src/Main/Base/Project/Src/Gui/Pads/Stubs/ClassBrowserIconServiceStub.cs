// MVP stub: the real ClassBrowserIconService (System.Drawing.Icon-based, layered on the excluded WinForms
// IconService/IWinFormsService.BitmapToIcon surface) is out of MVP scope.
// This lightweight replacement uses CompletionImage (WPF ImageSource) directly, avoiding the WinForms
// dependency so that class/type/method icons appear correctly in the icon bar margin.
using System;
using System.Drawing;
using System.Windows.Media;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Lightweight IImage that provides only ImageSource (no Bitmap/Icon without WinForms interop).
	/// </summary>
	sealed class ImageSourceOnlyImage : IImage
	{
		readonly ImageSource imageSource;

		public ImageSourceOnlyImage(ImageSource imageSource)
		{
			this.imageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
		}

		public ImageSource ImageSource => imageSource;
		public Bitmap Bitmap => null;
		public Icon Icon => null;
	}

	public static class ClassBrowserIconService
	{
		static IImage Wrap(ImageSource imageSource) =>
			imageSource != null ? new ImageSourceOnlyImage(imageSource) : null;

		// Entity Images
		public static readonly IImage Class = Wrap(CompletionImage.Class.BaseImage);
		public static readonly IImage Struct = Wrap(CompletionImage.Struct.BaseImage);
		public static readonly IImage Interface = Wrap(CompletionImage.Interface.BaseImage);
		public static readonly IImage Enum = Wrap(CompletionImage.Enum.BaseImage);
		public static readonly IImage Delegate = Wrap(CompletionImage.Delegate.BaseImage);
		public static readonly IImage Method = Wrap(CompletionImage.Method.BaseImage);
		public static readonly IImage Property = Wrap(CompletionImage.Property.BaseImage);
		public static readonly IImage Field = Wrap(CompletionImage.Field.BaseImage);
		public static readonly IImage Event = Wrap(CompletionImage.Event.BaseImage);
		public static readonly IImage Indexer = Wrap(CompletionImage.Indexer.BaseImage);

		public static IImage GetIcon(IEntity entity) => Wrap(CompletionImage.GetImage(entity));
		public static IImage GetIcon(IVariable v)
		{
			if (v is IField f)
				return GetIcon(f);
			if (v.IsConst)
				return Const;
			if (v is IParameter)
				return Parameter;
			return LocalVariable;
		}
		public static IImage GetIcon(IField v) => GetIcon((IEntity)v);
		public static IImage GetIcon(IType t)
		{
			var def = t.GetDefinition();
			return def != null ? GetIcon(def) : null;
		}
		public static IImage GetIcon(ITypeDefinition t) => GetIcon((IEntity)t);
		public static IImage GetIcon(IUnresolvedEntity entity) => Wrap(CompletionImage.GetImage(entity));
		public static IImage GetIcon(IUnresolvedMember m) => GetIcon((IUnresolvedEntity)m);
		public static IImage GetIcon(IUnresolvedTypeDefinition t) => GetIcon((IUnresolvedEntity)t);

		// Additional icons (non-entity, best-effort; may be null in MVP build)
		public static IImage Namespace => Wrap(CompletionImage.NamespaceImage);
		public static IImage Solution => null;
		public static IImage Const => Wrap(CompletionImage.Literal.BaseImage);
		public static IImage GotoArrow => null;
		public static IImage LocalVariable => null;
		public static IImage Parameter => null;
		public static IImage Keyword => null;
		public static IImage Operator => null;
		public static IImage CodeTemplate => null;
	}
}
