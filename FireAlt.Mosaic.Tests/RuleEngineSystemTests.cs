using System.Runtime.InteropServices;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace FireAlt.Mosaic.Tests
{
    public sealed class RuleEngineSystemTests
    {
        private readonly Hash128 _hash = new(71u, 72u, 73u, 74u);

        private World _world;
        private EntityManager _entityManager;
        private SystemHandle _system;
        private Entity _intGridEntity;
        private BlobAssetReference<RuleBlob> _wallRule;

        [SetUp]
        public void SetUp()
        {
            _world = new World(nameof(RuleEngineSystemTests), WorldFlags.Editor);
            _entityManager = _world.EntityManager;
            _system = _world.GetOrCreateSystem<RuleEngineSystem>();

            var intGridData = new IntGridData
            {
                Hash = _hash,
                DebugName = "Rule Engine Test",
                DualGrid = false,
            };
            _intGridEntity = _entityManager.CreateEntity(typeof(IntGridData));
            _entityManager.SetComponentData(_intGridEntity, intGridData);
            _entityManager.AddComponentData(_intGridEntity, new TilemapTransform
            {
                CellSize = new float3(1f), Orientation = Orientation.XY, Swizzle = Swizzle.XZY
            });
            _entityManager.AddComponentData(_intGridEntity, new LocalToWorld { Value = float4x4.identity });
            _entityManager.AddBuffer<RuleBlobReferenceElement>(_intGridEntity);
            _entityManager.AddBuffer<RefreshPositionElement>(_intGridEntity).Add(new RefreshPositionElement
            {
                Value = int2.zero,
            });
            _entityManager.AddBuffer<WeightedEntityElement>(_intGridEntity);

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            dataSingleton.IntGridLayers.Add(_hash, new TilemapIntGridSingleton.IntGridLayer(
                16, Allocator.Persistent, intGridData, false, _intGridEntity));

            var commandSingleton = GetSingleton<TilemapCommandBufferSingleton>();
            commandSingleton.IntGridLayers.Add(_hash,
                new TilemapCommandBufferSingleton.IntGridLayer(16, Allocator.Persistent));
        }

        [TearDown]
        public void TearDown()
        {
            if (_world == null || !_world.IsCreated) return;
            _entityManager.CompleteAllTrackedJobs();
            _world.Dispose();
            if (_wallRule.IsCreated) _wallRule.Dispose();
        }

        [Test]
        public void ChangedRuleBuffer_ForcesOneFullRefreshAndClearsStaleResult()
        {
            UpdateSystem();

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            var stalePosition = new int2(4, 4);
            layer.IntGrid[int2.zero] = 1;
            layer.RuleGrid[stalePosition] = new RuleResultState { Hash = 123 };
            _entityManager.GetBuffer<RuleBlobReferenceElement>(_intGridEntity).Add(
                new RuleBlobReferenceElement { Enabled = false });

            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var refreshedLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.IsFalse(refreshedLayer.RuleGrid.ContainsKey(stalePosition));
            Assert.IsTrue(Contains(refreshedLayer.RefreshedPositions, stalePosition));
            Assert.IsFalse(refreshedLayer.ForceRuleRefresh);

            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var unchangedLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(0, unchangedLayer.PositionsToRefresh.Count);
            Assert.AreEqual(0, unchangedLayer.RefreshedPositions.Length);
        }

        [Test]
        public void DualGridChange_RefreshesNewFootprintAndRemovesOldResults()
        {
            UpdateSystem();

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            layer.IntGrid[int2.zero] = 1;
            layer.IntGrid[new int2(2, 0)] = 1;

            var intGridData = _entityManager.GetComponentData<IntGridData>(_intGridEntity);
            intGridData.DualGrid = true;
            _entityManager.SetComponentData(_intGridEntity, intGridData);
            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var dualLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.IsTrue(dualLayer.DualGrid);
            Assert.AreEqual(8, dualLayer.PositionsToRefresh.Count);
            Assert.IsTrue(dualLayer.PositionsToRefresh.Contains(new int2(-1, -1)));
            Assert.IsTrue(dualLayer.PositionsToRefresh.Contains(new int2(1, -1)));

            UpdateSystem();
            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            Assert.AreEqual(0, dataSingleton.IntGridLayers[_hash].PositionsToRefresh.Count);

            var firstStalePosition = new int2(-1, -1);
            var secondStalePosition = new int2(1, -1);
            ref var beforeSingleLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            beforeSingleLayer.RuleGrid[firstStalePosition] = new RuleResultState { Hash = 1 };
            beforeSingleLayer.RuleGrid[secondStalePosition] = new RuleResultState { Hash = 2 };
            intGridData.DualGrid = false;
            _entityManager.SetComponentData(_intGridEntity, intGridData);
            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var singleLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.IsFalse(singleLayer.DualGrid);
            Assert.AreEqual(0, singleLayer.RuleGrid.Count);
            Assert.IsTrue(Contains(singleLayer.RefreshedPositions, firstStalePosition));
            Assert.IsTrue(Contains(singleLayer.RefreshedPositions, secondStalePosition));
        }

        [Test]
        public void ForcedRefresh_RebuildsAnUnchangedRuleIdentity()
        {
            Assert.IsFalse(RuleEngineSystem.RuleResultChanged(42, 42, false));
            Assert.IsTrue(RuleEngineSystem.RuleResultChanged(42, 42, true));
        }

        [TestCase(false, 4, false)]
        [TestCase(true, 1, false)]
        [TestCase(true, 1, true)]
        public void StandingRule_EmitsEveryExposedFaceAndLimitsPrefabResults(bool uniquePrefab, int prefabCount, bool isTerrainLayer)
        {
            _wallRule = CreateWallRule(uniquePrefab);
            _entityManager.GetBuffer<RuleBlobReferenceElement>(_intGridEntity).Add(new RuleBlobReferenceElement
            {
                Enabled = true, Value = _wallRule
            });
            var prefab = _entityManager.CreateEntity(typeof(LocalTransform));
            _entityManager.SetComponentData(prefab, LocalTransform.FromPosition(new float3(0.25f, 0f, 0.25f)));
            _entityManager.GetBuffer<WeightedEntityElement>(_intGridEntity).Add(new WeightedEntityElement { Value = prefab });

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            layer.IsTerrainLayer = isTerrainLayer;
            layer.IntGrid[int2.zero] = 1;
            layer.MarkChanged(int2.zero);
            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(4, layer.RenderedSprites.Count());
            Assert.AreEqual(prefabCount, dataSingleton.EntityCommands.List.Length);
            var seenFaces = 0;
            foreach (var sprite in layer.RenderedSprites)
            {
                seenFaces |= 1 << MosaicUtils.StandingTileFace(sprite.Value.MatchedMirror, sprite.Value.MatchedRotation);
            }
            Assert.AreEqual(15, seenFaces);
            if (uniquePrefab) Assert.AreEqual(0, dataSingleton.EntityCommands.List[0].Face);

            UpdateSpawnSystems();
            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(prefabCount, layer.SpawnedEntities.Count());
            var previousInstances = new NativeList<Entity>(Allocator.Temp);
            foreach (var spawned in layer.SpawnedEntities) previousInstances.Add(spawned.Value.Entity);
            if (uniquePrefab)
            {
                var transform = _entityManager.GetComponentData<LocalTransform>(previousInstances[0]);
                Assert.That(math.distance(transform.Position, new float3(0.25f, 0f, 0.25f)), Is.LessThan(0.0001f));
            }

            layer.IntGrid[new int2(0, -1)] = 1;
            layer.MarkChanged(int2.zero);
            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(3, layer.RenderedSprites.Count());
            Assert.AreEqual(uniquePrefab ? 1 : 3, dataSingleton.EntityCommands.List.Length);
            if (uniquePrefab) Assert.AreEqual(1, dataSingleton.EntityCommands.List[0].Face);
            Assert.IsTrue(Contains(layer.RefreshedPositions, int2.zero));
            UpdateSpawnSystems();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(uniquePrefab ? 1 : 3, layer.SpawnedEntities.Count());
            foreach (var previous in previousInstances) Assert.IsFalse(_entityManager.Exists(previous));
            if (uniquePrefab)
            {
                Assert.IsTrue(layer.SpawnedEntities.TryGetFirstValue(int2.zero, out var spawned, out _));
                var transform = _entityManager.GetComponentData<LocalTransform>(spawned.Entity);
                Assert.That(math.distance(transform.Position, new float3(0.25f, 0f, 0.75f)), Is.LessThan(0.0001f));
                Assert.That(math.abs(math.dot(transform.Rotation.value,
                    quaternion.RotateY(math.PI / 2f).value)), Is.EqualTo(1f).Within(0.0001f));
            }
        }

        [Test]
        public void StandingTerrain_BlendsByFaceAndBuildsFourQuads()
        {
            var terrainHash = new Hash128(81u, 82u, 83u, 84u);
            var secondGridHash = new Hash128(85u, 86u, 87u, 88u);
            var terrainEntity = _entityManager.CreateEntity(typeof(TilemapRendererData), typeof(TilemapTransform),
                typeof(TerrainData), typeof(MosaicRendererInitialized));
            _entityManager.SetComponentData(terrainEntity, new TilemapRendererData { MeshHash = terrainHash });
            _entityManager.SetComponentData(terrainEntity, _entityManager.GetComponentData<TilemapTransform>(_intGridEntity));
            _entityManager.SetComponentData(terrainEntity, new TerrainData { TileSize = new float2(1f), MaxLayersBlend = 2 });
            var terrainLayers = _entityManager.AddBuffer<TilemapTerrainLayerElement>(terrainEntity);
            terrainLayers.Add(new TilemapTerrainLayerElement { IntGridEntity = _intGridEntity });

            var secondGrid = new IntGridData { Hash = secondGridHash };
            var secondGridEntity = _entityManager.CreateEntity(typeof(IntGridData));
            _entityManager.SetComponentData(secondGridEntity, secondGrid);
            terrainLayers.Add(new TilemapTerrainLayerElement { IntGridEntity = secondGridEntity });

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            dataSingleton.IntGridLayers.Add(secondGridHash, new TilemapIntGridSingleton.IntGridLayer(
                4, Allocator.Persistent, secondGrid, true, secondGridEntity));
            ref var firstLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            firstLayer.IsTerrainLayer = true;
            for (var rotation = 0; rotation < 4; rotation++)
            {
                var sprite = new SpriteMesh(null) { MatchedRotation = rotation, Rotation = rotation };
                if (rotation == 2)
                {
                    sprite.MatchedRotation = 0;
                    sprite.Rotation = 0;
                    sprite.MatchedMirror = new bool2(false, true);
                    sprite.Flip = sprite.MatchedMirror;
                }
                firstLayer.RenderedSprites.Add(int2.zero, sprite);
            }
            ref var secondLayer = ref dataSingleton.IntGridLayers.GetValueAsRef(secondGridHash);
            secondLayer.RenderedSprites.Add(int2.zero, new SpriteMesh(null)
            {
                MatchedMirror = new bool2(true, false), Flip = new bool2(true, false), Rotation = 1
            });

            var terrainSystem = _world.GetOrCreateSystem<TerrainMeshDataSystem>();
            var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<TerrainMeshDataSystem.Singleton>());
            var singletonEntity = query.GetSingletonEntity();
            query.Dispose();
            var meshSingleton = _entityManager.GetComponentData<TerrainMeshDataSystem.Singleton>(singletonEntity);
            meshSingleton.MeshDataArray.AllocateWritableMeshData(1);
            _entityManager.SetComponentData(singletonEntity, meshSingleton);
            try
            {
                terrainSystem.Update(_world.Unmanaged);
                _entityManager.CompleteAllTrackedJobs();

                meshSingleton = _entityManager.GetComponentData<TerrainMeshDataSystem.Singleton>(singletonEntity);
                var terrain = meshSingleton.Terrains[terrainHash];
                Assert.AreEqual(4, terrain.RawTilesToBlend.Count);
                Assert.AreEqual(4, terrain.IndexBuffer.Length);
                Assert.AreEqual(5, terrain.TileBuffer.Length);

                var meshData = meshSingleton.MeshDataArray.Array[0];
                var vertices = meshData.GetVertexData<TerrainVertex>();
                var indices = meshData.GetIndexData<int>();
                Assert.AreEqual(16, vertices.Length);
                Assert.AreEqual(24, indices.Length);
                var faces = 0;
                for (var quad = 0; quad < 4; quad++)
                {
                    var normal = vertices[quad * 4].Normal;
                    var face = normal.z < -0.5f ? 0 : normal.x < -0.5f ? 1 : normal.z > 0.5f ? 2 : 3;
                    faces |= 1 << face;
                    var a = vertices[indices[quad * 6]].Position;
                    var b = vertices[indices[quad * 6 + 1]].Position;
                    var c = vertices[indices[quad * 6 + 2]].Position;
                    Assert.That(math.dot(math.cross(b - a, c - a), normal), Is.GreaterThan(0f));
                    var range = terrain.IndexBuffer[quad];
                    Assert.AreEqual(face == 0 ? 2u : 1u, range.EndIndex - range.StartIndex);
                    if (face == 0)
                    {
                        var flags = new[] { terrain.TileBuffer[(int)range.StartIndex].Flags,
                            terrain.TileBuffer[(int)range.StartIndex + 1].Flags };
                        Assert.That(flags, Is.EquivalentTo(new uint[] { 0u, 6u }));
                    }
                    if (face == 2) Assert.AreEqual(1u, terrain.TileBuffer[(int)range.StartIndex].Flags & 3u);
                }
                Assert.AreEqual(15, faces);
            }
            finally
            {
                _entityManager.CompleteAllTrackedJobs();
                meshSingleton = _entityManager.GetComponentData<TerrainMeshDataSystem.Singleton>(singletonEntity);
                meshSingleton.MeshDataArray.Array.Dispose();
            }
        }

        [Test]
        public void XzTileOnXyGrid_UsesOneRuleResult()
        {
            var transform = _entityManager.GetComponentData<TilemapTransform>(_intGridEntity);
            transform.Orientation = Orientation.XZ;
            transform.Swizzle = Swizzle.XYZ;
            _entityManager.SetComponentData(_intGridEntity, transform);
            _wallRule = CreateWallRule(false);
            _entityManager.GetBuffer<RuleBlobReferenceElement>(_intGridEntity).Add(new RuleBlobReferenceElement
            {
                Enabled = true, Value = _wallRule
            });
            var prefab = _entityManager.CreateEntity(typeof(LocalTransform));
            _entityManager.SetComponentData(prefab, LocalTransform.FromPosition(float3.zero));
            _entityManager.GetBuffer<WeightedEntityElement>(_intGridEntity).Add(new WeightedEntityElement { Value = prefab });

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            layer.IntGrid[int2.zero] = 1;
            layer.MarkChanged(int2.zero);
            UpdateSystem();

            dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            Assert.AreEqual(1, layer.RenderedSprites.Count());
            Assert.AreEqual(1, dataSingleton.EntityCommands.List.Length);
            Assert.AreEqual(0, dataSingleton.EntityCommands.List[0].MatchedRotation);
        }

        [Test]
        public void SpawnInitialization_DropsStaleCommandsAndSpawnsCurrentResults()
        {
            var prefab = _entityManager.CreateEntity(typeof(LocalTransform));
            _entityManager.SetComponentData(prefab, LocalTransform.FromPosition(float3.zero));
            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            var secondCell = new int2(1, 0);
            layer.RuleGrid[int2.zero] = new RuleResultState { Hash = 1, Version = 2 };
            layer.RuleGrid[secondCell] = new RuleResultState { Hash = 1, Version = 3 };
            var commands = dataSingleton.EntityCommands.List;
            commands.Add(new EntityCommand { SrcEntity = prefab, IntGridHash = _hash, IntGridEntity = _intGridEntity,
                Position = int2.zero, RuleVersion = 1 });
            commands.Add(new EntityCommand { SrcEntity = prefab, IntGridHash = _hash, IntGridEntity = _intGridEntity,
                Position = int2.zero, RuleVersion = 2 });
            commands.Add(new EntityCommand { SrcEntity = prefab, IntGridHash = _hash, IntGridEntity = _intGridEntity,
                Position = secondCell, RuleVersion = 3 });

            _world.GetOrCreateSystem<EntityInitializationSystem>().Update(_world.Unmanaged);

            Assert.AreEqual(0, commands.Length);
            Assert.AreEqual(2, layer.SpawnedEntities.Count());
            Assert.IsTrue(layer.SpawnedEntities.TryGetFirstValue(int2.zero, out var first, out _));
            Assert.IsTrue(layer.SpawnedEntities.TryGetFirstValue(secondCell, out var second, out _));
            Assert.AreEqual(2, first.RuleVersion);
            Assert.AreEqual(3, second.RuleVersion);
        }

        private static BlobAssetReference<RuleBlob> CreateWallRule(bool uniquePrefab)
        {
            var builder = new BlobBuilder(Allocator.Temp);
            ref var rule = ref builder.ConstructRoot<RuleBlob>();
            rule.Chance = 100f;
            rule.RuleTransform = Transformation.Rotated;
            rule.UniquePrefabPerCell = uniquePrefab;
            rule.CellsToCheckCount = 2;
            var cells = builder.Allocate(ref rule.Cells, 8);
            var neighbors = new[] { new int2(0, -1), new int2(-1, 0), new int2(0, 1), new int2(1, 0) };
            for (var rotation = 0; rotation < 4; rotation++)
            {
                cells[rotation * 2] = new RuleCell { Offset = int2.zero, IntGridValue = 1 };
                cells[rotation * 2 + 1] = new RuleCell { Offset = neighbors[rotation], IntGridValue = -1 };
            }
            var sprites = builder.Allocate(ref rule.SpriteMeshes, 1);
            sprites[0] = new SpriteMesh(null);
            builder.Allocate(ref rule.SpritesWeights, 1)[0] = 1;
            rule.SpritesWeightSum = 1;
            builder.Allocate(ref rule.EntitiesWeights, 1)[0] = 1;
            builder.Allocate(ref rule.EntitiesPointers, 1)[0] = 0;
            rule.EntitiesWeightSum = 1;
            return builder.CreateBlobAssetReference<RuleBlob>(Allocator.Persistent);
        }

        private void UpdateSystem()
        {
            _system.Update(_world.Unmanaged);
            _entityManager.CompleteAllTrackedJobs();
        }

        private void UpdateSpawnSystems()
        {
            _world.GetOrCreateSystem<EntityCleanupSystem>().Update(_world.Unmanaged);
            _world.GetOrCreateSystem<EntityInitializationSystem>().Update(_world.Unmanaged);
            _entityManager.CompleteAllTrackedJobs();
        }

        private T GetSingleton<T>()
            where T : unmanaged, IComponentData
        {
            var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            var singleton = query.GetSingleton<T>();
            query.Dispose();
            return singleton;
        }

        private static bool Contains(in UnsafeList<int2> positions, int2 position)
        {
            foreach (var current in positions)
            {
                if (current.Equals(position)) return true;
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TerrainVertex
        {
            public float3 Position;
            public float3 Normal;
            public float2 TexCoord0;
        }
    }
}
