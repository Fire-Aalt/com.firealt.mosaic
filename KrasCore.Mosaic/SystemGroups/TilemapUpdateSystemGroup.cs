using Unity.Entities;

namespace FireAlt.Mosaic
{
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class TilemapUpdateSystemGroup : ComponentSystemGroup
    {
    }
}