using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace FireAlt.Mosaic
{
    [UpdateInGroup(typeof(TilemapCleanupSystemGroup))]
    public partial struct EntityCleanupSystem : ISystem
    {
        private NativeList<Entity> _entitiesToDelete;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _entitiesToDelete = new NativeList<Entity>(256, Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _entitiesToDelete.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.CompleteDependencyBeforeRO<TilemapIntGridSingleton>();
            var dataSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW;
            
            foreach (var kvp in dataSingleton.IntGridLayers)
            {
                ref var dataLayer = ref kvp.Value;
                ref var spawnedEntities = ref dataLayer.SpawnedEntities;

                if (dataLayer.RuleGrid.Count == 0 && dataLayer.SpawnedEntities.Count != 0)
                {
                    foreach (var kvPair in dataLayer.SpawnedEntities)
                    {
                        _entitiesToDelete.Add(kvPair.Value);
                    }
                    dataLayer.SpawnedEntities.Clear();
                }
                else
                {
                    foreach (var removedPos in dataLayer.RefreshedPositions)
                    {
                        if (!spawnedEntities.TryGetValue(removedPos, out var entity)) continue;
                        spawnedEntities.Remove(removedPos);
                        _entitiesToDelete.Add(entity);
                    }
                }
            }

            if (_entitiesToDelete.Length != 0)
            {
                state.EntityManager.DestroyEntity(_entitiesToDelete.AsArray());
                _entitiesToDelete.Clear();
            }
        }
    }
}