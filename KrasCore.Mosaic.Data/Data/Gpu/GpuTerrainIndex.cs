using System.Runtime.InteropServices;

namespace FireAlt.Mosaic.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuTerrainIndex
    {
        public uint StartIndex;
        public uint EndIndex;
    }
}