using Unity.Entities;

namespace FireAlt.Mosaic
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class TilemapUpdateSystemGroup : ComponentSystemGroup
    {
    }
}
