using System;
using FireAlt.Core.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace FireAlt.Mosaic.Data
{
    public struct SpriteMesh : IEquatable<SpriteMesh>
    {
        public float2 NormalizedPivot;
        public float2 RectScale;
        public float2 MinUv;
        public float2 MaxUv;

        public bool2 Flip;
        public int Rotation;
        public bool2 MatchedMirror;
        public int MatchedRotation;

        public SpriteMesh(Sprite sprite)
        {
            if (sprite != null)
            {
                var uvAtlas = RendererUtility.GetUvAtlas(sprite);
                NormalizedPivot = RendererUtility.GetNormalizedPivot(sprite);
                RectScale = RendererUtility.GetRectScale(sprite, uvAtlas);
                RendererUtility.GetUVCorners(sprite, out MinUv, out MaxUv);
            }
            else
            {
                NormalizedPivot = new float2(0.5f, 0.5f);
                RectScale = new float2(1f, 1f);
                MinUv = float2.zero;
                MaxUv = new float2(1f, 1f);
            }
            Flip = default;
            Rotation = default;
            MatchedMirror = default;
            MatchedRotation = default;
        }

        public bool Equals(SpriteMesh other)
        {
            return NormalizedPivot.Equals(other.NormalizedPivot) 
                   && RectScale.Equals(other.RectScale) 
                   && MinUv.Equals(other.MinUv) 
                   && MaxUv.Equals(other.MaxUv) 
                   && Flip.Equals(other.Flip) 
                   && Rotation.Equals(other.Rotation)
                   && MatchedMirror.Equals(other.MatchedMirror)
                   && MatchedRotation.Equals(other.MatchedRotation);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(NormalizedPivot, RectScale, MinUv, MaxUv, Flip, Rotation, MatchedMirror, MatchedRotation);
        }
    }
}
