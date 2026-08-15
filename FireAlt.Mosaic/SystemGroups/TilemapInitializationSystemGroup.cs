using Unity.Entities;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TilemapCleanupSystemGroup))]
    public partial class TilemapInitializationSystemGroup : ComponentSystemGroup
    {
    }
}
