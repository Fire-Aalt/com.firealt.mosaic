using FireAlt.Core.Extensions;
using FireAlt.Core.Groups;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic
{
    [UpdateInGroup(typeof(RuntimeBakingSystemGroup), OrderLast = true)]
    public partial class MosaicInitializationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var uninitializedQuery = SystemAPI.QueryBuilder().WithAll<TilemapRendererData, RuntimeMaterial>().WithNone<MaterialMeshInfo>().Build();
            if (!uninitializedQuery.IsEmpty)
            {
                var presentationDataObject = SystemAPI.GetSingleton<PresentationDataSingleton>().Value.Value;
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
                    
                    if (presentationDataObject.MeshMap.ContainsKey(tilemapRenderingData.MeshHash))
                    {
                        Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {tilemapRenderingData.MeshHash}");
                        continue;
                    };
                    
                    var mesh = new Mesh { name = "Mosaic.TilemapMesh" };
                    mesh.MarkDynamic();
                    presentationDataObject.MeshMap.Add(tilemapRenderingData.MeshHash, mesh);

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
                    
                    var meshId = entitiesGraphicsSystem.RegisterMesh(mesh);
                    var materialId = entitiesGraphicsSystem.RegisterMaterial(material);

                    var desc = new RenderMeshDescription(
                        tilemapRenderingData.ShadowCastingMode,
                        tilemapRenderingData.ReceiveShadows,
                        renderingLayerMask: tilemapRenderingData.RenderingLayerMask);
                    var materialMeshInfo = new MaterialMeshInfo(materialId, meshId);

                    RenderMeshUtility.AddComponents(entity, EntityManager, desc, materialMeshInfo);
                }

                tilemapSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(tilemapSingleton.RenderingEntities.Length);
                terrainSingleton.UpdatedMeshBoundsMap.EnsureMinCapacity(terrainSingleton.RenderingEntities.Length);
            }
        
            Dependency = new RegisterJob
            {
                TilemapTerrainLayerTagLookup = SystemAPI.GetComponentLookup<Data.TerrainLayer>(true),
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
            
            public NativeHashMap<Hash128, TilemapCommandBufferSingleton.IntGridLayer> IntGridLayers;
            public TilemapIntGridSingleton DataTilemapIntGridSingleton;
            
            private void Execute(ref IntGridData intGridData, EnabledRefRW<IntGridData> enabled, Entity entity)
            {
                var isTerrainLayer = TilemapTerrainLayerTagLookup.HasComponent(entity);

                if (TryRegisterIntGridLayer(intGridData.Hash) 
                    && DataTilemapIntGridSingleton.TryRegisterIntGridLayer(intGridData, isTerrainLayer, entity))
                {
                    enabled.ValueRW = true;
                }
                else
                {
                    Debug.LogError($"A duplicate registry attempt detected. This may happen if a TilemapTerrain and a Tilemap share the same IntGrid. Culprit: {intGridData.DebugName}");
                }
            }
            
            private bool TryRegisterIntGridLayer(Hash128 intGridHash)
            {
                if (IntGridLayers.ContainsKey(intGridHash)) return false;
            
                var layer = new TilemapCommandBufferSingleton.IntGridLayer(256, Allocator.Persistent);
                IntGridLayers.Add(intGridHash, layer);
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