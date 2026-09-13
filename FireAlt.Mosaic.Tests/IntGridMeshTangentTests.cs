using FireAlt.Mosaic.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace FireAlt.Mosaic.Tests
{
    public sealed class IntGridMeshTangentTests
    {
        [Test]
        public void TangentLayoutAndBasisMatchQuadGeometryAndUvs()
        {
            var singleton = new IntGridMeshDataSystem.Singleton(1, Allocator.Persistent);
            try
            {
                Assert.That(singleton.Layout.Length, Is.EqualTo(4));
                Assert.That(singleton.Layout[2].attribute, Is.EqualTo(VertexAttribute.Tangent));
                Assert.That(singleton.Layout[2].format, Is.EqualTo(VertexAttributeFormat.SNorm8));
                Assert.That(singleton.Layout[2].dimension, Is.EqualTo(4));
            }
            finally { singleton.Dispose(); }

            foreach (var orientation in new[] { Orientation.XY, Orientation.XZ })
            for (var rotation = 0; rotation < 4; rotation++)
            foreach (var flipX in new[] { false, true })
            foreach (var flipY in new[] { false, true })
            {
                var normal = MosaicUtils.ApplyOrientation(new float3(0, 0, 1), orientation);
                var up = MosaicUtils.Rotate(MosaicUtils.ApplyOrientation(new float3(0, 1, 0), orientation),
                    rotation, orientation);
                var right = MosaicUtils.Rotate(MosaicUtils.ApplyOrientation(new float3(1, 0, 0), orientation),
                    rotation, orientation);
                var minUv = new float2(flipX ? 1 : 0, flipY ? 1 : 0);
                var maxUv = new float2(flipX ? 0 : 1, flipY ? 0 : 1);
                var tangent = MosaicUtils.CalculateTangent(
                    normal, up, up + right, float3.zero, minUv, maxUv);
                var expectedTangent = right * (flipX ? -1 : 1);
                var expectedBitangent = up * (flipY ? -1 : 1);
                var actualBitangent = math.cross(normal, tangent.xyz) * tangent.w;

                Assert.That(math.dot(tangent.xyz, expectedTangent), Is.EqualTo(1).Within(0.0001f));
                Assert.That(math.dot(actualBitangent, expectedBitangent), Is.EqualTo(1).Within(0.0001f));
                Assert.That(math.dot(normal, tangent.xyz), Is.EqualTo(0).Within(0.0001f));
            }
        }

        [Test]
        public void TerrainLayoutOmitsTangentData()
        {
            var singleton = new TerrainMeshDataSystem.Singleton(1, Allocator.Persistent);
            try
            {
                Assert.That(singleton.Layout.Length, Is.EqualTo(3));
                Assert.That(singleton.Layout[0].attribute, Is.EqualTo(VertexAttribute.Position));
                Assert.That(singleton.Layout[1].attribute, Is.EqualTo(VertexAttribute.Normal));
                Assert.That(singleton.Layout[2].attribute, Is.EqualTo(VertexAttribute.TexCoord0));
            }
            finally { singleton.Dispose(); }
        }

        [Test]
        public void StandingWallTransforms_TurnFacesAroundCellCenterWithoutTiltingArtwork()
        {
            var baseNormal = new float3(0f, 0f, -1f);
            var baseTop = new float3(-0.5f, 1f, -0.5f);
            var expectedNormals = new[]
            {
                new float3(0f, 0f, -1f), new float3(-1f, 0f, 0f),
                new float3(0f, 0f, 1f), new float3(1f, 0f, 0f)
            };
            for (var rotation = 0; rotation < 4; rotation++)
            {
                var normal = MosaicUtils.TransformStandingTile(baseNormal, default, rotation);
                var top = MosaicUtils.TransformStandingTile(baseTop, default, rotation);
                Assert.That(math.distance(normal, expectedNormals[rotation]), Is.LessThan(0.0001f));
                Assert.That(top.y, Is.EqualTo(1f));
                Assert.That(MosaicUtils.StandingTileFace(default, rotation), Is.EqualTo(rotation));
            }

            Assert.That(MosaicUtils.StandingTileFace(new bool2(true, false), 0), Is.EqualTo(0));
            Assert.That(MosaicUtils.StandingTileFace(new bool2(false, true), 0), Is.EqualTo(2));
            Assert.That(MosaicUtils.TransformStandingTile(baseTop, new bool2(true, false), 0).x, Is.EqualTo(0.5f));
        }
    }
}
