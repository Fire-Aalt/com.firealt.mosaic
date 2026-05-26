using System;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Data
{
    ///<summary>
    /// An AABB, or axis-aligned bounding box, is a simple bounding shape, typically used for quick determination
    /// of whether two objects may intersect. If the AABBs that enclose each object do not intersect, then logically
    /// the objects also may not intersect. This AABB struct is formulated as a center and a size, rather than as a
    /// minimum and maximum coordinate. Therefore, there may be issues at extreme coordinates, such as FLT_MAX or infinity.
    ///</summary>
    [Serializable]
    public struct AABB2D : IEquatable<AABB2D>
    {
        /// <summary>
        /// The location of the center of the AABB
        /// </summary>
        public float2 Center;

        /// <summary>
        /// A 2D vector from the center of the AABB, to the corner of the AABB with maximum XY values
        /// </summary>
        public float2 Extents;

        /// <summary>
        /// The size of the AABB
        /// </summary>
        /// <returns>The size of the AABB, in three dimensions. All three dimensions must be positive.</returns>
        public float2 Size { get { return Extents * 2; } }

        /// <summary>
        /// The minimum coordinate of the AABB
        /// </summary>
        /// <returns>The minimum coordinate of the AABB, in three dimensions.</returns>
        public float2 Min { get { return Center - Extents; } }

        /// <summary>
        /// The maximum coordinate of the AABB
        /// </summary>
        /// <returns>The maximum coordinate of the AABB, in three dimensions.</returns>
        public float2 Max { get { return Center + Extents; } }

        /// <summary>Returns a string representation of the AABB.</summary>
        /// <returns>a string representation of the AABB.</returns>
        public override string ToString()
        {
            return $"AABB2D(Center:{Center}, Extents:{Extents}";
        }

        /// <summary>
        /// Returns whether a point in 2D space is contained by the AABB, or not. Because math is done
        /// to compute the minimum and maximum coordinates of the AABB, overflow is possible for extreme values.
        /// </summary>
        /// <param name="point">The point to check for whether it's contained by the AABB</param>
        /// <returns>True if the point is contained, and false if the point is not contained by the AABB.</returns>
        public bool Contains(float2 point) => math.all(point >= Min & point <= Max);

        /// <summary>
        /// Returns whether the AABB contains another AABB completely. Because math is done
        /// to compute the minimum and maximum coordinates of the AABBs, overflow is possible for extreme values.
        /// </summary>
        /// <param name="b">The AABB to check for whether it's contained by this AABB</param>
        /// <returns>True if the AABB is contained, and false if it is not.</returns>
        public bool Contains(AABB2D b) => math.all((Min <= b.Min) & (Max >= b.Max));

        public bool Equals(AABB2D other)
        {
            return Center.Equals(other.Center) && Extents.Equals(other.Extents);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Center, Extents);
        }
    }
}