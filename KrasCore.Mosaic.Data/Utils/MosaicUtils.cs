using System.Runtime.CompilerServices;
using Unity.Mathematics;
using UnityEngine;

namespace FireAlt.Mosaic.Data
{
    public static class MosaicUtils
    {
        public static uint Hash(uint seed, int2 pos)
        {
            ulong combined = ((ulong)pos.x << 32) | (uint)pos.y;
            uint hash = seed;

            hash ^= (uint)(combined & 0xFFFFFFFF); 
            hash *= 0x85EBCA6B;
            hash ^= hash >> 13;
            hash ^= (uint)(combined >> 32);       
            hash *= 0xC2B2AE35;
            hash ^= hash >> 16;

            return hash + 1;
        }
        
        public static int Hash(int ruleIndex, bool2 mirror, int rotation)
        {
            var mirrorHash = (mirror.x ? 1 : 0) | ((mirror.y ? 1 : 0) << 1);
            var hash = ruleIndex + 531;
            hash = (hash * 431) + mirrorHash;
            hash = (hash * 701) + rotation;
            return hash;
        }
        
        public static bool CanPlace(short rule, short value)
        {
            // "never place"
            if (rule == -RuleGridConsts.AnyIntGridValue) 
                return false;
                
            // "always place"
            if (rule == RuleGridConsts.AnyIntGridValue) 
                return true;
    
            // negative => "must not match this exact value"
            if (rule < 0) 
                return -rule != value;
    
            // positive => "must match exactly"
            return rule == value;
        }
        
        public static void GetSpriteMeshTranslation(SpriteMesh spriteMesh, out float2 translation)
        {
            var pivot = spriteMesh.NormalizedPivot;
            
            pivot.x = spriteMesh.Flip.x ? 1.0f - pivot.x : pivot.x;
            pivot.y = spriteMesh.Flip.y ? 1.0f - pivot.y : pivot.y;
                
            // Offset by from center (0.5f - x/y)
            translation = new float2((0.5f - pivot.x) * spriteMesh.RectScale.x, (0.5f - pivot.y) * spriteMesh.RectScale.y);
        }
        
        public static float3 ApplyOrientation(float2 pos, Orientation orientation)
        {
            return orientation == Orientation.XZ 
                ? new float3(pos.x, 0f, pos.y) 
                : new float3(pos.x, pos.y, 0f);
        }
        
        public static float3 ApplyOrientation(float3 pos, Orientation orientation)
        {
            return orientation switch
            {
                Orientation.XY => new float3(pos.x, pos.y, -pos.z),
                Orientation.XZ => new float3(pos.x, pos.z, pos.y),
                _ => float3.zero
            };
        }
        
        public static float3 ToWorldSpace(float2 pos, TilemapTransform rendererData)
        {
            return ApplySwizzle(pos, rendererData.Swizzle) * ApplySwizzle(rendererData.CellSize, rendererData.Swizzle);
        }
        
        public static float3 ApplySwizzle(float2 pos, Swizzle swizzle)
        {
            return swizzle switch
            {
                Swizzle.XYZ => new float3(pos.x, pos.y, 0f),
                Swizzle.XZY => new float3(pos.x, 0f, pos.y),
                _ => float3.zero
            };
        }
        
        public static float3 ApplySwizzle(float3 pos, Swizzle swizzle)
        {
            return swizzle switch
            {
                Swizzle.XYZ => pos.xyz,
                Swizzle.XZY => pos.xzy,
                _ => float3.zero
            };
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 Rotate(in float3 dir, int rotation, in Orientation orientation)
        {
            var r = rotation & 3; // mod 4
            if (r == 0) return dir;

            if (orientation == Orientation.XY)
            {
                // Rotate around Z
                switch (r)
                {
                    case 1: return new float3(-dir.y, dir.x, dir.z);   // 90°
                    case 2: return new float3(-dir.x, -dir.y, dir.z);  // 180°
                    case 3: return new float3(dir.y, -dir.x, dir.z);   // 270°
                }
            }
            else
            {
                // Rotate around Y
                switch (r)
                {
                    case 1: return new float3(dir.z, dir.y, -dir.x);   // 90°
                    case 2: return new float3(-dir.x, dir.y, -dir.z);  // 180°
                    case 3: return new float3(-dir.z, dir.y, dir.x);   // 270°
                }
            }

            return dir;
        }
    }
}