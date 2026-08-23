using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace FireAlt.Mosaic.Tests
{
    public sealed class RuleEngineSystemTests
    {
        private readonly Hash128 _hash = new(71u, 72u, 73u, 74u);

        private World _world;
        private EntityManager _entityManager;
        private SystemHandle _system;
        private Entity _intGridEntity;

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
        }

        [Test]
        public void ChangedRuleBuffer_ForcesOneFullRefreshAndClearsStaleResult()
        {
            UpdateSystem();

            var dataSingleton = GetSingleton<TilemapIntGridSingleton>();
            ref var layer = ref dataSingleton.IntGridLayers.GetValueAsRef(_hash);
            var stalePosition = new int2(4, 4);
            layer.IntGrid[int2.zero] = 1;
            layer.RuleGrid[stalePosition] = 123;
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
            beforeSingleLayer.RuleGrid[firstStalePosition] = 1;
            beforeSingleLayer.RuleGrid[secondStalePosition] = 2;
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

        private void UpdateSystem()
        {
            _system.Update(_world.Unmanaged);
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
    }
}
