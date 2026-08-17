using System;
using FireAlt.Core.Collections;
using FireAlt.Core.Extensions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct TilemapIntGridSingleton : IComponentData, IDisposable
    {
        public struct IntGridLayer : IDisposable
        {
            public UnsafeHashMap<int2, IntGridValue> IntGrid;
            public UnsafeHashMap<int2, int> RuleGrid;
        
            public UnsafeHashMap<int2, SpriteMesh> RenderedSprites;
            public UnsafeHashMap<int2, Entity> SpawnedEntities;

            public UnsafeHashSet<int2> ChangedPositions;
            public UnsafeHashSet<int2> PositionsToRefresh;
            public UnsafeList<int2> RefreshedPositions;

            public bool Cleared;
            public bool DestroySpawnedEntities;

            public bool DualGrid;
            public bool IsTerrainLayer;
            public Entity IntGridEntity;
            
            public IntGridLayer(int capacity, Allocator allocator, IntGridData intGridData, bool isTerrainLayer, Entity intGridEntity)
            {
                IntGrid = new UnsafeHashMap<int2, IntGridValue>(capacity, allocator);
                RuleGrid = new UnsafeHashMap<int2, int>(capacity, allocator);
                
                ChangedPositions = new UnsafeHashSet<int2>(capacity, allocator);
                PositionsToRefresh = new UnsafeHashSet<int2>(capacity, allocator);
            
                SpawnedEntities = new UnsafeHashMap<int2, Entity>(capacity, allocator);
                RenderedSprites = new UnsafeHashMap<int2, SpriteMesh>(capacity, allocator);
            
                RefreshedPositions = new UnsafeList<int2>(capacity, allocator);

                Cleared = false;
                DestroySpawnedEntities = false;
                
                DualGrid = intGridData.DualGrid;
                IsTerrainLayer = isTerrainLayer;
                IntGridEntity = intGridEntity;
            }

            public void Reset(IntGridData intGridData, bool isTerrainLayer, Entity intGridEntity)
            {
                IntGrid.Clear();
                RuleGrid.Clear();
                RenderedSprites.Clear();
                ChangedPositions.Clear();
                PositionsToRefresh.Clear();
                RefreshedPositions.Clear();

                Cleared = true;
                DestroySpawnedEntities = SpawnedEntities.Count != 0;
                DualGrid = intGridData.DualGrid;
                IsTerrainLayer = isTerrainLayer;
                IntGridEntity = intGridEntity;
            }

            public void SetValue(int2 position, IntGridValue value)
            {
                ChangedPositions.Add(position);
                if (value == 0)
                {
                    IntGrid.Remove(position);
                }
                else
                {
                    IntGrid[position] = value;
                }

                if (!DualGrid) return;

                ChangedPositions.Add(position + new int2(-1, 0));
                ChangedPositions.Add(position + new int2(0, -1));
                ChangedPositions.Add(position + new int2(-1, -1));
            }
            
            public void Dispose()
            {
                IntGrid.Dispose();
                RuleGrid.Dispose();
                
                ChangedPositions.Dispose();
                PositionsToRefresh.Dispose();
            
                SpawnedEntities.Dispose();
                RenderedSprites.Dispose();

                RefreshedPositions.Dispose();
            }
        }
        
        public NativeHashMap<Hash128, IntGridLayer> IntGridLayers;

        // Store entity commands on a singleton to sort it later and instantiate using batch API
        public NativeThreadToListMapper<EntityCommand> EntityCommands;
            
        public bool TryRegisterIntGridLayer(IntGridData intGridData, bool terrainLayer, Entity intGridEntity,
            in ComponentLookup<IntGridData> intGridDataLookup)
        {
            if (IntGridLayers.TryGetValue(intGridData.Hash, out var existing))
            {
                if (existing.IntGridEntity == intGridEntity)
                {
                    ref var layer = ref IntGridLayers.GetValueAsRef(intGridData.Hash);
                    layer.Reset(intGridData, terrainLayer, intGridEntity);
                    return true;
                }

                if (intGridDataLookup.HasComponent(existing.IntGridEntity)) return false;

                existing.Dispose();
                IntGridLayers.Remove(intGridData.Hash);
            }
                
            IntGridLayers.Add(intGridData.Hash, new IntGridLayer(64, Allocator.Persistent, intGridData, terrainLayer, intGridEntity));
            return true;
        }
            
        public void Dispose()
        {
            foreach (var layer in IntGridLayers)
            {
                layer.Value.Dispose();
            }
            IntGridLayers.Dispose();
            EntityCommands.Dispose();
        }

        public ReadOnly AsReadOnly()
        {
            return new ReadOnly { IntGridLayers = IntGridLayers.AsReadOnly() };
        }
            
        public struct ReadOnly
        {
            public NativeHashMap<Hash128, IntGridLayer>.ReadOnly IntGridLayers;
                
            public bool TryGetIntGridValue(in Hash128 intGridHash, int2 position, out IntGridValue intGridValue)
            {
                var intGrid = IntGridLayers[intGridHash].IntGrid;
                return intGrid.TryGetValue(position, out intGridValue);
            }
        }
    }
}
