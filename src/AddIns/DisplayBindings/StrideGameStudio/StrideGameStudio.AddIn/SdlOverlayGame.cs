// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The windowed counterpart of the old HeadlessViewportGame: same animated placeholder scene
// (checkerboard ground grid + a rotating, hue-cycling billboard + orbiting satellites), but drawn
// straight to a real presenter backbuffer with no CPU readback - GPU presents directly to the
// SDL-owned native window. Driven by StrideSdlViewport via GameContextSDL(isUserManagingRun:
// true) + Game.Tick(), not Game.Run()'s own blocking loop (see StrideSdlViewport.cs for why).

using System;
using System.Collections.Generic;

using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;

namespace ICSharpCode.StrideGameStudio
{
	sealed class SdlOverlayGame : Stride.Engine.Game
	{
		readonly int initialWidth, initialHeight;
		float t;
		SpriteBatch spriteBatch;
		Texture whiteTex;

		// Set from the UI thread (StrideSdlViewport, after StrideEditorHost.OpenSessionAsync
		// completes) and read from Draw() - both run on the same WPF UI thread
		// (CompositionTarget.Rendering drives both Tick() and any pending set here), so no lock
		// is needed; a plain field is safe.
		IReadOnlyList<SceneAssetReader.EntityMarker> entities;

		public SdlOverlayGame(int width, int height)
		{
			initialWidth = Math.Max(1, width);
			initialHeight = Math.Max(1, height);
		}

		/// <summary>Gap 2, small-first slice: swap the synthetic placeholder scene for markers
		/// at the real entity positions read from the loaded session's scene asset.</summary>
		public void SetEntities(IReadOnlyList<SceneAssetReader.EntityMarker> value) => entities = value;

		protected override void Initialize()
		{
			GraphicsDeviceManager.PreferredBackBufferWidth = initialWidth;
			GraphicsDeviceManager.PreferredBackBufferHeight = initialHeight;
			GraphicsDeviceManager.PreferredBackBufferFormat = PixelFormat.B8G8R8A8_UNorm;
			GraphicsDeviceManager.PreferredDepthStencilFormat = PixelFormat.D24_UNorm_S8_UInt;
			GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
			// The engine-side fix for macOS's MoltenVK drawable-doubling defect (see
			// doc/technotes/stride-game-studio.md "macOS drawable-doubling RESOLVED"): keep our
			// own preferred backbuffer size authoritative instead of the window/surface's.
			GraphicsDeviceManager.SkipBackBufferClampToWindow = true;
			base.Initialize();
		}

		protected override System.Threading.Tasks.Task LoadContent()
		{
			base.LoadContent();
			spriteBatch = new SpriteBatch(GraphicsDevice);
			whiteTex = Texture.New2D(GraphicsDevice, 1, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource);
			GraphicsContext.CommandList.Clear(whiteTex, Color4.White);
			return System.Threading.Tasks.Task.CompletedTask;
		}

		/// <summary>Called by the host when its layout element resizes.</summary>
		public void Resize(int width, int height)
		{
			if (GraphicsDeviceManager == null)
				return;
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			if (GraphicsDeviceManager.PreferredBackBufferWidth == width && GraphicsDeviceManager.PreferredBackBufferHeight == height)
				return;
			GraphicsDeviceManager.PreferredBackBufferWidth = width;
			GraphicsDeviceManager.PreferredBackBufferHeight = height;
			GraphicsDeviceManager.ApplyChanges();
		}

		void DrawQuad(RectangleF rect, Color4 color, float rotation = 0f, Vector2? origin = null)
		{
			spriteBatch.Draw(whiteTex, rect, null, color, rotation, origin ?? new Vector2(rect.Width / 2f, rect.Height / 2f), SpriteEffects.None, 0f);
		}

		protected override void Update(GameTime gameTime)
		{
			t += (float)gameTime.Total.TotalSeconds;
			base.Update(gameTime);
		}

		protected override void Draw(GameTime gameTime)
		{
			var cmd = GraphicsContext.CommandList;
			var backBuffer = GraphicsDevice.Presenter.BackBuffer;
			var W = backBuffer.Width;
			var H = backBuffer.Height;

			cmd.SetRenderTargetAndViewport(null, backBuffer);
			cmd.Clear(backBuffer, new Color4(0.08f, 0.10f, 0.14f, 1f));

			spriteBatch.Begin(GraphicsContext, SpriteSortMode.Deferred, null);

			var cx = W * 0.5f;
			var cy = H * 0.55f;
			var hue = (t * 0.6f) % 1f;

			if (entities is { Count: > 0 } realEntities)
			{
				// Real data from the loaded session's scene asset (SceneAssetReader), not the
				// synthetic placeholder: a top-down (X, Z) projection of each entity's position,
				// auto-scaled to fit the backbuffer. No meshes/materials yet (that needs the
				// asset-compiler pipeline via EditorGameController - deferred, see the technote's
				// threading-conflict finding) - just real positions driving real markers.
				DrawEntityMarkers(realEntities, W, H, hue);
			}
			else
			{
				const int cell = 40;
				var cols = W / cell + 1;
				var rows = H / cell + 1;
				var scrollY = (int)(t * 20) % cell;
				for (int gy = 0; gy < rows; gy++)
				{
					for (int gx = 0; gx < cols; gx++)
					{
						if ((gx + gy) % 2 != 0)
							continue;
						var shade = 0.14f + 0.04f * (float)Math.Sin((gx + gy) * 0.6);
						DrawQuad(new RectangleF(gx * cell, gy * cell - scrollY, cell, cell), new Color4(shade, shade, shade + 0.03f, 1f));
					}
				}

				var size = 90 + 12f * (float)Math.Sin(t * 2.0f);
				var rgb = HsvToRgb(hue, 0.75f, 1f);
				DrawQuad(new RectangleF(cx - size / 2f, cy - size / 2f, size, size), rgb, t * 1.2f);

				for (int i = 0; i < 5; i++)
				{
					var ang = t * 1.5f + i * (float)(Math.PI * 2 / 5);
					var sx = cx + (float)Math.Cos(ang) * 120;
					var sy = cy + (float)Math.Sin(ang) * 120 * 0.5f;
					var s = 14;
					var srgb = HsvToRgb((hue + i * 0.15f) % 1f, 0.9f, 1f);
					DrawQuad(new RectangleF(sx - s / 2f, sy - s / 2f, s, s), srgb, ang);
				}
			}

			spriteBatch.End();
			base.Draw(gameTime);
		}

		void DrawEntityMarkers(IReadOnlyList<SceneAssetReader.EntityMarker> markers, int w, int h, float hue)
		{
			// Auto-fit: find the (X, Z) bounding box of all entity positions and scale/center it
			// into the backbuffer, so scenes of any size show up regardless of world-unit scale.
			float minX = float.MaxValue, maxX = float.MinValue;
			float minZ = float.MaxValue, maxZ = float.MinValue;
			foreach (var m in markers)
			{
				minX = Math.Min(minX, m.Position.X);
				maxX = Math.Max(maxX, m.Position.X);
				minZ = Math.Min(minZ, m.Position.Z);
				maxZ = Math.Max(maxZ, m.Position.Z);
			}

			var spanX = Math.Max(1e-3f, maxX - minX);
			var spanZ = Math.Max(1e-3f, maxZ - minZ);
			var margin = 60f;
			var scale = Math.Min((w - 2 * margin) / spanX, (h - 2 * margin) / spanZ);
			if (float.IsInfinity(scale) || scale <= 0)
				scale = 1f;

			for (int i = 0; i < markers.Count; i++)
			{
				var m = markers[i];
				var sx = margin + (m.Position.X - minX) * scale;
				var sy = margin + (m.Position.Z - minZ) * scale;
				var pulse = 1f + 0.15f * (float)Math.Sin(t * 3f + i);
				var size = 16f * pulse;
				var color = HsvToRgb((hue + i * 0.12f) % 1f, 0.8f, 1f);
				DrawQuad(new RectangleF(sx - size / 2f, sy - size / 2f, size, size), color, t + i);
			}
		}

		static Color4 HsvToRgb(float h, float s, float v)
		{
			int i = (int)(h * 6);
			float f = h * 6 - i;
			float p = v * (1 - s), q = v * (1 - s * f), tt = v * (1 - s * (1 - f));
			float r, g, b;
			switch (i % 6)
			{
				case 0: r = v; g = tt; b = p; break;
				case 1: r = q; g = v; b = p; break;
				case 2: r = p; g = v; b = tt; break;
				case 3: r = p; g = q; b = v; break;
				case 4: r = tt; g = p; b = v; break;
				default: r = v; g = p; b = q; break;
			}
			return new Color4(r, g, b, 1f);
		}
	}
}
