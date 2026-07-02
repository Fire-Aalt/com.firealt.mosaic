using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Authoring
{
    public class TilemapAuthoring : MonoBehaviour
    {
        public IntGridDefinition intGrid;
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
                BakerUtils.AddIntGridLayerData(this, entity, authoring.intGrid, refSprite, false, ref tilePivot, ref tileSize);
                BakerUtils.AddRenderingData(this, authoring.gameObject, entity, authoring.intGrid.Hash, authoring.renderingData, refSprite);
            }
        }
    }
}
