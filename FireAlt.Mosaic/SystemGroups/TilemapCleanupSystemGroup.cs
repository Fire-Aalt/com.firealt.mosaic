using Unity.Entities;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class TilemapCleanupSystemGroup : ComponentSystemGroup
    {
    }
}
