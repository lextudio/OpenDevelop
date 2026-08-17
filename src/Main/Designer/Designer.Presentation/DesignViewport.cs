using System;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Shared design-space-to-surface coordinate math: a design of <see cref="DesignWidth"/> x
	/// <see cref="DesignHeight"/> logical units, shown at <see cref="Scale"/> with its top-left
	/// placed at (<see cref="OriginX"/> + <see cref="PanX"/>, <see cref="OriginY"/> + <see cref="PanY"/>)
	/// in the host's own surface-local coordinate space.
	///
	/// This is deliberately the ONLY thing this type knows about - it has no opinion on toolbar
	/// chrome, scroll offsets, selection, gestures or rendering. Callers that need those (e.g.
	/// UnoDesignSurfaceControl's ScrollViewer offsets and toolbar height) apply them on top of
	/// the plain conversion this type provides.
	///
	/// WinForms' backend never scales or pans - it is the <see cref="Identity"/> case of the
	/// exact same shape (Scale=1, Origin=(0,0), Pan=(0,0), so DesignToSurface/SurfaceToDesign
	/// are the identity function). WinUI/Uno's backend uses <see cref="Fit"/>, which centers the
	/// design at "fit" scale inside a viewport and layers a user zoom/pan on top - see
	/// UnoDesignSurfaceControl's own "Viewport model" doc comment for the full picture.
	/// </summary>
	public readonly struct DesignViewport
	{
		public double DesignWidth { get; }
		public double DesignHeight { get; }
		public double Scale { get; }
		public double OriginX { get; }
		public double OriginY { get; }
		public double PanX { get; }
		public double PanY { get; }

		DesignViewport(double designWidth, double designHeight, double scale, double originX, double originY, double panX, double panY)
		{
			DesignWidth = designWidth;
			DesignHeight = designHeight;
			Scale = scale;
			OriginX = originX;
			OriginY = originY;
			PanX = panX;
			PanY = panY;
		}

		/// <summary>Scale=1, origin=(0,0), pan=(0,0) - the case a backend with no zoom/pan
		/// concept (WinForms) always uses, and the degenerate case Uno's own math already
		/// falls back to when the design or viewport has zero size.</summary>
		public static DesignViewport Identity(double designWidth, double designHeight)
			=> new(designWidth, designHeight, 1.0, 0.0, 0.0, 0.0, 0.0);

		/// <summary>
		/// Centers a <paramref name="designWidth"/> x <paramref name="designHeight"/> design
		/// inside a <paramref name="viewportWidth"/> x <paramref name="viewportHeight"/> surface
		/// at "fit" scale, times the caller's own <paramref name="zoomFactor"/>, offset by the
		/// caller's own <paramref name="panX"/>/<paramref name="panY"/>. Matches
		/// UnoDesignSurfaceControl's EffectiveScale()/ViewportParams() formulas exactly - this
		/// is a relocation of those formulas, not a re-derivation.
		/// </summary>
		public static DesignViewport Fit(double designWidth, double designHeight,
			double viewportWidth, double viewportHeight, double zoomFactor, double panX, double panY)
		{
			// Matches EffectiveScale()/ViewportParams() exactly, including their degenerate-input
			// behavior: only Scale falls back to 1.0 when a dimension is zero - OriginX/OriginY
			// are still computed from the raw (possibly zero) inputs at that fallback scale, not
			// zeroed out, since some callers (e.g. LayoutSelection) invoke this before the
			// viewport has been sized and rely on that exact fallback shape.
			var scale = (designWidth == 0 || designHeight == 0 || viewportWidth == 0 || viewportHeight == 0)
				? 1.0
				: Math.Min(viewportWidth / designWidth, viewportHeight / designHeight) * zoomFactor;
			var originX = (viewportWidth - designWidth * scale) / 2;
			var originY = (viewportHeight - designHeight * scale) / 2;
			return new DesignViewport(designWidth, designHeight, scale, originX, originY, panX, panY);
		}

		/// <summary>
		/// Centers a design at a fixed zoom <paramref name="scale"/> (1.0 = 100%, 1:1), for
		/// backends whose zoom is an absolute scale rather than a multiple of fit - e.g. the
		/// WinForms designer's shared toolbar zoom.
		/// </summary>
		public static DesignViewport Zoom(double designWidth, double designHeight,
			double viewportWidth, double viewportHeight, double scale)
		{
			var originX = (viewportWidth - designWidth * scale) / 2;
			var originY = (viewportHeight - designHeight * scale) / 2;
			return new DesignViewport(designWidth, designHeight, scale, originX, originY, 0.0, 0.0);
		}

		/// <summary>Design-space point to surface-local coordinates (no toolbar/scroll offsets -
		/// callers with those apply them on top of this).</summary>
		public (double X, double Y) DesignToSurface(double x, double y)
		{
			var baseX = Math.Max(0, OriginX);
			var baseY = Math.Max(0, OriginY);
			return (baseX + PanX + x * Scale, baseY + PanY + y * Scale);
		}

		/// <summary>Surface-local coordinates to design-space point (inverse of
		/// <see cref="DesignToSurface"/>).</summary>
		public (double X, double Y) SurfaceToDesign(double x, double y)
		{
			var baseX = Math.Max(0, OriginX);
			var baseY = Math.Max(0, OriginY);
			return ((x - baseX - PanX) / Scale, (y - baseY - PanY) / Scale);
		}
	}
}
