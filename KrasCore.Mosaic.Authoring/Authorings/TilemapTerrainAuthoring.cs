using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    public class TilemapTerrainAuthoring : MonoBehaviour
    {
        public List<IntGridDefinition> intGridLayers = new();
        public RenderingData renderingData;
        public int maxLayersBlend = 4;

        private void OnValidate()
        {
            maxLayersBlend = math.max(1, maxLayersBlend);

#if MOSAIC_BLEND_128
            var blendCapacity = new FixedList128Bytes<GpuTerrainTile>();
#else
            var blendCapacity = new FixedList64Bytes<GpuTerrainTile>();
#endif
            
            if (maxLayersBlend > blendCapacity.Capacity)
            {
#if MOSAIC_BLEND_128
                Debug.LogWarning("You are trying to exceed a maximum blend FixedList capacity");
#else
                Debug.LogWarning("You are trying to exceed a maximum blend FixedList capacity. If you want more blends, consider adding a project define MOSAIC_BLEND_128");
#endif
                maxLayersBlend = blendCapacity.Capacity;
            }
        }
        
        private class Baker : Baker<TilemapTerrainAuthoring>
        {
            public override void Bake(TilemapTerrainAuthoring authoring)
            {
                if (authoring.intGridLayers.Count == 0) return;
                
                var gridAuthoring = GetComponentInParent<GridAuthoring>();
                if (gridAuthoring == null)
                {
                    throw new Exception("GridAuthoring not found");
                }
                
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                var layersBuffer = AddBuffer<TilemapTerrainLayerElement>(entity);
                var refSprite = new RefSprite();
                
                // Bake layers
                var intGridLayersEntities = new NativeArray<Entity>(authoring.intGridLayers.Count, Allocator.Temp);
                CreateAdditionalEntities(intGridLayersEntities, TransformUsageFlags.None);
                
                var tilePivot = float2.zero;
                var tileSize = float2.zero;
                for (int i = 0; i < intGridLayersEntities.Length; i++)
                {
                    var intGridLayerEntity = intGridLayersEntities[i];
                    
                    BakerUtils.AddTilemapTransform(this, intGridLayerEntity, authoring.renderingData, gridAuthoring);
                    BakerUtils.AddIntGridLayerData(this, intGridLayerEntity, authoring.intGridLayers[i], refSprite, true, ref tilePivot, ref tileSize);
                    layersBuffer.Add(new TilemapTerrainLayerElement { IntGridHash = authoring.intGridLayers[i].Hash });
                    AddComponent(intGridLayerEntity, new Data.TerrainLayer { TerrainEntity = entity });
                }

                // The system ensures that IntGrids are not shared, so we can just use the first one as hash
                var terrainHash = authoring.intGridLayers[0].Hash;
                
                // Bake terrain entity
                AddComponent(entity, new Data.TerrainData
                {
                    TerrainHash = terrainHash,
                    TileSize = tileSize,
                    MaxLayersBlend = authoring.maxLayersBlend,
                });

                BakerUtils.AddTilemapTransform(this, entity, authoring.renderingData, gridAuthoring);
                BakerUtils.AddRenderingData(this, entity, terrainHash, authoring.renderingData, refSprite);
            }
        }
    }
}