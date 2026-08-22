using System;
using System.Collections.Generic;
using FireAlt.Core.EntityCommands;
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
        [Tooltip("True: to access the runtime IntGrid of this tilemap, the IntGridDefinition can be used directly.\n\n" +
                 "False: the hash of the runtime IntGrid is unique per each instance and the entity needs to be queried to get the runtime hash.")]
        public bool isGlobal = true;
        public RenderingData renderingData = new();
        public int maxLayersBlend = 4;

        [SerializeField, HideInInspector] private List<SerializedIntGridLayer> _paintedLayers = new();

        public IReadOnlyList<SerializedIntGridLayer> PaintedLayers => _paintedLayers;

        internal List<SerializedIntGridLayer> MutablePaintedLayers => _paintedLayers;

        private void Bake<TCommands>(ref TCommands commands, Entity gridEntity,
            Func<GameObject, Entity> entityResolver)
            where TCommands : IEntityCommands
        {
            if (intGridLayers.Count == 0) return;

            var uniqueDefinitions = new HashSet<IntGridDefinition>();
            foreach (var definition in intGridLayers)
            {
                if (definition != null && !uniqueDefinitions.Add(definition))
                {
                    throw new Exception($"Duplicate IntGridDefinition '{definition.name}' in terrain layers");
                }
            }

            var terrainEntity = commands.Entity;
            var layerEntities = new NativeArray<Entity>(intGridLayers.Count, Allocator.Temp);
            for (var i = 0; i < layerEntities.Length; i++)
            {
                layerEntities[i] = commands.CreateEntity();
            }

            commands.Entity = terrainEntity;
            var refSprite = new RefSprite();
            var rendererHash = default(Unity.Entities.Hash128);
            var tilePivot = float2.zero;
            var tileSize = float2.zero;

            for (var i = 0; i < layerEntities.Length; i++)
            {
                var intGridDefinition = intGridLayers[i];
                if (intGridDefinition == null)
                {
                    throw new Exception($"IntGridDefinition is null at terrain layer {i}");
                }

                var runtimeHash = BakerUtils.GetHash(intGridDefinition, isGlobal);
                if (i == 0) rendererHash = runtimeHash;

                commands.Entity = layerEntities[i];
                BakerUtils.AddTilemapTransform(ref commands, gridEntity, renderingData);
                BakerUtils.AddIntGridLayerData(ref commands, intGridDefinition, runtimeHash, refSprite, true,
                    ref tilePivot, ref tileSize, FindPaintedCells(intGridDefinition), entityResolver);
                commands.AddComponent(new Data.TerrainLayer { TerrainEntity = terrainEntity });
            }

            commands.Entity = terrainEntity;
            var layersBuffer = commands.AddBuffer<TilemapTerrainLayerElement>();
            foreach (var layerEntity in layerEntities)
            {
                layersBuffer.Add(new TilemapTerrainLayerElement { IntGridEntity = layerEntity });
            }

            commands.AddComponent(new Data.TerrainData
            {
                TileSize = tileSize,
                MaxLayersBlend = maxLayersBlend,
            });

            BakerUtils.AddTilemapTransform(ref commands, gridEntity, renderingData);
            BakerUtils.AddRenderingData(ref commands, gameObject, rendererHash, renderingData, refSprite);
        }

        private IReadOnlyList<SerializedIntGridCell> FindPaintedCells(IntGridDefinition definition)
        {
            foreach (var layer in _paintedLayers)
            {
                if (layer.IntGrid == definition) return layer.Cells;
            }

            return Array.Empty<SerializedIntGridCell>();
        }

        private void OnValidate()
        {
            maxLayersBlend = math.max(1, maxLayersBlend);
            SynchronizePaintedLayers();

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

        private void SynchronizePaintedLayers()
        {
            var existing = new Dictionary<IntGridDefinition, SerializedIntGridLayer>();
            foreach (var layer in _paintedLayers)
            {
                if (layer != null && layer.IntGrid != null)
                {
                    existing.TryAdd(layer.IntGrid, layer);
                }
            }

            _paintedLayers.Clear();
            foreach (var intGridLayer in intGridLayers)
            {
                if (intGridLayer == null) continue;

                if (!existing.TryGetValue(intGridLayer, out var paintedLayer))
                {
                    paintedLayer = new SerializedIntGridLayer(intGridLayer);
                }
                else
                {
                    paintedLayer.SetIntGrid(intGridLayer);
                }

                _paintedLayers.Add(paintedLayer);
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
                RegisterDependencies(authoring);

                var commands = new BakerCommands(this, entity);
                authoring.Bake(ref commands, GetEntity(gridAuthoring, TransformUsageFlags.None),
                    go => GetEntity(go, TransformUsageFlags.None));
            }

            private void RegisterDependencies(TilemapTerrainAuthoring authoring)
            {
                foreach (var intGridDefinition in authoring.intGridLayers)
                {
                    DependsOn(intGridDefinition);
                    if (intGridDefinition == null) continue;

                    foreach (var group in intGridDefinition.ruleGroups)
                    {
                        DependsOn(group);
                    }
                }
            }
        }
    }
}
