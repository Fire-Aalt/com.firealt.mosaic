using Unity.Entities;

namespace FireAlt.Mosaic
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class TilemapCleanupSystemGroup : ComponentSystemGroup
    {
    }
}