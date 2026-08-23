using System;
using System.Collections.Generic;
using System.Linq;
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

        internal IEnumerable<ValidLayer> ValidLayers()
        {
            var index = 0;
            foreach (var definition in intGridLayers)
            {
                if (definition == null) continue;
                yield return new ValidLayer(index++, definition);
            }
        }

        private void Bake<TCommands>(ref TCommands commands, Entity gridEntity,
            Func<GameObject, Entity> entityResolver, IReadOnlyList<ValidLayer> validLayers)
            where TCommands : IEntityCommands
        {
            ValidateUniqueLayers(validLayers);

            var terrainEntity = commands.Entity;
            var layerEntities = new NativeArray<Entity>(validLayers.Count, Allocator.Temp);
            for (var i = 0; i < layerEntities.Length; i++)
            {
                layerEntities[i] = commands.CreateEntity();
            }

            commands.Entity = terrainEntity;
            var refSprite = new RefSprite();
            var rendererHash = default(Unity.Entities.Hash128);
            var tilePivot = float2.zero;
            var tileSize = float2.zero;

            foreach (var layer in validLayers)
            {
                var intGridDefinition = layer.Definition;
                var runtimeHash = BakerUtils.GetHash(intGridDefinition, isGlobal);
                if (layer.Index == 0) rendererHash = runtimeHash;

                commands.Entity = layerEntities[layer.Index];
                BakerUtils.AddTilemapTransform(ref commands, gridEntity, renderingData);
                BakerUtils.AddIntGridLayerData(ref commands, intGridDefinition, runtimeHash, refSprite, true,
                    ref tilePivot, ref tileSize, FindPaintedRectangles(intGridDefinition), entityResolver);
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

        internal static void ValidateUniqueLayers(IReadOnlyList<ValidLayer> validLayers)
        {
            var uniqueDefinitions = new HashSet<IntGridDefinition>();
            foreach (var layer in validLayers)
            {
                if (!uniqueDefinitions.Add(layer.Definition))
                {
                    throw new Exception($"Duplicate IntGridDefinition '{layer.Definition.name}' in terrain layers");
                }
            }
        }

        private IReadOnlyList<SerializedIntGridRectangle> FindPaintedRectangles(IntGridDefinition definition)
        {
            foreach (var layer in _paintedLayers)
            {
                if (layer.IntGrid == definition) return layer.PaintedData.Rectangles;
            }

            return Array.Empty<SerializedIntGridRectangle>();
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
            foreach (var layer in ValidLayers())
            {
                var intGridLayer = layer.Definition;

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
                foreach (var intGridDefinition in authoring.intGridLayers)
                {
                    BakerUtils.RegisterDependencies(this, intGridDefinition);
                }

                var validLayers = authoring.ValidLayers().ToArray();
                if (validLayers.Length == 0) return;
                
                var gridAuthoring = GetComponentInParent<GridAuthoring>();
                if (gridAuthoring == null)
                {
                    throw new Exception("GridAuthoring not found");
                }
                
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                var commands = new BakerCommands(this, entity);
                authoring.Bake(ref commands, GetEntity(gridAuthoring, TransformUsageFlags.None),
                    go => GetEntity(go, TransformUsageFlags.None), validLayers);
            }
        }

        internal readonly struct ValidLayer
        {
            public ValidLayer(int index, IntGridDefinition definition)
            {
                Index = index;
                Definition = definition;
            }

            public int Index { get; }

            public IntGridDefinition Definition { get; }
        }
    }
}
