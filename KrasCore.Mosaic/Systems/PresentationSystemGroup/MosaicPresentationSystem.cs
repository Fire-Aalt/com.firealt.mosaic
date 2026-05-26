using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Hash128 = Unity.Entities.Hash128;
using Mesh = UnityEngine.Mesh;

namespace FireAlt.Mosaic
{
	[UpdateInGroup(typeof(PresentationSystemGroup), OrderFirst = true)]
	public partial class MosaicPresentationSystem : SystemBase
	{
		public class Singleton : IComponentData, IDisposable
		{
			public Dictionary<Hash128, Mesh> MeshMap;
			public Dictionary<Hash128, TilemapTerrainRenderingData> TerrainMap;
        
			public void Dispose()
			{
				foreach (var kvp in MeshMap)
				{
					UnityEngine.Object.Destroy(kvp.Value);
				}
				foreach (var kvp in TerrainMap)
				{
					kvp.Value.Dispose();
				}
			}
		}
		
		private readonly List<Mesh> _intGridMeshesToUpdate = new();
		private readonly List<Mesh> _terrainMeshesToUpdate = new();
		private readonly Mesh _dummyMesh = new();
		
		protected override void OnCreate()
		{
			EntityManager.CreateSingleton(new Singleton
			{
				MeshMap = new Dictionary<Hash128, Mesh>(4),
				TerrainMap = new Dictionary<Hash128, TilemapTerrainRenderingData>(1)
			});
		}

		protected override void OnDestroy()
		{
			SystemAPI.ManagedAPI.GetSingleton<Singleton>().Dispose();
		}
 
		protected override void OnUpdate()
		{
			EntityManager.CompleteDependencyBeforeRW<IntGridMeshDataSystem.Singleton>();
			EntityManager.CompleteDependencyBeforeRW<TerrainMeshDataSystem.Singleton>();
			var singleton = SystemAPI.ManagedAPI.GetSingleton<Singleton>();
			ref var intGridSingleton = ref SystemAPI.GetSingletonRW<IntGridMeshDataSystem.Singleton>().ValueRW;
			ref var terrainSingleton = ref SystemAPI.GetSingletonRW<TerrainMeshDataSystem.Singleton>().ValueRW;
			
			foreach (var intGridHash in intGridSingleton.HashesToUpdate)
			{
				_intGridMeshesToUpdate.Add(singleton.MeshMap[intGridHash]);
			}
			
			foreach (var terrainHash in terrainSingleton.HashesToUpdate)
			{
				var terrainRenderingData = singleton.TerrainMap[terrainHash];
				var terrainData = terrainSingleton.Terrains[terrainHash];
				var tilemapTerrainData = EntityManager.GetComponentData<TerrainData>(terrainData.TerrainEntity);
				
				terrainRenderingData.SetTileSize(tilemapTerrainData.TileSize);
				terrainRenderingData.SetTileBuffer(terrainData.TileBuffer);
				terrainRenderingData.SetIndexBuffer(terrainData.IndexBuffer);
				
				_terrainMeshesToUpdate.Add(singleton.MeshMap[terrainHash]);
			}
			
			UpdateMeshes(ref intGridSingleton.MeshDataArray, _intGridMeshesToUpdate, intGridSingleton.RenderingEntities.Length);
			UpdateMeshes(ref terrainSingleton.MeshDataArray, _terrainMeshesToUpdate, terrainSingleton.RenderingEntities.Length);
			
			if (_intGridMeshesToUpdate.Count != 0 || _terrainMeshesToUpdate.Count != 0)
			{
				new UpdateBoundsJob
				{
					IntGridUpdatedMeshBoundsMap = intGridSingleton.UpdatedMeshBoundsMap,
					TerrainUpdatedMeshBoundsMap = terrainSingleton.UpdatedMeshBoundsMap
				}.Run();
				
				intGridSingleton.HashesToUpdate.Clear();
				intGridSingleton.UpdatedMeshBoundsMap.Clear();
				terrainSingleton.HashesToUpdate.Clear();
				terrainSingleton.UpdatedMeshBoundsMap.Clear();
				_intGridMeshesToUpdate.Clear();
				_terrainMeshesToUpdate.Clear();
			}
		}

		private void UpdateMeshes(ref MeshDataArrayWrapper meshDataArray, List<Mesh> meshes, int neededMeshesCount)
		{
			if (meshes.Count != 0)
			{
				// Mesh.ApplyAndDisposeWritableMeshData() expects same size List<Mesh>, so we have to populate it with dummy meshes
				var neededDummies = meshDataArray.Array.Length - meshes.Count;
				for (int i = 0; i < neededDummies; i++)
				{
					meshes.Add(_dummyMesh);
				}
				meshDataArray.ApplyAndDisposeWritableMeshData(meshes);
			}
			
			if (!meshDataArray.IsCreated || neededMeshesCount != meshDataArray.Array.Length)
			{
				meshDataArray.AllocateWritableMeshData(neededMeshesCount);
			}
		}

		[BurstCompile]
		private partial struct UpdateBoundsJob : IJobEntity
		{
			[ReadOnly]
			public NativeParallelHashMap<Hash128, AABB> IntGridUpdatedMeshBoundsMap;
			[ReadOnly]
			public NativeParallelHashMap<Hash128, AABB> TerrainUpdatedMeshBoundsMap;
			
			private void Execute(in TilemapRendererData rendererData, ref RenderBounds renderBounds)
			{
				if (IntGridUpdatedMeshBoundsMap.TryGetValue(rendererData.MeshHash, out var aabb) 
				    || TerrainUpdatedMeshBoundsMap.TryGetValue(rendererData.MeshHash, out aabb))
				{
					renderBounds.Value = aabb;
				}
			}
		}
	}
}
