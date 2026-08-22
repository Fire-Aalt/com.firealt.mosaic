using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct TerrainData : IComponentData
    {
        public float2 TileSize;
        public int MaxLayersBlend;
    }
}
