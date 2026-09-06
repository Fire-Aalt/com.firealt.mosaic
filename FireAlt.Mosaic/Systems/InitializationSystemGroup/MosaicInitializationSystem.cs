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
        private const uint LOCAL_HASH_NAMESPACE_A = 0x4D4F5341;
        private const uint LOCAL_HASH_NAMESPACE_B = 0x4C4F434C;

        protected override void OnUpdate()
        {
            AssignRuntimeHashes(EntityManager);
            CleanupRenderers();
            var staleHashes = new NativeHashSet<Hash128>(
                SystemAPI.GetSingleton<TilemapIntGridSingleton>().IntGridLayers.Count + 1, WorldUpdateAllocator);
            InitializeRenderers(ref staleHashes);
        
            Dependency = new RegisterJob
            {
                TilemapTerrainLayerTagLookup = SystemAPI.GetComponentLookup<Data.TerrainLayer>(true),
                IntGridDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true),
                EntityGuidLookup = SystemAPI.GetComponentLookup<EntityGuid>(true),
                IntGridLayers = SystemAPI.GetSingletonRW<TilemapCommandBufferSingleton>().ValueRW.IntGridLayers,
                DataTilemapIntGridSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW,
                StaleHashes = staleHashes,
            }.Schedule(Dependency);
            
            Dependency = new UpdateTilemapRendererDataJob
            {
                GridDataLookup = SystemAPI.GetComponentLookup<GridData>(true)
            }.Schedule(Dependency);
        }

        internal static void AssignRuntimeHashes(EntityManager entityManager)
        {
            var intGridQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in intGridQuery.ToEntityArray(Allocator.Temp))
            {
                var intGridData = entityManager.GetComponentData<IntGridData>(entity);
                if (intGridData.Hash != default) continue;

                intGridData.Hash = CreateRuntimeHash(entity);
                entityManager.SetComponentData(entity, intGridData);
            }
            intGridQuery.Dispose();

            var rendererQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData>()
                .Build(entityManager);
            foreach (var entity in rendererQuery.ToEntityArray(Allocator.Temp))
            {
                var rendererData = entityManager.GetComponentData<TilemapRendererData>(entity);
                if (rendererData.MeshHash != default) continue;

                if (entityManager.HasComponent<IntGridData>(entity))
                {
                    rendererData.MeshHash = entityManager.GetComponentData<IntGridData>(entity).Hash;
                }
                else if (entityManager.HasBuffer<TilemapTerrainLayerElement>(entity))
                {
                    var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(entity, true);
                    if (!layers.IsEmpty && entityManager.HasComponent<IntGridData>(layers[0].IntGridEntity))
                    {
                        rendererData.MeshHash =
                            entityManager.GetComponentData<IntGridData>(layers[0].IntGridEntity).Hash;
                    }
                }

                if (rendererData.MeshHash != default) entityManager.SetComponentData(entity, rendererData);
            }
            rendererQuery.Dispose();
        }

        private static Hash128 CreateRuntimeHash(Entity entity)
        {
            return new Hash128((uint)entity.Index, (uint)entity.Version,
                LOCAL_HASH_NAMESPACE_A, LOCAL_HASH_NAMESPACE_B);
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

        private void InitializeRenderers(ref NativeHashSet<Hash128> staleHashes)
        {
            var rendererQuery = SystemAPI.QueryBuilder()
                .WithAll<TilemapRendererData, RuntimeMaterial>()
                .Build();
            if (rendererQuery.IsEmpty) return;

            var presentationData = SystemAPI.GetSingleton<PresentationDataSingleton>().Value.Value;
            var entitiesGraphicsSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

            ref var tilemapSingleton = ref SystemAPI.GetSingletonRW<IntGridMeshDataSystem.Singleton>().ValueRW;
            ref var terrainSingleton = ref SystemAPI.GetSingletonRW<TerrainMeshDataSystem.Singleton>().ValueRW;
            var entities = rendererQuery.ToEntityArray(Allocator.Temp);
            var rendererData = rendererQuery.ToComponentDataArray<TilemapRendererData>(Allocator.Temp);
            var runtimeMaterials = rendererQuery.ToComponentDataArray<RuntimeMaterial>(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var renderingData = rendererData[i];
                var material = runtimeMaterials[i].Value.Value;
                if (material == null)
                {
                    // Domain reload destroys generated RuntimeMaterials while their disabled lookup survives.
                    // Re-enable the lookup and keep Mosaic pending until RuntimeMaterialSystem recreates it.
                    if (EntityManager.HasComponent<RuntimeMaterialLookup>(entity))
                    {
                        EntityManager.SetComponentEnabled<RuntimeMaterialLookup>(entity, true);
                    }

                    EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, false);
                    continue;
                }

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
                        if (IsStaleSceneEntity(EntityManager, registeredEntity))
                        {
                            AddStaleHashes(registeredEntity, renderingData.MeshHash, ref staleHashes);
                            continue;
                        }

                        if (IsSameBakedEntity(registeredEntity, entity)) continue;

                        Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {renderingData.MeshHash}");
                        continue;
                    }

                    var wasTerrain = presentationData.TerrainMap.ContainsKey(renderingData.MeshHash);
                    ReleaseRenderer(presentationData, renderingData.MeshHash, registeredEntity, wasTerrain,
                        ref tilemapSingleton, ref terrainSingleton);
                }

                var mesh = presentationData.GetOrCreateMesh(renderingData.MeshHash);
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

        private bool IsSameBakedEntity(Entity left, Entity right)
        {
            if (!EntityManager.HasComponent<EntityGuid>(left) || !EntityManager.HasComponent<EntityGuid>(right))
            {
                return false;
            }

            var leftGuid = EntityManager.GetComponentData<EntityGuid>(left);
            return IsSameBakedSource(leftGuid, EntityManager.GetComponentData<EntityGuid>(right));
        }

        internal static bool IsSameBakedSource(EntityGuid left, EntityGuid right)
        {
            return left != EntityGuid.Null && right != EntityGuid.Null
                   && left.OriginatingEntityId == right.OriginatingEntityId
                   && left.OriginatingSubEntityId == right.OriginatingSubEntityId
                   && left.Serial == right.Serial;
        }

        internal static bool IsStaleSceneEntity(EntityManager entityManager, Entity entity)
        {
            if (!entityManager.HasComponent<SceneTag>(entity)) return false;

            var sceneEntity = entityManager.GetSharedComponent<SceneTag>(entity).SceneEntity;
            return sceneEntity != Entity.Null && !entityManager.Exists(sceneEntity);
        }

        private void AddStaleHashes(Entity entity, Hash128 rendererHash, ref NativeHashSet<Hash128> staleHashes)
        {
            staleHashes.Add(rendererHash);
            if (!EntityManager.HasBuffer<TilemapTerrainLayerElement>(entity)) return;

            foreach (var layer in EntityManager.GetBuffer<TilemapTerrainLayerElement>(entity))
            {
                if (EntityManager.HasComponent<IntGridData>(layer.IntGridEntity))
                {
                    staleHashes.Add(EntityManager.GetComponentData<IntGridData>(layer.IntGridEntity).Hash);
                }
            }
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

            [ReadOnly]
            public ComponentLookup<EntityGuid> EntityGuidLookup;
            
            public NativeHashMap<Hash128, TilemapCommandBufferSingleton.IntGridLayer> IntGridLayers;
            public TilemapIntGridSingleton DataTilemapIntGridSingleton;

            [ReadOnly]
            public NativeHashSet<Hash128> StaleHashes;
            
            private void Execute(ref IntGridData intGridData, EnabledRefRW<IntGridData> enabled,
                in DynamicBuffer<IntGridInitialValueElement> initialValues, Entity entity)
            {
                var isTerrainLayer = TilemapTerrainLayerTagLookup.HasComponent(entity);
                if (DataTilemapIntGridSingleton.IntGridLayers.TryGetValue(intGridData.Hash, out var existing)
                    && existing.IntGridEntity != entity
                    && (StaleHashes.Contains(intGridData.Hash)
                        || IsSameBakedEntity(existing.IntGridEntity, entity)))
                {
                    return;
                }

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

            private bool IsSameBakedEntity(Entity left, Entity right)
            {
                return EntityGuidLookup.TryGetComponent(left, out var leftGuid)
                       && EntityGuidLookup.TryGetComponent(right, out var rightGuid)
                       && IsSameBakedSource(leftGuid, rightGuid);
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
