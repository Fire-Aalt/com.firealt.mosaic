using Unity.Entities;

namespace FireAlt.Mosaic.Data
{
    [InternalBufferCapacity(0)]
    public struct TilemapTerrainLayerElement : IBufferElementData
    {
        public Entity IntGridEntity;
    }
}
