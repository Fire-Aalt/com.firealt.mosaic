using System;
using System.Runtime.InteropServices;
using FireAlt.Core;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;
using Mesh = UnityEngine.Mesh;

namespace FireAlt.Mosaic
{
	[WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
	[UpdateAfter(typeof(IntGridMeshDataSystem))]
	[UpdateInGroup(typeof(TilemapUpdateSystemGroup))]
    public partial struct TerrainMeshDataSystem : ISystem
    {
	    public struct Singleton : IComponentData, IDisposable
	    {
	        public struct Terrain : IDisposable
	        {
	            public Entity TerrainEntity;
		        
#if MOSAIC_BLEND_128
	            public UnsafeHashMap<int2, FixedList128Bytes<GpuTerrainTile>> RawTilesToBlend;
#else
	            public UnsafeHashMap<int2, FixedList64Bytes<GpuTerrainTile>> RawTilesToBlend;
#endif

	            public UnsafeList<GpuTerrainTile> TileBuffer;
	            public UnsafeList<GpuTerrainIndex> IndexBuffer;
	            
	            public Terrain(Entity terrainEntity, int capacity, Allocator allocator)
	            {
		            TerrainEntity = terrainEntity;
	                
#if MOSAIC_BLEND_128
	                RawTilesToBlend = new UnsafeHashMap<int2, FixedList128Bytes<GpuTerrainTile>>(capacity, allocator);
#else
	                RawTilesToBlend = new UnsafeHashMap<int2, FixedList64Bytes<GpuTerrainTile>>(capacity, allocator);
#endif

	                TileBuffer = new UnsafeList<GpuTerrainTile>(capacity, allocator);
	                IndexBuffer = new UnsafeList<GpuTerrainIndex>(capacity, allocator);
	            }
	            
	            public void Dispose()
	            {
	                RawTilesToBlend.Dispose();

	                TileBuffer.Dispose();
	                IndexBuffer.Dispose();
	            }
	        }
	        
	        public NativeArray<VertexAttributeDescriptor> Layout;
	        public NativeList<Hash128> HashesToUpdate;
	        public MeshDataArrayWrapper MeshDataArray;
	        public NativeParallelHashMap<Hash128, AABB> UpdatedMeshBoundsMap;
	        public NativeList<Entity> RenderingEntities;
	        
	        public NativeHashMap<Hash128, Terrain> Terrains;

	        public Singleton(int capacity, Allocator allocator)
	        {
	            Layout = new NativeArray<VertexAttributeDescriptor>(3, allocator);
	            Layout[0] = new VertexAttributeDescriptor(VertexAttribute.Position);
	            Layout[1] = new VertexAttributeDescriptor(VertexAttribute.Normal);
	            Layout[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);

	            HashesToUpdate = new NativeList<Hash128>(capacity, allocator);
	            MeshDataArray = default;
	            UpdatedMeshBoundsMap = new NativeParallelHashMap<Hash128, AABB>(capacity, allocator);
	            RenderingEntities = new NativeList<Entity>(capacity, allocator);
	            
	            Terrains = new NativeHashMap<Hash128, Terrain>(capacity, allocator);
	        }

	        public void Dispose()
	        {
	            Layout.Dispose();
	            HashesToUpdate.Dispose();
	            UpdatedMeshBoundsMap.Dispose();
	            RenderingEntities.Dispose();
	            
	            foreach (var kvp in Terrains)
	            {
	                kvp.Value.Dispose();
	            }
	            Terrains.Dispose();
	        }
	    }
	    
	    
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
	        state.EntityManager.CreateSingleton(new Singleton(1, Allocator.Persistent));
        }
        
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
	        SystemAPI.GetSingleton<Singleton>().Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var dataSingleton = SystemAPI.GetSingleton<TilemapIntGridSingleton>();
            var tcb = SystemAPI.GetSingleton<TilemapCommandBufferSingleton>();
            
            var singleton = SystemAPI.GetSingletonRW<Singleton>().ValueRW;

            var cullingBoundsChanged = !tcb.PrevCullingBounds.Value.Equals(tcb.CullingBounds.Value);
            tcb.PrevCullingBounds.Value = tcb.CullingBounds.Value;

            var terrainDataLookup = SystemAPI.GetComponentLookup<TerrainData>(true);
            
            state.Dependency = new FindHashesToUpdateJob
            {
	            IntGridLayers = dataSingleton.IntGridLayers,
	            IntGridDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true),
	            HashesToUpdate = singleton.HashesToUpdate,
	            CullingBoundsChanged = cullingBoundsChanged,
	            Terrains = singleton.Terrains
            }.Schedule(state.Dependency);

            state.Dependency = new PrepareAndCullSpriteMeshDataJob
            {
	            TerrainDataLookup = terrainDataLookup,
	            TerrainLayersBufferLookup = SystemAPI.GetBufferLookup<TilemapTerrainLayerElement>(true),
	            IntGridDataLookup = SystemAPI.GetComponentLookup<IntGridData>(true),
	            IntGridLayers = dataSingleton.IntGridLayers,
	            HashesToUpdate = singleton.HashesToUpdate.AsDeferredJobArray(),
	            Terrains = singleton.Terrains,
	            CullingBounds = tcb.CullingBounds.Value,
            }.Schedule(singleton.HashesToUpdate, 1, state.Dependency);
            
            state.Dependency = new GenerateTerrainMeshDataJob
            {
	            TerrainDataLookup = terrainDataLookup,
	            TilemapTransformLookup = SystemAPI.GetComponentLookup<TilemapTransform>(true),
	            Layout = singleton.Layout,
	            HashesToUpdate = singleton.HashesToUpdate.AsDeferredJobArray(),
	            Terrains = singleton.Terrains,
	            MeshDataArray = singleton.MeshDataArray.Array,
	            UpdatedMeshBoundsMapWriter = singleton.UpdatedMeshBoundsMap.AsParallelWriter()
            }.Schedule(singleton.HashesToUpdate, 1, state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(MosaicRendererInitialized))]
        private partial struct FindHashesToUpdateJob : IJobEntity
        {
            [ReadOnly]
            public NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> IntGridLayers;
            [ReadOnly]
            public ComponentLookup<IntGridData> IntGridDataLookup;
            
            public bool CullingBoundsChanged;
            public NativeList<Hash128> HashesToUpdate;
            public NativeHashMap<Hash128, Singleton.Terrain> Terrains;
            
            private void Execute(in TilemapRendererData rendererData,
                in DynamicBuffer<TilemapTerrainLayerElement> layers, Entity entity)
            {
                var terrainHash = rendererData.MeshHash;
                var created = false;
                if (Terrains.TryGetValue(terrainHash, out var existing)
                    && existing.TerrainEntity != entity)
                {
                    existing.Dispose();
                    Terrains.Remove(terrainHash);
                }

                if (!Terrains.ContainsKey(terrainHash))
                {
                    var terrain = new Singleton.Terrain(entity, 256, Allocator.Persistent);
                    Terrains.Add(terrainHash, terrain);
                    created = true;
                }

                if (created)
                {
                    HashesToUpdate.Add(terrainHash);
                    return;
                }
                
                foreach (var layer in layers)
                {
                    var intGridHash = IntGridDataLookup[layer.IntGridEntity].Hash;
                    ref var dataLayer = ref IntGridLayers.GetValueAsRef(intGridHash);
                    
                    if (!dataLayer.RefreshedPositions.IsEmpty || CullingBoundsChanged || dataLayer.Cleared)
                    {
                        HashesToUpdate.Add(terrainHash);
                        return;
                    }
                }
            }

        }
        
        [BurstCompile]
        private struct PrepareAndCullSpriteMeshDataJob : IJobParallelForDefer
        {
	        [ReadOnly]
	        public ComponentLookup<TerrainData> TerrainDataLookup;
            [ReadOnly]
            public BufferLookup<TilemapTerrainLayerElement> TerrainLayersBufferLookup;
            [ReadOnly]
            public ComponentLookup<IntGridData> IntGridDataLookup;
            
            [ReadOnly]
            public NativeArray<Hash128> HashesToUpdate;
            [ReadOnly]
            public NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> IntGridLayers;
            
            public AABB2D CullingBounds;
            
            [ReadOnly]
            public NativeHashMap<Hash128, Singleton.Terrain> Terrains;
            
            public void Execute(int index)
            {
                ref var terrainData = ref Terrains.GetValueAsRef(HashesToUpdate[index]);
                var terrainLayersBuffer = TerrainLayersBufferLookup[terrainData.TerrainEntity];
                var maxLayersBlend = TerrainDataLookup[terrainData.TerrainEntity].MaxLayersBlend;
                
                terrainData.RawTilesToBlend.Clear();
                
                for (int layerIndex = terrainLayersBuffer.Length - 1; layerIndex >= 0; layerIndex--)
                {
                    var intGridHash = IntGridDataLookup[terrainLayersBuffer[layerIndex].IntGridEntity].Hash;
                    var intGridLayer = IntGridLayers[intGridHash];

                    foreach (var kvp in intGridLayer.RenderedSprites)
                    {
	                    if (!CullingBounds.Contains(kvp.Key)) continue;
	                    
	                    ref var layers = ref terrainData.RawTilesToBlend.GetOrAddRefUnsafe(kvp.Key);

	                    if (layers.Length == maxLayersBlend)
	                    {
		                    continue;
	                    }
	                    
	                    var spriteMesh = kvp.Value;
	                    layers.Add(new GpuTerrainTile(spriteMesh.MinUv, spriteMesh.Flip, spriteMesh.Rotation));
                    }
                }
            }
        }
        
	    [BurstCompile]
        private struct GenerateTerrainMeshDataJob : IJobParallelForDefer
        {
	        [ReadOnly]
	        public ComponentLookup<TilemapTransform> TilemapTransformLookup;
	        [ReadOnly]
	        public ComponentLookup<TerrainData> TerrainDataLookup;
	        
	        [ReadOnly]
	        public NativeArray<VertexAttributeDescriptor> Layout;
	        [ReadOnly]
	        public NativeArray<Hash128> HashesToUpdate;
	        [ReadOnly]
	        public NativeHashMap<Hash128, Singleton.Terrain> Terrains;
	        
	        public Mesh.MeshDataArray MeshDataArray;
	        public NativeParallelHashMap<Hash128, AABB>.ParallelWriter UpdatedMeshBoundsMapWriter;
	        
        	public void Execute(int index)
	        {
		        var hash = HashesToUpdate[index];
		        var meshData = MeshDataArray[index];
		        ref var terrainData = ref Terrains.GetValueAsRef(hash);
		        var rendererData = TilemapTransformLookup[terrainData.TerrainEntity];
		        var tileSize = TerrainDataLookup[terrainData.TerrainEntity].TileSize;

		        var quadCount = terrainData.RawTilesToBlend.Count;
                
		        var vertexCount = quadCount * 4;
		        var indexCount = quadCount * 6;
		        
		        PrepareMeshData(meshData, vertexCount, indexCount);

		        terrainData.TileBuffer.Clear();
		        terrainData.IndexBuffer.Clear();
		        
		        var vertices = meshData.GetVertexData<Vertex>();
		        var indices = meshData.GetIndexData<int>();

		        var quadIndex = 0;
		        var orientation = rendererData.Orientation;
		        
		        var minPos = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
		        var maxPos = new float3(float.MinValue, float.MinValue, float.MinValue);
		        
		        foreach (var kvp in terrainData.RawTilesToBlend)
		        {
			        var worldPos = MosaicUtils.ToWorldSpace(kvp.Key, rendererData)
			                         + MosaicUtils.ApplyOrientation(float2.zero, orientation);

			        var rectSize = MosaicUtils.ApplySwizzle(rendererData.CellSize, rendererData.Swizzle).xy;
			        var rotatedSize = MosaicUtils.ApplyOrientation(rectSize, orientation);

			        var normal = MosaicUtils.ApplyOrientation(new float3(0, 0, 1), orientation);
			        var up = MosaicUtils.ApplyOrientation(new float3(0, 1, 0), orientation) * rotatedSize;
			        var right = MosaicUtils.ApplyOrientation(new float3(1, 0, 0), orientation) * rotatedSize;

			        var vc = 4 * quadIndex;
			        var tc = 6 * quadIndex;
			        
			        var minVertexPos = worldPos;
			        var maxVertexPos = worldPos + up + right;

			        minPos = math.min(minPos, minVertexPos);
			        maxPos = math.max(maxPos, maxVertexPos);
			        
			        vertices[vc + 0] = new Vertex
			        {
				        Position = worldPos + up,
				        Normal = normal,
				        TexCoord0 = new float2(0f, tileSize.y)
			        };
			        vertices[vc + 1] = new Vertex
			        {
				        Position = maxVertexPos,
				        Normal = normal,
				        TexCoord0 = new float2(tileSize.x, tileSize.y)
			        };
			        vertices[vc + 2] = new Vertex
			        {
				        Position = worldPos + right,
				        Normal = normal,
				        TexCoord0 = new float2(tileSize.x, 0f)
			        };
			        vertices[vc + 3] = new Vertex
			        {
				        Position = minVertexPos,
				        Normal = normal,
				        TexCoord0 = new float2(0f, 0f)
			        };

			        var startIndex = terrainData.TileBuffer.Length;
			        
			        foreach (var terrainTile in kvp.Value)
			        {
				        terrainData.TileBuffer.Add(terrainTile);
			        }
			        
			        terrainData.IndexBuffer.Add(new GpuTerrainIndex
			        {
				        StartIndex = (uint)startIndex,
				        EndIndex = (uint)terrainData.TileBuffer.Length
			        });
			        
			        indices[tc + 0] = (vc + 0);
			        indices[tc + 1] = (vc + 1);
			        indices[tc + 2] = (vc + 2);
			        
			        indices[tc + 3] = (vc + 0);
			        indices[tc + 4] = (vc + 2);
			        indices[tc + 5] = (vc + 3);
			        
			        quadIndex++;
		        }
		        
		        FinalizeMeshData(hash, meshData, indexCount, maxPos, minPos);
	        }
	        
	        private void PrepareMeshData(Mesh.MeshData meshData, int vertexCount, int indexCount)
	        {
		        meshData.SetVertexBufferParams(vertexCount, Layout);
		        meshData.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
	        }
	        
	        private void FinalizeMeshData(Hash128 hash, Mesh.MeshData meshData, int indexCount, float3 maxPos, float3 minPos)
	        {
		        meshData.subMeshCount = 1;
		        meshData.SetSubMesh(0, new SubMeshDescriptor(0, indexCount));
		        
		        UpdatedMeshBoundsMapWriter.TryAdd(hash, new AABB
		        {
			        Center = (maxPos + minPos) * 0.5f,
			        Extents = (maxPos - minPos) * 0.5f,
		        });
	        }
        }
	    
	    [StructLayout(LayoutKind.Sequential)]
	    private struct Vertex
	    {
		    public float3 Position;
		    public float3 Normal;
		    public float2 TexCoord0;
	    }
    }
}
