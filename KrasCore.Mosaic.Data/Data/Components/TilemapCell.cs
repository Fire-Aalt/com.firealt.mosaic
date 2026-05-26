using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct TilemapCell : IComponentData
    {
        public Hash128 IntGridLayerHash;
        public int2 Cell;
    }
}