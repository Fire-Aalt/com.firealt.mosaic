using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Authoring
{
    public class TilemapAuthoring : MonoBehaviour
    {
        public IntGridDefinition intGrid;
        [Tooltip("True: to access the runtime IntGrid of this tilemap, the IntGridDefinition can be used directly.\n\n" +
                 "False: the hash of the runtime IntGrid is unique per each instance and the entity needs to be queried to get the runtime hash.")]
        public bool isGlobal = true;
        public RenderingData renderingData;

        public class Baker : Baker<TilemapAuthoring>
        {
            public override void Bake(TilemapAuthoring authoring)
            {
                var gridAuthoring = GetComponentInParent<GridAuthoring>();
                if (gridAuthoring == null)
                {
                    throw new Exception("GridAuthoring not found");
                }
                
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                var tilePivot = float2.zero;
                var tileSize = float2.zero;
                var refSprite = new RefSprite();
                
                BakerUtils.AddTilemapTransform(this, entity, authoring.renderingData, gridAuthoring);
                BakerUtils.AddIntGridLayerData(this, entity, authoring.intGrid, authoring.isGlobal, refSprite,
                    false, ref tilePivot, ref tileSize, out var runtimeHash);
                BakerUtils.AddRenderingData(this, authoring.gameObject, entity, runtimeHash, authoring.renderingData, refSprite);
            }
        }
    }
}
