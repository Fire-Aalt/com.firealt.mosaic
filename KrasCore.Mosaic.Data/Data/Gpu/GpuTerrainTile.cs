using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuTerrainTile
    {
        public float2 Offset;
        public uint Flags;

        public GpuTerrainTile(float2 offset, bool2 flip, int rot)
        {
            Offset = offset;
		            
            var flipX = flip.x ? 1 : 0;
            var flipY = flip.y ? 1 : 0;
          
            flipX &= 1;
            flipY &= 1;
            rot &= 3;

            Flags = (uint)(flipX | (flipY << 1) | (rot << 2));
        }
    }
}

