using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    [InternalBufferCapacity(0)]
    public struct RefreshPositionElement : IBufferElementData
    {
        public int2 Value;
    }
}