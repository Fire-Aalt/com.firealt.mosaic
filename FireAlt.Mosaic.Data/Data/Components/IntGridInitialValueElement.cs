using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    public struct IntGridInitialValueElement : IBufferElementData
    {
        public int2 Position;
        public IntGridValue Value;
    }
}
