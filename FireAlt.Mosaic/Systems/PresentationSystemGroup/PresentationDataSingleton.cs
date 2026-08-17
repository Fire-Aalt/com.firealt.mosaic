using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Data;
using Unity.Burst;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic
{
    public struct PresentationDataSingleton : IComponentData, IDisposable
    {
        public UnityObjectRef<PresentationDataObject> Value;

        public PresentationDataSingleton(int capacity)
        {
            Value = ScriptableObject.CreateInstance<PresentationDataObject>();
            Value.Value.Init(capacity);
        }
			
        [BurstDiscard]
        public void Dispose()
        {
            Value.Value.Dispose();
        }
    }
    
    public class PresentationDataObject : ScriptableObject, IDisposable
    {
        [NonSerialized] public Dictionary<Hash128, Mesh> MeshMap;
        [NonSerialized] public Dictionary<Hash128, TilemapTerrainRenderingData> TerrainMap;
        [NonSerialized] public Dictionary<Hash128, Entity> RenderingEntityMap;

        public bool IsCreated => MeshMap != null && TerrainMap != null && RenderingEntityMap != null;

        public void Init(int capacity)
        {
            MeshMap = new Dictionary<Hash128, Mesh>(capacity);
            TerrainMap = new Dictionary<Hash128, TilemapTerrainRenderingData>(1);
            RenderingEntityMap = new Dictionary<Hash128, Entity>(capacity);
        }

        public Mesh GetOrCreateMesh(Hash128 hash)
        {
            if (MeshMap.TryGetValue(hash, out var mesh)) return mesh;

            mesh = new Mesh { name = "Mosaic.TilemapMesh" };
            mesh.MarkDynamic();
            MeshMap.Add(hash, mesh);
            return mesh;
        }

        public void ReleaseEntity(Hash128 hash, Entity entity)
        {
            if (RenderingEntityMap.TryGetValue(hash, out var registered) && registered == entity)
            {
                RenderingEntityMap.Remove(hash);
            }
        }

        public void ReleaseTerrain(Hash128 hash)
        {
            if (!TerrainMap.Remove(hash, out var terrain)) return;

            terrain.Dispose();
            CoreUtils.Destroy(terrain.Material);
            CoreUtils.Destroy(terrain);
        }
		
        public void Dispose()
        {
            if (MeshMap != null)
            {
                foreach (var kvp in MeshMap)
                {
                    CoreUtils.Destroy(kvp.Value);
                }
            }

            if (TerrainMap != null)
            {
                foreach (var kvp in TerrainMap)
                {
                    kvp.Value.Dispose();
                    CoreUtils.Destroy(kvp.Value.Material);
                    CoreUtils.Destroy(kvp.Value);
                }
            }

            CoreUtils.Destroy(this);
        }
    }
}
