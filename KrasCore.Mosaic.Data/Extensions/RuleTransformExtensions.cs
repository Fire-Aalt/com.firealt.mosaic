namespace FireAlt.Mosaic.Data
{
    public static class RuleTransformExtensions
    {
        public static bool IsMirroredX(this Transformation transformation) => transformation.HasFlagBurst(Transformation.MirrorX);
        
        public static bool IsMirroredY(this Transformation transformation) => transformation.HasFlagBurst(Transformation.MirrorY);
        
        public static bool HasFlagBurst(this Transformation flags, Transformation flag)
        {
            return (flags & flag) != 0;
        }
    }
}