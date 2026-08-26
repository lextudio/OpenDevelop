// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Gap 2, small-first slice (doc/technotes/stride-game-studio.md "Real-content integration
// plan"): read real entity/transform data out of the loaded session's first scene asset, without
// touching EditorGameController (that needs a real threading-model fork patch - SDL/Cocoa
// requires the main thread, EditorGameController runs its game loop on a dedicated background
// thread - deferred; see the technote). This reads the design-time Quantum-backed asset data
// directly (Entity.Name/Transform.Position), not a runtime-compiled scene - no meshes/materials,
// just real positions or entities driving the render instead of a synthetic checkerboard.

using System.Collections.Generic;
using System.Linq;
using Stride.Assets.Entities;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;

namespace ICSharpCode.StrideGameStudio
{
	public static class SceneAssetReader
	{
		public readonly record struct EntityMarker(string Name, Vector3 Position);

		/// <summary>Reads entity name/position pairs from the first `.sdscene` asset found in the
		/// session's local packages. Returns an empty list if the package has no scene asset.</summary>
		public static IReadOnlyList<EntityMarker> ReadFirstScene(SessionViewModel session)
		{
			foreach (var pkg in session.LocalPackages)
			{
				foreach (var assetVm in pkg.Assets)
				{
					if (assetVm.Asset is not SceneAsset sceneAsset)
						continue;

					return sceneAsset.Hierarchy.Parts.Values
						.Select(part => part.Entity)
						.Where(entity => entity != null)
						.Select(entity => new EntityMarker(entity.Name, entity.Transform.Position))
						.ToList();
				}
			}
			return [];
		}
	}
}
