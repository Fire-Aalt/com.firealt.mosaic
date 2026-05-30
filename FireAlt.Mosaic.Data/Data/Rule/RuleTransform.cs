using System;

namespace FireAlt.Mosaic.Data
{
    // Exists for migration
    [Obsolete]
    public enum RuleTransform
    {
        None,
        MirrorX,
        MirrorY,
        MirrorXY,
        Rotated,
        Migrated
    }
}