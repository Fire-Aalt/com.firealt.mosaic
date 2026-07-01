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
        public Dictionary<Hash128, Mesh> MeshMap;
        public Dictionary<Hash128, TilemapTerrainRenderingData> TerrainMap;

        public void Init(int capacity)
        {
            MeshMap = new Dictionary<Hash128, Mesh>(capacity);
            TerrainMap = new Dictionary<Hash128, TilemapTerrainRenderingData>(1);
        }
		
        public void Dispose()
        {
            foreach (var kvp in MeshMap)
            {
                CoreUtils.Destroy(kvp.Value);
            }
            foreach (var kvp in TerrainMap)
            {
                kvp.Value.Dispose();
            }
        }
    }
}