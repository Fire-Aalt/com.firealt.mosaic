using Unity.Entities;

namespace FireAlt.Mosaic.Data
{
    [InternalBufferCapacity(0)]
    public struct WeightedEntityElement : IBufferElementData
    {
        public Entity Value;
    }
}