using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using FireAlt.Core.Extensions;

namespace FireAlt.Mosaic.Data
{
    public class TilemapTerrainRenderingData : ScriptableObject, IDisposable
    {
        private static readonly int TileSizeId = Shader.PropertyToID("_TileSize");
        private static readonly int TileBufferId = Shader.PropertyToID("_TerrainTileBuffer");
        private static readonly int IndexBufferId = Shader.PropertyToID("_TerrainIndexBuffer");
        
        public Material Material;
        
        private GraphicsBuffer _tileBuffer;
        private GraphicsBuffer _indexBuffer;

        public void Init(Material material)
        {
            Material = material;
            ResizeTileBuffer(256);
            ResizeIndexBuffer(256);
        }
        
        public void SetTileSize(float2 tileSize)
        {
            Material.SetVector(TileSizeId, new Vector4(tileSize.x, tileSize.y));
        }
        
        public void SetTileBuffer(UnsafeList<GpuTerrainTile> buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }
            
            if (_tileBuffer.count < buffer.Length)
            {
                ResizeTileBuffer(buffer.Length);
            }
            
            _tileBuffer.SetData(buffer.AsNativeArray());
        }
        
        public void SetIndexBuffer(UnsafeList<GpuTerrainIndex> buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }
            
            if (_indexBuffer.count < buffer.Length)
            {
                ResizeIndexBuffer(buffer.Length);
            }
            
            _indexBuffer.SetData(buffer.AsNativeArray());
        }
        
        private void ResizeTileBuffer(int length)
        {
            _tileBuffer?.Dispose();
            _tileBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, length, UnsafeUtility.SizeOf<GpuTerrainTile>());
            Material.SetBuffer(TileBufferId, _tileBuffer);
        }

        private void ResizeIndexBuffer(int length)
        {
            _indexBuffer?.Dispose();
            _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, length, UnsafeUtility.SizeOf<GpuTerrainIndex>());
            Material.SetBuffer(IndexBufferId, _indexBuffer);
        }
        
        public void Dispose()
        {
            _tileBuffer?.Dispose();
            _indexBuffer?.Dispose();
        }
    }
}