using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct GridData : IComponentData
    {
        public float3 CellSize;
        public Swizzle Swizzle;
    }
}