using System;
using UnityEngine;

namespace FireAlt.Mosaic.Data
{
    [Flags]
    public enum Transformation
    {
        [Tooltip("X mirror. Enable this to mark the rule as able to mirror the result horizontally")]
        MirrorX = 1,

        [Tooltip("Y mirror. Enable this to mark the rule as able to mirror the result vertically")]
        MirrorY = 2,
        
        [Tooltip("Enable this to mark the rule as able to rotate the result by 90 degrees up to 4 times")]
        Rotated = 4
    }
}