using FireAlt.Core.Extensions;
using FireAlt.Core.Groups;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(RuntimeBakingSystemGroup), OrderLast = true)]
    public partial class MosaicInitializationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            CleanupRenderers();
            InitializeRenderers();
        
            Dependency = new RegisterJob
            {
                TilemapTerrainLayerTagLookup = SystemAPI.GetComponentLookup<Data.TerrainLayer>(true),
                IntGridDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true),
                IntGridLayers = SystemAPI.GetSingletonRW<TilemapCommandBufferSingleton>().ValueRW.IntGridLayers,
                DataTilemapIntGridSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW,
            }.Schedule(Dependency);
            
            Dependency = new UpdateTilemapRendererDataJob
            {
                GridDataLookup = SystemAPI.GetComponentLookup<GridData>(true)
            }.Schedule(Dependency);
        }

        private void CleanupRenderers()
        {
            var cleanupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MosaicRendererCleanup>()
                .WithNone<TilemapRendererData>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities)
                .Build(EntityManager);
            if (cleanupQuery.IsEmpty) return;

            EntityManager.CompleteDependencyBeforeRW<IntGridMeshDataSystem.Singleton>();
            EntityManager.CompleteDependencyBeforeRW<TerrainMeshDataSystem.Singleton>();
            ref var tilemapSingleton = ref SystemAPI.GetSingletonRW<IntGridMeshDataSystem.Singleton>().ValueRW;
            ref var terrainSingleton = ref SystemAPI.GetSingletonRW<TerrainMeshDataSystem.Singleton>().ValueRW;
            var presentationData = SystemAPI.GetSingleton<PresentationDataSingleton>().Value.Value;
            var entitiesGraphicsSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

            foreach (var entity in cleanupQuery.ToEntityArray(Allocator.Temp))
            {
                var cleanup = EntityManager.GetComponentData<MosaicRendererCleanup>(entity);
                entitiesGraphicsSystem?.UnregisterMesh(cleanup.MeshID);
                entitiesGraphicsSystem?.UnregisterMaterial(cleanup.MaterialID);
                ReleaseRenderer(presentationData, cleanup.MeshHash, entity, cleanup.IsTerrain,
                    ref tilemapSingleton, ref terrainSingleton);
            }

            EntityManager.RemoveComponent<MosaicRendererCleanup>(cleanupQuery);
        }

        private void InitializeRenderers()
        {
            var rendererQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData, RuntimeMaterial>()
                .Build(EntityManager);
            if (rendererQuery.IsEmpty) return;

            var presentationData = SystemAPI.GetSingleton<PresentationDataSingleton>().Value.Value;
            if (presentationData == null || !presentationData.IsCreated) return;

            var entitiesGraphicsSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            if (entitiesGraphicsSystem == null) return;

            ref var tilemapSingleton = ref SystemAPI.GetSingletonRW<IntGridMeshDataSystem.Singleton>().ValueRW;
            ref var terrainSingleton = ref SystemAPI.GetSingletonRW<TerrainMeshDataSystem.Singleton>().ValueRW;
            var entities = rendererQuery.ToEntityArray(Allocator.Temp);
            var rendererData = rendererQuery.ToComponentDataArray<TilemapRendererData>(Allocator.Temp);
            var runtimeMaterials = rendererQuery.ToComponentDataArray<RuntimeMaterial>(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var renderingData = rendererData[i];
                var isTerrain = EntityManager.HasComponent<Data.TerrainData>(entity);

                if (EntityManager.HasComponent<MosaicRendererCleanup>(entity))
                {
                    var existingCleanup = EntityManager.GetComponentData<MosaicRendererCleanup>(entity);
                    if (IsRendererInitialized(entity, renderingData.MeshHash, isTerrain, existingCleanup,
                            presentationData, entitiesGraphicsSystem))
                    {
                        AddRendererEntity(entity, isTerrain, ref tilemapSingleton, ref terrainSingleton);
                        EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, true);
                        continue;
                    }

                    entitiesGraphicsSystem.UnregisterMesh(existingCleanup.MeshID);
                    entitiesGraphicsSystem.UnregisterMaterial(existingCleanup.MaterialID);
                    ReleaseRenderer(presentationData, existingCleanup.MeshHash, entity, existingCleanup.IsTerrain,
                        ref tilemapSingleton, ref terrainSingleton);
                }

                if (presentationData.RenderingEntityMap.TryGetValue(renderingData.MeshHash, out var registeredEntity))
                {
                    if (EntityManager.HasComponent<TilemapRendererData>(registeredEntity))
                    {
                        EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, false);
                        Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {renderingData.MeshHash}");
                        continue;
                    }

                    var wasTerrain = presentationData.TerrainMap.ContainsKey(renderingData.MeshHash);
                    ReleaseRenderer(presentationData, renderingData.MeshHash, registeredEntity, wasTerrain,
                        ref tilemapSingleton, ref terrainSingleton);
                }

                var mesh = presentationData.GetOrCreateMesh(renderingData.MeshHash);
                var material = runtimeMaterials[i].Value.Value;
                if (isTerrain)
                {
                    if (!presentationData.TerrainMap.TryGetValue(renderingData.MeshHash, out var terrainRenderingData))
                    {
                        terrainRenderingData = ScriptableObject.CreateInstance<TilemapTerrainRenderingData>();
                        terrainRenderingData.Init(new Material(material));
                        presentationData.TerrainMap.Add(renderingData.MeshHash, terrainRenderingData);
                    }

                    material = terrainRenderingData.Material;
                }

                AddRendererEntity(entity, isTerrain, ref tilemapSingleton, ref terrainSingleton);

                var description = new RenderMeshDescription(
                    renderingData.ShadowCastingMode,
                    renderingData.ReceiveShadows,
                    layer: renderingData.LayerMask,
                    renderingLayerMask: renderingData.RenderingLayerMask);
                var meshID = entitiesGraphicsSystem.RegisterMesh(mesh);
                var materialID = entitiesGraphicsSystem.RegisterMaterial(material);
                RenderMeshUtility.AddComponents(entity, EntityManager, description,
                    new MaterialMeshInfo(materialID, meshID));

                var cleanup = new MosaicRendererCleanup
                {
                    MeshHash = renderingData.MeshHash,
                    MeshID = meshID,
                    MaterialID = materialID,
                    IsTerrain = isTerrain,
                };
                if (EntityManager.HasComponent<MosaicRendererCleanup>(entity))
                {
                    EntityManager.SetComponentData(entity, cleanup);
                }
                else
                {
                    EntityManager.AddComponentData(entity, cleanup);
                }

                presentationData.RenderingEntityMap[renderingData.MeshHash] = entity;
                EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, true);
            }

            tilemapSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(tilemapSingleton.RenderingEntities.Length);
            terrainSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(terrainSingleton.RenderingEntities.Length);
        }

        private bool IsRendererInitialized(Entity entity, Hash128 hash, bool isTerrain,
            MosaicRendererCleanup cleanup, PresentationDataObject presentationData,
            EntitiesGraphicsSystem entitiesGraphicsSystem)
        {
            if (cleanup.MeshHash != hash || cleanup.IsTerrain != isTerrain
                || !presentationData.RenderingEntityMap.TryGetValue(hash, out var registeredEntity)
                || registeredEntity != entity
                || !presentationData.MeshMap.TryGetValue(hash, out var mesh)
                || entitiesGraphicsSystem.GetMesh(cleanup.MeshID) != mesh
                || entitiesGraphicsSystem.GetMaterial(cleanup.MaterialID) == null
                || isTerrain && !presentationData.TerrainMap.ContainsKey(hash)
                || !EntityManager.HasComponent<MaterialMeshInfo>(entity))
            {
                return false;
            }

            var materialMeshInfo = EntityManager.GetComponentData<MaterialMeshInfo>(entity);
            return materialMeshInfo.MeshID == cleanup.MeshID
                   && materialMeshInfo.MaterialID == cleanup.MaterialID;
        }

        private static void AddRendererEntity(Entity entity, bool isTerrain,
            ref IntGridMeshDataSystem.Singleton tilemapSingleton,
            ref TerrainMeshDataSystem.Singleton terrainSingleton)
        {
            if (isTerrain)
            {
                AddUnique(ref terrainSingleton.RenderingEntities, entity);
            }
            else
            {
                AddUnique(ref tilemapSingleton.RenderingEntities, entity);
            }
        }

        private static void ReleaseRenderer(PresentationDataObject presentationData, Hash128 hash, Entity entity,
            bool isTerrain, ref IntGridMeshDataSystem.Singleton tilemapSingleton,
            ref TerrainMeshDataSystem.Singleton terrainSingleton)
        {
            var ownsRegistration = presentationData != null
                                   && presentationData.RenderingEntityMap.TryGetValue(hash, out var registeredEntity)
                                   && registeredEntity == entity;
            presentationData?.ReleaseEntity(hash, entity);
            if (isTerrain)
            {
                RemoveEntity(ref terrainSingleton.RenderingEntities, entity);
                if (terrainSingleton.Terrains.TryGetValue(hash, out var terrain)
                    && terrain.TerrainEntity == entity)
                {
                    terrain.Dispose();
                    terrainSingleton.Terrains.Remove(hash);
                    RemoveHash(ref terrainSingleton.HashesToUpdate, hash);
                    terrainSingleton.UpdatedMeshBoundsMap.Remove(hash);
                }

                if (ownsRegistration) presentationData.ReleaseTerrain(hash);
            }
            else
            {
                RemoveEntity(ref tilemapSingleton.RenderingEntities, entity);
                if (tilemapSingleton.Tilemaps.TryGetValue(hash, out var tilemap)
                    && tilemap.IntGridEntity == entity)
                {
                    tilemap.Dispose();
                    tilemapSingleton.Tilemaps.Remove(hash);
                    RemoveHash(ref tilemapSingleton.HashesToUpdate, hash);
                    tilemapSingleton.UpdatedMeshBoundsMap.Remove(hash);
                }
            }
        }

        private static void AddUnique(ref NativeList<Entity> entities, Entity entity)
        {
            foreach (var candidate in entities)
            {
                if (candidate == entity) return;
            }

            entities.Add(entity);
        }

        private static void RemoveEntity(ref NativeList<Entity> entities, Entity entity)
        {
            for (var i = entities.Length - 1; i >= 0; i--)
            {
                if (entities[i] == entity) entities.RemoveAtSwapBack(i);
            }
        }

        private static void RemoveHash(ref NativeList<Hash128> hashes, Hash128 hash)
        {
            for (var i = hashes.Length - 1; i >= 0; i--)
            {
                if (hashes[i] == hash) hashes.RemoveAtSwapBack(i);
            }
        }

        [BurstCompile]
        [WithDisabled(typeof(IntGridData))]
        private partial struct RegisterJob : IJobEntity
        {
            [ReadOnly]
            public ComponentLookup<Data.TerrainLayer> TilemapTerrainLayerTagLookup;

            [ReadOnly]
            [NativeDisableContainerSafetyRestriction]
            public ComponentLookup<IntGridData> IntGridDataLookup;
            
            public NativeHashMap<Hash128, TilemapCommandBufferSingleton.IntGridLayer> IntGridLayers;
            public TilemapIntGridSingleton DataTilemapIntGridSingleton;
            
            private void Execute(ref IntGridData intGridData, EnabledRefRW<IntGridData> enabled,
                in DynamicBuffer<IntGridInitialValueElement> initialValues, Entity entity)
            {
                var isTerrainLayer = TilemapTerrainLayerTagLookup.HasComponent(entity);

                if (DataTilemapIntGridSingleton.TryRegisterIntGridLayer(
                        intGridData, isTerrainLayer, entity, IntGridDataLookup)
                    && TryRegisterCommandLayer(intGridData.Hash))
                {
                    ref var layer = ref DataTilemapIntGridSingleton.IntGridLayers.GetValueAsRef(intGridData.Hash);
                    foreach (var initialValue in initialValues)
                    {
                        layer.SetValue(initialValue.Position, initialValue.Value);
                    }

                    enabled.ValueRW = true;
                }
                else
                {
                    Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {intGridData.DebugName}");
                }
            }

            private bool TryRegisterCommandLayer(Hash128 intGridHash)
            {
                if (IntGridLayers.ContainsKey(intGridHash))
                {
                    ref var existing = ref IntGridLayers.GetValueAsRef(intGridHash);
                    existing.SetCommands.Clear();
                    existing.ClearCommand = false;
                    return true;
                }

                IntGridLayers.Add(intGridHash,
                    new TilemapCommandBufferSingleton.IntGridLayer(256, Allocator.Persistent));
                return true;
            }

        }

        [BurstCompile]
        private partial struct UpdateTilemapRendererDataJob : IJobEntity
        {
            [ReadOnly]
            public ComponentLookup<GridData> GridDataLookup;
            
            private void Execute(ref TilemapTransform rendererData)
            {
                var gridData = GridDataLookup[rendererData.GridEntity];
                rendererData.Swizzle = gridData.Swizzle;
                rendererData.CellSize = gridData.CellSize;
            }
        }
    }
}
