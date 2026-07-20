using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace FireAlt.Mosaic.Data
{
    [InternalBufferCapacity(0)]
    public struct IntGridValueElement : IBufferElementData
    {
        public IntGridValue Value;
        public FixedString64Bytes Name;
        public Color Color;
        public UnityObjectRef<Texture> Texture;
    }
}