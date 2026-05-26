using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct TerrainData : IComponentData
    {
        public Hash128 TerrainHash;
        public float2 TileSize;
        public int MaxLayersBlend;
    }
}