using Unity.Entities;

namespace FireAlt.Mosaic
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TilemapCleanupSystemGroup))]
    public partial class TilemapInitializationSystemGroup : ComponentSystemGroup
    {
    }
}