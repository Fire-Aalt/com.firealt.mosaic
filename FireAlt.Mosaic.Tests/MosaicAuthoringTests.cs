using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FireAlt.Core;
using FireAlt.Core.Editor;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using FireAlt.Mosaic.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace FireAlt.Mosaic.Tests
{
    public sealed class MosaicAuthoringTests
    {
        private struct TestCleanup : ICleanupComponentData
        {
        }

        private readonly List<string> _temporaryAssets = new();
        private GameObject _gridObject;
        private IntGridDefinition _intGrid;
        private Material _material;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = new World(nameof(MosaicAuthoringTests), WorldFlags.Editor);
            _gridObject = new GameObject("Grid", typeof(GridAuthoring));
            _intGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            _intGrid.name = "Test IntGrid";
            _intGrid.intGridValues.Add(new IntGridValueDefinition
            {
                value = 1,
                name = "Solid",
                color = Color.red,
            });

            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            _material = new Material(shader);
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated) _world.Dispose();
            Object.DestroyImmediate(_material);
            foreach (var assetPath in _temporaryAssets) AssetDatabase.DeleteAsset(assetPath);
            _temporaryAssets.Clear();
            Object.DestroyImmediate(_intGrid);
            Object.DestroyImmediate(_gridObject);
        }

        [Test]
        public void EditorBake_WritesSavedCellsAndStableLocalHash()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.isGlobal = false;
            tilemap.MutablePaintedCells.Add(new SerializedIntGridCell(new Vector2Int(-2, 3), 1));

            var firstEntity = FindEntity<IntGridData>(EditorBakingWorld.BakeInto(new[] { _gridObject }, _world));
            var secondEntity = FindEntity<IntGridData>(EditorBakingWorld.BakeInto(new[] { _gridObject }, _world));

            Assert.That(_world.EntityManager.HasComponent<TilemapTransform>(firstEntity), Is.True);
            Assert.That(_world.EntityManager.HasComponent<TilemapRendererData>(firstEntity), Is.True);
            Assert.That(_world.EntityManager.IsComponentEnabled<IntGridData>(firstEntity), Is.False);

            var firstHash = _world.EntityManager.GetComponentData<IntGridData>(firstEntity).Hash;
            var secondHash = _world.EntityManager.GetComponentData<IntGridData>(secondEntity).Hash;
            Assert.That(firstHash, Is.EqualTo(secondHash));

            var initialValues = _world.EntityManager.GetBuffer<IntGridInitialValueElement>(firstEntity);
            Assert.That(initialValues.Length, Is.EqualTo(1));
            Assert.That(initialValues[0].Position, Is.EqualTo(new int2(-2, 3)));
            Assert.That((short)initialValues[0].Value, Is.EqualTo(1));

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void EditorBake_BakesEntityRuleResults()
        {
            var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicAuthoringTests.prefab");
            var groupPath = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicAuthoringTests.asset");
            var intGridPath = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicAuthoringTestsIntGrid.asset");
            _temporaryAssets.Add(prefabPath);
            _temporaryAssets.Add(groupPath);
            _temporaryAssets.Add(intGridPath);
            var prefabSource = new GameObject("Rule Entity");
            var prefab = PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
            Object.DestroyImmediate(prefabSource);
            var group = ScriptableObject.CreateInstance<RuleGroup>();
            group.intGrid = _intGrid;
            var rule = new RuleGroup.Rule();
            rule.TileEntities.Add(new PrefabResult(prefab));
            group.rules.Add(rule);
            group.OnValidate();
            AssetDatabase.CreateAsset(group, groupPath);
            _intGrid.ruleGroups.Add(group);
            AssetDatabase.CreateAsset(_intGrid, intGridPath);
            EditorUtility.SetDirty(_intGrid);
            AssetDatabase.SaveAssets();

            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            var entity = FindEntity<IntGridData>(EditorBakingWorld.BakeInto(new[] { _gridObject }, _world));

            var rules = _world.EntityManager.GetBuffer<RuleBlobReferenceElement>(entity);
            var weightedEntities = _world.EntityManager.GetBuffer<WeightedEntityElement>(entity);
            Assert.That(rules.Length, Is.EqualTo(1));
            Assert.That(rules[0].Value.Value.EntitiesPointers.Length, Is.EqualTo(1));
            Assert.That(weightedEntities.Length, Is.EqualTo(1));
            Assert.That(weightedEntities[0].Value, Is.Not.EqualTo(Entity.Null));
            Assert.That(_world.EntityManager.Exists(weightedEntities[0].Value), Is.True);

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void TerrainBake_UsesRuntimeHashesForLayerBuffer()
        {
            var secondIntGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            secondIntGrid.name = "Second IntGrid";
            var terrainObject = new GameObject("Terrain", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.isGlobal = false;
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(secondIntGrid);

            var terrainEntity = FindEntity<FireAlt.Mosaic.Data.TerrainData>(
                EditorBakingWorld.BakeInto(new[] { _gridObject }, _world));

            var layers = _world.EntityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);
            var intGridQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(_world.EntityManager);
            var intGridData = intGridQuery.ToComponentDataArray<IntGridData>(Allocator.Temp);

            Assert.That(layers.Length, Is.EqualTo(2));
            Assert.That(intGridData.Length, Is.EqualTo(2));
            Assert.That(ContainsHash(intGridData, layers[0].IntGridHash), Is.True);
            Assert.That(ContainsHash(intGridData, layers[1].IntGridHash), Is.True);
            Assert.That(layers[0].IntGridHash, Is.Not.EqualTo(layers[1].IntGridHash));

            Object.DestroyImmediate(terrainObject);
            Object.DestroyImmediate(secondIntGrid);
        }

        [Test]
        public void SharedSetValue_RefreshesDualGridNeighbours()
        {
            var layer = new TilemapIntGridSingleton.IntGridLayer(8, Allocator.Persistent,
                new IntGridData { DualGrid = true }, false, Entity.Null);
            try
            {
                layer.SetValue(new int2(4, 5), 1);

                Assert.That(layer.IntGrid[new int2(4, 5)], Is.EqualTo((IntGridValue)1));
                Assert.That(layer.ChangedPositions.Contains(new int2(4, 5)), Is.True);
                Assert.That(layer.ChangedPositions.Contains(new int2(3, 5)), Is.True);
                Assert.That(layer.ChangedPositions.Contains(new int2(4, 4)), Is.True);
                Assert.That(layer.ChangedPositions.Contains(new int2(3, 4)), Is.True);
            }
            finally
            {
                layer.Dispose();
            }
        }

        [Test]
        public void Initialization_ReloadsSavedCellsForTheSameEntityAndHash()
        {
            var hash = (Unity.Entities.Hash128)UnityEngine.Hash128.Compute("Mosaic initialization test");
            _world.GetOrCreateSystem<RuleEngineSystem>();
            _world.GetOrCreateSystem<IntGridMeshDataSystem>();
            _world.GetOrCreateSystem<TerrainMeshDataSystem>();
            _world.GetOrCreateSystemManaged<MosaicPresentationSystem>();
            var initialization = _world.GetOrCreateSystemManaged<MosaicInitializationSystem>();

            var gridEntity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(gridEntity, new GridData
            {
                CellSize = 1f,
                Swizzle = Swizzle.XZY,
            });
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, new IntGridData
            {
                Hash = hash,
                DebugName = "Test",
            });
            _world.EntityManager.SetComponentEnabled<IntGridData>(entity, false);
            _world.EntityManager.AddComponentData(entity, new TilemapTransform { GridEntity = gridEntity });
            var initialValues = _world.EntityManager.AddBuffer<IntGridInitialValueElement>(entity);
            initialValues.Add(new IntGridInitialValueElement { Position = new int2(1, 2), Value = 1 });

            initialization.Update();
            _world.EntityManager.CompleteAllTrackedJobs();

            var singleton = _world.EntityManager.CreateEntityQuery(typeof(TilemapIntGridSingleton))
                .GetSingleton<TilemapIntGridSingleton>();
            Assert.That(_world.EntityManager.IsComponentEnabled<IntGridData>(entity), Is.True);
            Assert.That(singleton.IntGridLayers[hash].IntGrid[new int2(1, 2)], Is.EqualTo((IntGridValue)1));

            initialValues = _world.EntityManager.GetBuffer<IntGridInitialValueElement>(entity);
            initialValues.Clear();
            initialValues.Add(new IntGridInitialValueElement { Position = new int2(-4, 6), Value = 1 });
            _world.EntityManager.SetComponentEnabled<IntGridData>(entity, false);

            initialization.Update();
            _world.EntityManager.CompleteAllTrackedJobs();

            singleton = _world.EntityManager.CreateEntityQuery(typeof(TilemapIntGridSingleton))
                .GetSingleton<TilemapIntGridSingleton>();
            var layer = singleton.IntGridLayers[hash];
            Assert.That(layer.IntGrid.ContainsKey(new int2(1, 2)), Is.False);
            Assert.That(layer.IntGrid[new int2(-4, 6)], Is.EqualTo((IntGridValue)1));
        }

        [Test]
        public void Cleanup_RemovesLayersAndSpawnsWhenTheirBakedEntityIsGone()
        {
            var hash = (Unity.Entities.Hash128)UnityEngine.Hash128.Compute("Mosaic cleanup test");
            _world.GetOrCreateSystem<RuleEngineSystem>();
            var cleanup = _world.GetOrCreateSystem<EntityCleanupSystem>();
            var updateGroup = _world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            updateGroup.AddSystemToUpdateList(cleanup);

            var intGridEntity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(intGridEntity, new IntGridData { Hash = hash });
            var spawnedEntity = _world.EntityManager.CreateEntity();
            var dataSingleton = _world.EntityManager.CreateEntityQuery(typeof(TilemapIntGridSingleton))
                .GetSingleton<TilemapIntGridSingleton>();
            var dataLayer = new TilemapIntGridSingleton.IntGridLayer(8, Allocator.Persistent,
                new IntGridData { Hash = hash }, false, intGridEntity);
            dataLayer.SpawnedEntities.Add(int2.zero, spawnedEntity);
            dataSingleton.IntGridLayers.Add(hash, dataLayer);

            _world.EntityManager.AddComponent<TestCleanup>(intGridEntity);
            _world.EntityManager.DestroyEntity(intGridEntity);
            Assert.That(_world.EntityManager.Exists(intGridEntity), Is.True);
            Assert.That(_world.EntityManager.HasComponent<IntGridData>(intGridEntity), Is.False);
            updateGroup.Update();

            Assert.That(_world.EntityManager.Exists(spawnedEntity), Is.False);
            Assert.That(dataSingleton.IntGridLayers.ContainsKey(hash), Is.False);
        }

        [Test]
        public void Initialization_ReplacesAnIntGridCleanupShell()
        {
            var hash = (Unity.Entities.Hash128)UnityEngine.Hash128.Compute("Mosaic cleanup shell test");
            _world.GetOrCreateSystem<RuleEngineSystem>();
            _world.GetOrCreateSystem<IntGridMeshDataSystem>();
            _world.GetOrCreateSystem<TerrainMeshDataSystem>();
            _world.GetOrCreateSystemManaged<MosaicPresentationSystem>();
            var initialization = _world.GetOrCreateSystemManaged<MosaicInitializationSystem>();
            var gridEntity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(gridEntity, new GridData { CellSize = 1f });
            var previous = CreateIntGridEntity(hash, gridEntity);

            initialization.Update();
            _world.EntityManager.CompleteAllTrackedJobs();
            _world.EntityManager.AddComponent<TestCleanup>(previous);
            _world.EntityManager.DestroyEntity(previous);
            Assert.That(_world.EntityManager.Exists(previous), Is.True);
            Assert.That(_world.EntityManager.HasComponent<IntGridData>(previous), Is.False);

            var replacement = CreateIntGridEntity(hash, gridEntity);
            initialization.Update();
            _world.EntityManager.CompleteAllTrackedJobs();

            Assert.That(_world.EntityManager.IsComponentEnabled<IntGridData>(replacement), Is.True);
            var singleton = _world.EntityManager.CreateEntityQuery(typeof(TilemapIntGridSingleton))
                .GetSingleton<TilemapIntGridSingleton>();
            Assert.That(singleton.IntGridLayers[hash].IntGridEntity, Is.EqualTo(replacement));
        }

        [Test]
        public void TerrainPaintedCells_FollowDefinitionsAcrossReordering()
        {
            var secondIntGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            var terrainObject = new GameObject("Terrain", typeof(TilemapTerrainAuthoring));
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(secondIntGrid);

            var firstLayer = new SerializedIntGridLayer(_intGrid);
            firstLayer.MutableCells.Add(new SerializedIntGridCell(new Vector2Int(1, 2), 1));
            var secondLayer = new SerializedIntGridLayer(secondIntGrid);
            secondLayer.MutableCells.Add(new SerializedIntGridCell(new Vector2Int(8, 9), 2));
            terrain.MutablePaintedLayers.Add(firstLayer);
            terrain.MutablePaintedLayers.Add(secondLayer);

            terrain.intGridLayers.Reverse();
            typeof(TilemapTerrainAuthoring).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(terrain, null);

            Assert.That(terrain.PaintedLayers[0].IntGrid, Is.EqualTo(secondIntGrid));
            Assert.That(terrain.PaintedLayers[0].Cells[0].Position, Is.EqualTo(new Vector2Int(8, 9)));
            Assert.That(terrain.PaintedLayers[1].IntGrid, Is.EqualTo(_intGrid));
            Assert.That(terrain.PaintedLayers[1].Cells[0].Position, Is.EqualTo(new Vector2Int(1, 2)));

            Object.DestroyImmediate(terrainObject);
            Object.DestroyImmediate(secondIntGrid);
        }

        [Test]
        public void PaintingTarget_PersistsAndErasesOwnerCell()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);

            Assert.That(target.SetCell(new Vector2Int(7, -3), 1), Is.True);
            Assert.That(tilemap.PaintedCells.Count, Is.EqualTo(1));
            Assert.That(tilemap.PaintedCells[0].Position, Is.EqualTo(new Vector2Int(7, -3)));
            Assert.That(tilemap.PaintedCells[0].Value, Is.EqualTo(1));

            Assert.That(target.SetCell(new Vector2Int(7, -3), 0), Is.True);
            Assert.That(tilemap.PaintedCells, Is.Empty);

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void PaintingTarget_BatchesAndSortsBrushCells()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);
            var cells = new[]
            {
                new Vector2Int(2, 0),
                new Vector2Int(-1, -1),
                new Vector2Int(1, 0),
            };

            Assert.That(target.SetCells(cells, 1), Is.True);
            Assert.That(tilemap.PaintedCells.Count, Is.EqualTo(3));
            Assert.That(tilemap.PaintedCells[0].Position, Is.EqualTo(new Vector2Int(-1, -1)));
            Assert.That(tilemap.PaintedCells[1].Position, Is.EqualTo(new Vector2Int(1, 0)));
            Assert.That(tilemap.PaintedCells[2].Position, Is.EqualTo(new Vector2Int(2, 0)));

            Assert.That(target.SetCells(new[] { cells[0], cells[2] }, 0), Is.True);
            Assert.That(tilemap.PaintedCells.Count, Is.EqualTo(1));
            Assert.That(tilemap.PaintedCells[0].Position, Is.EqualTo(new Vector2Int(-1, -1)));

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void PaintingTarget_CentersDualGridCellsOnGridVertices()
        {
            _intGrid.useDualGrid = true;
            var tilemapObject = CreateTilemap("Tilemap");
            var target = new MosaicPaintingTarget(tilemapObject.GetComponent<TilemapAuthoring>());
            var corners = new Vector3[4];

            target.GetCellCorners(new Vector2Int(2, 3), corners, 0f);

            Assert.That(corners[0], Is.EqualTo(new Vector3(1.5f, 0f, 2.5f)));
            Assert.That(corners[2], Is.EqualTo(new Vector3(2.5f, 0f, 3.5f)));

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void PaintingStroke_AppliesEachIncrementImmediately()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);

            using (var stroke = target.BeginStroke(1))
            {
                Assert.That(stroke.SetCells(new[] { new Vector2Int(3, 2) }), Is.True);
                Assert.That(tilemap.PaintedCells.Count, Is.EqualTo(1));
                Assert.That(stroke.SetCells(new[] { new Vector2Int(-1, -4) }), Is.True);
                Assert.That(tilemap.PaintedCells.Count, Is.EqualTo(2));
                Assert.That(tilemap.PaintedCells[0].Position, Is.EqualTo(new Vector2Int(-1, -4)));
                Assert.That(tilemap.PaintedCells[1].Position, Is.EqualTo(new Vector2Int(3, 2)));
            }

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void PaintingTool_IsAlwaysAvailable()
        {
            var tool = ScriptableObject.CreateInstance<MosaicPaintingTool>();
            try
            {
                Assert.That(tool.IsAvailable(), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(tool);
            }
        }

        [Test]
        public void PaintingBrush_UsesPoICircularRadius()
        {
            MosaicPaintingSession.BrushRadius = 2;
            try
            {
                Assert.That(MosaicPaintingTool.IsWithinBrushRadius(2, 0), Is.True);
                Assert.That(MosaicPaintingTool.IsWithinBrushRadius(1, 1), Is.True);
                Assert.That(MosaicPaintingTool.IsWithinBrushRadius(2, 1), Is.False);
            }
            finally
            {
                MosaicPaintingSession.BrushRadius = 0;
            }
        }

        [Test]
        public void PaintingPreview_ReseedsExistingLayerWithoutClearCommand()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.MutablePaintedCells.Add(new SerializedIntGridCell(new Vector2Int(-2, 3), 1));
            var target = new MosaicPaintingTarget(tilemap);
            var hash = target.IntGridHash;
            var intGridEntity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(intGridEntity, new IntGridData
            {
                Hash = hash,
                DualGrid = true,
            });

            _world.GetOrCreateSystem<RuleEngineSystem>();
            var singleton = _world.EntityManager.CreateEntityQuery(typeof(TilemapIntGridSingleton))
                .GetSingleton<TilemapIntGridSingleton>();
            singleton.IntGridLayers.Add(hash, new TilemapIntGridSingleton.IntGridLayer(8, Allocator.Persistent,
                new IntGridData { Hash = hash, DualGrid = true }, false, intGridEntity));
            ref var layer = ref singleton.IntGridLayers.GetValueAsRef(hash);
            layer.SetValue(new int2(8, 9), 1);
            layer.ChangedPositions.Clear();

            MosaicPaintingPreview.Reseed(_world, target);

            Assert.That(layer.IntGrid.ContainsKey(new int2(8, 9)), Is.False);
            Assert.That(layer.IntGrid[new int2(-2, 3)], Is.EqualTo((IntGridValue)1));
            Assert.That(layer.ChangedPositions.Contains(new int2(8, 9)), Is.True);
            Assert.That(layer.ChangedPositions.Contains(new int2(-3, 2)), Is.True);

            layer.ChangedPositions.Clear();
            MosaicPaintingPreview.Apply(_world, target, new[] { new Vector2Int(5, -6) }, 1);
            Assert.That(layer.IntGrid[new int2(5, -6)], Is.EqualTo((IntGridValue)1));
            Assert.That(layer.ChangedPositions.Contains(new int2(5, -6)), Is.True);

            Object.DestroyImmediate(tilemapObject);
        }

        [Test]
        public void PaintingPreview_RebuildReplacesItsBakedEntitiesAndSynchronizesTransforms()
        {
            var tilemapObject = CreateTilemap("Tilemap");
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.isGlobal = false;
            var target = new MosaicPaintingTarget(tilemap);
            using var preview = new MosaicPaintingPreview();

            preview.Rebuild(_world, new[] { target });
            var firstRenderer = FindEntity<TilemapRendererData>(GetPreviewEntities());

            Assert.That(_world.EntityManager.HasComponent<HybridEntitySync>(firstRenderer), Is.True);
            Assert.That(_world.EntityManager.GetComponentData<HybridEntitySync>(firstRenderer).MonoBehaviour.Value,
                Is.EqualTo(tilemap));

            preview.Rebuild(_world, new[] { target });
            var previewEntities = GetPreviewEntities();
            var renderer = FindEntity<TilemapRendererData>(previewEntities);
            var grid = FindEntity<GridData>(previewEntities);

            Assert.That(_world.EntityManager.Exists(firstRenderer), Is.False);
            Assert.That(previewEntities.Count(entity => _world.EntityManager.HasComponent<TilemapRendererData>(entity)),
                Is.EqualTo(1));
            Assert.That(_world.EntityManager.GetComponentData<HybridEntitySync>(renderer).MonoBehaviour.Value,
                Is.EqualTo(tilemap));
            Assert.That(_world.EntityManager.GetComponentData<HybridEntitySync>(grid).MonoBehaviour.Value,
                Is.EqualTo(_gridObject.GetComponent<GridAuthoring>()));

            Object.DestroyImmediate(tilemapObject);
        }

        private GameObject CreateTilemap(string name)
        {
            var tilemapObject = new GameObject(name, typeof(TilemapAuthoring));
            tilemapObject.transform.SetParent(_gridObject.transform);
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.intGrid = _intGrid;
            tilemap.renderingData.material = _material;
            return tilemapObject;
        }

        private Entity CreateIntGridEntity(Unity.Entities.Hash128 hash, Entity gridEntity)
        {
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, new IntGridData { Hash = hash, DebugName = "Test" });
            _world.EntityManager.SetComponentEnabled<IntGridData>(entity, false);
            _world.EntityManager.AddComponentData(entity, new TilemapTransform { GridEntity = gridEntity });
            _world.EntityManager.AddBuffer<IntGridInitialValueElement>(entity);
            return entity;
        }

        private Entity FindEntity<T>(Entity[] entities)
            where T : unmanaged, IComponentData
        {
            foreach (var entity in entities)
            {
                if (_world.EntityManager.HasComponent<T>(entity)) return entity;
            }

            Assert.Fail($"No baked entity contains {typeof(T).Name}.");
            return Entity.Null;
        }

        private Entity[] GetPreviewEntities()
        {
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MosaicPaintingPreviewEntity>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(_world.EntityManager);
            return query.ToEntityArray(Allocator.Temp).ToArray();
        }

        private static bool ContainsHash(NativeArray<IntGridData> data, Unity.Entities.Hash128 hash)
        {
            foreach (var value in data)
            {
                if (value.Hash == hash) return true;
            }

            return false;
        }
    }
}
