using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(TilemapCleanupSystemGroup))]
    public partial struct EntityCleanupSystem : ISystem
    {
        private NativeList<Entity> _entitiesToDelete;
        private NativeList<Hash128> _layersToDelete;
        
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _entitiesToDelete = new NativeList<Entity>(256, Allocator.Persistent);
            _layersToDelete = new NativeList<Hash128>(16, Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _entitiesToDelete.Dispose();
            _layersToDelete.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.EntityManager.CompleteDependencyBeforeRW<TilemapIntGridSingleton>();
            state.EntityManager.CompleteDependencyBeforeRW<TilemapCommandBufferSingleton>();
            var dataSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW;
            var commandSingleton = SystemAPI.GetSingletonRW<TilemapCommandBufferSingleton>().ValueRW;
            var entityLookup = SystemAPI.GetEntityStorageInfoLookup();
            var intGridDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true);
            
            foreach (var kvp in dataSingleton.IntGridLayers)
            {
                ref var dataLayer = ref kvp.Value;
                ref var spawnedEntities = ref dataLayer.SpawnedEntities;

                if (!intGridDataLookup.HasComponent(dataLayer.IntGridEntity))
                {
                    foreach (var spawnedEntity in spawnedEntities)
                    {
                        if (entityLookup.Exists(spawnedEntity.Value.Entity)) _entitiesToDelete.Add(spawnedEntity.Value.Entity);
                    }

                    spawnedEntities.Clear();
                    _layersToDelete.Add(kvp.Key);
                    continue;
                }

                if (dataLayer.DestroySpawnedEntities ||
                    dataLayer.RuleGrid.Count == 0 && dataLayer.SpawnedEntities.Count() != 0)
                {
                    foreach (var kvPair in dataLayer.SpawnedEntities)
                    {
                        if (entityLookup.Exists(kvPair.Value.Entity)) _entitiesToDelete.Add(kvPair.Value.Entity);
                    }
                    dataLayer.SpawnedEntities.Clear();
                    dataLayer.DestroySpawnedEntities = false;
                }
                else
                {
                    foreach (var removedPos in dataLayer.RefreshedPositions)
                    {
                        if (!spawnedEntities.TryGetFirstValue(removedPos, out var spawned, out var iterator)) continue;
                        do
                        {
                            if (entityLookup.Exists(spawned.Entity)) _entitiesToDelete.Add(spawned.Entity);
                        }
                        while (spawnedEntities.TryGetNextValue(out spawned, ref iterator));
                        spawnedEntities.Remove(removedPos);
                    }

                    if (dataLayer.RuleGrid.Count == 0 && (dataLayer.Cleared || !dataLayer.RefreshedPositions.IsEmpty))
                        spawnedEntities.Clear();
                }
            }

            if (_entitiesToDelete.Length != 0)
            {
                state.EntityManager.DestroyEntity(_entitiesToDelete.AsArray());
                _entitiesToDelete.Clear();
            }

            foreach (var hash in _layersToDelete)
            {
                if (dataSingleton.IntGridLayers.TryGetValue(hash, out var dataLayer))
                {
                    dataLayer.Dispose();
                    dataSingleton.IntGridLayers.Remove(hash);
                }

                if (commandSingleton.IntGridLayers.TryGetValue(hash, out var commandLayer))
                {
                    commandLayer.Dispose();
                    commandSingleton.IntGridLayers.Remove(hash);
                }
            }

            _layersToDelete.Clear();
        }
    }
}
