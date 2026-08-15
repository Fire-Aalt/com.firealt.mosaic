using FireAlt.Core.Extensions;
using FireAlt.Core.Groups;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
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
            var uninitializedQuery = SystemAPI.QueryBuilder().WithAll<TilemapRendererData, RuntimeMaterial>()
                .WithDisabled<MosaicRendererInitialized>().Build();
            if (!uninitializedQuery.IsEmpty)
            {
                var presentationDataObject = SystemAPI.GetSingleton<PresentationDataSingleton>().Value.Value;
                if (presentationDataObject != null && presentationDataObject.IsCreated)
                {
                    var tilemapSingleton = SystemAPI.GetSingleton<IntGridMeshDataSystem.Singleton>();
                    var terrainSingleton = SystemAPI.GetSingleton<TerrainMeshDataSystem.Singleton>();
                    var entitiesGraphicsSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

                    var entities = uninitializedQuery.ToEntityArray(Allocator.Temp);
                    var rendererData = uninitializedQuery.ToComponentDataArray<TilemapRendererData>(Allocator.Temp);
                    var runtimeMaterials = uninitializedQuery.ToComponentDataArray<RuntimeMaterial>(Allocator.Temp);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        var tilemapRenderingData = rendererData[i];
                        var entity = entities[i];

                        if (presentationDataObject.RenderingEntityMap.TryGetValue(tilemapRenderingData.MeshHash,
                                out var registeredEntity))
                        {
                            if (registeredEntity == entity)
                            {
                                EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, true);
                                continue;
                            }

                            if (EntityManager.Exists(registeredEntity))
                            {
                                Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {tilemapRenderingData.MeshHash}");
                                continue;
                            }

                            if (presentationDataObject.MeshMap.Remove(tilemapRenderingData.MeshHash, out var staleMesh))
                            {
                                CoreUtils.Destroy(staleMesh);
                            }

                            if (presentationDataObject.TerrainMap.Remove(tilemapRenderingData.MeshHash,
                                    out var staleTerrain))
                            {
                                staleTerrain.Dispose();
                            }

                            presentationDataObject.RenderingEntityMap.Remove(tilemapRenderingData.MeshHash);
                        }

                        var mesh = new Mesh { name = "Mosaic.TilemapMesh" };
                        mesh.MarkDynamic();
                        presentationDataObject.MeshMap.Add(tilemapRenderingData.MeshHash, mesh);
                        presentationDataObject.RenderingEntityMap.Add(tilemapRenderingData.MeshHash, entity);

                        var material = runtimeMaterials[i].Value.Value;
                        if (EntityManager.HasComponent<Data.TerrainData>(entity))
                        {
                            material = new Material(material); // Force unique for terrains

                            var renderingData = ScriptableObject.CreateInstance<TilemapTerrainRenderingData>();
                            renderingData.Init(material);

                            presentationDataObject.TerrainMap.Add(tilemapRenderingData.MeshHash, renderingData);
                            terrainSingleton.RenderingEntities.Add(entity);
                        }
                        else
                        {
                            tilemapSingleton.RenderingEntities.Add(entity);
                        }

                        if (entitiesGraphicsSystem != null)
                        {
                            var meshId = entitiesGraphicsSystem.RegisterMesh(mesh);
                            var materialId = entitiesGraphicsSystem.RegisterMaterial(material);

                            var desc = new RenderMeshDescription(
                                tilemapRenderingData.ShadowCastingMode,
                                tilemapRenderingData.ReceiveShadows,
                                layer: tilemapRenderingData.LayerMask,
                                renderingLayerMask: tilemapRenderingData.RenderingLayerMask);
                            var materialMeshInfo = new MaterialMeshInfo(materialId, meshId);

                            RenderMeshUtility.AddComponents(entity, EntityManager, desc, materialMeshInfo);
                        }

                        EntityManager.SetComponentEnabled<MosaicRendererInitialized>(entity, true);
                    }

                    tilemapSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(tilemapSingleton.RenderingEntities.Length);
                    terrainSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(terrainSingleton.RenderingEntities.Length);
                }
            }
        
            Dependency = new RegisterJob
            {
                TilemapTerrainLayerTagLookup = SystemAPI.GetComponentLookup<Data.TerrainLayer>(true),
                EntityLookup = SystemAPI.GetEntityStorageInfoLookup(),
                IntGridLayers = SystemAPI.GetSingletonRW<TilemapCommandBufferSingleton>().ValueRW.IntGridLayers,
                DataTilemapIntGridSingleton = SystemAPI.GetSingletonRW<TilemapIntGridSingleton>().ValueRW,
            }.Schedule(Dependency);
            
            Dependency = new UpdateTilemapRendererDataJob
            {
                GridDataLookup = SystemAPI.GetComponentLookup<GridData>(true)
            }.Schedule(Dependency);
        }

        [BurstCompile]
        [WithDisabled(typeof(IntGridData))]
        private partial struct RegisterJob : IJobEntity
        {
            [ReadOnly]
            public ComponentLookup<Data.TerrainLayer> TilemapTerrainLayerTagLookup;

            [ReadOnly]
            public EntityStorageInfoLookup EntityLookup;
            
            public NativeHashMap<Hash128, TilemapCommandBufferSingleton.IntGridLayer> IntGridLayers;
            public TilemapIntGridSingleton DataTilemapIntGridSingleton;
            
            private void Execute(ref IntGridData intGridData, EnabledRefRW<IntGridData> enabled,
                in DynamicBuffer<IntGridInitialValueElement> initialValues, Entity entity)
            {
                var isTerrainLayer = TilemapTerrainLayerTagLookup.HasComponent(entity);

                if (DataTilemapIntGridSingleton.TryRegisterIntGridLayer(intGridData, isTerrainLayer, entity, EntityLookup)
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
