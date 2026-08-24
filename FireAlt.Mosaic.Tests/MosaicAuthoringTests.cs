using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Unity.Transforms;

namespace FireAlt.Mosaic.Tests
{
    public sealed class MosaicAuthoringTests
    {
        private sealed class CallbackTestWindow : EditorWindow
        {
        }

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

        private MosaicPaintingTarget CreateEntityPaintingTarget(float3 position,
            Unity.Entities.Hash128? targetHash = null)
        {
            var intGridHash = targetHash ?? new Unity.Entities.Hash128(1u, 0u, 0u, 0u);
            var rendererHash = new Unity.Entities.Hash128(2u, 0u, 0u, 0u);
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, new IntGridData
            {
                Hash = intGridHash,
                DebugName = "Entity IntGrid",
            });
            _world.EntityManager.AddComponentData(entity, new TilemapTransform
            {
                CellSize = 1f,
                Swizzle = Swizzle.XZY,
            });
            _world.EntityManager.AddComponentData(entity, new TilemapRendererData { MeshHash = rendererHash });
            _world.EntityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(position) });
            _world.EntityManager.AddBuffer<IntGridValueElement>(entity).Add(new IntGridValueElement
            {
                Value = 1,
                Name = "Solid",
                Color = Color.red,
            });

            return new MosaicPaintingTarget(_world, entity, entity, "Entity IntGrid", false, 0);
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

        [Test]
        public void BakerHash_LocalIsAlwaysPendingAndGlobalIsStable()
        {
            var globalHash = new Unity.Entities.Hash128(11u, 12u, 13u, 14u);
            typeof(IntGridDefinition).GetField("<Hash>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_intGrid, globalHash);

            Assert.AreEqual(default(Unity.Entities.Hash128), BakerUtils.GetHash(_intGrid, false));
            Assert.AreEqual(default(Unity.Entities.Hash128), BakerUtils.GetHash(_intGrid, false));
            Assert.AreEqual(globalHash, BakerUtils.GetHash(_intGrid, true));
        }

        [Test]
        public void TerrainBake_StoresOrderedIntGridEntityReferences()
        {
            var second = ScriptableObject.CreateInstance<IntGridDefinition>();
            second.name = "Second IntGrid";
            var terrainObject = new GameObject("Terrain", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.isGlobal = false;
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(second);

            try
            {
                var entities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
                var terrainEntity = FindEntity<FireAlt.Mosaic.Data.TerrainData>(entities);
                var layers = _world.EntityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);

                Assert.AreEqual(2, layers.Length);
                Assert.AreEqual(_intGrid.name,
                    _world.EntityManager.GetComponentData<IntGridData>(layers[0].IntGridEntity).DebugName.ToString());
                Assert.AreEqual(second.name,
                    _world.EntityManager.GetComponentData<IntGridData>(layers[1].IntGridEntity).DebugName.ToString());
                Assert.AreEqual(default(Unity.Entities.Hash128),
                    _world.EntityManager.GetComponentData<IntGridData>(layers[0].IntGridEntity).Hash);
                Assert.AreEqual(default(Unity.Entities.Hash128),
                    _world.EntityManager.GetComponentData<IntGridData>(layers[1].IntGridEntity).Hash);
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void TilemapBake_WithoutIntGrid_BakesNoMosaicDataOrErrors()
        {
            var tilemapObject = new GameObject("Empty Tilemap", typeof(TilemapAuthoring));
            try
            {
                var entities = EditorBakingWorld.BakeInto(new[] { tilemapObject }, _world);

                Assert.IsFalse(entities.Any(entity => _world.EntityManager.HasComponent<IntGridData>(entity)));
                Assert.IsFalse(entities.Any(entity => _world.EntityManager.HasComponent<TilemapRendererData>(entity)));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(tilemapObject);
            }
        }

        [Test]
        public void TerrainBake_WithOnlyNullLayers_BakesNoMosaicDataOrErrors()
        {
            var terrainObject = new GameObject("Empty Terrain", typeof(TilemapTerrainAuthoring));
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.intGridLayers.Add(null);
            terrain.intGridLayers.Add(null);

            try
            {
                var entities = EditorBakingWorld.BakeInto(new[] { terrainObject }, _world);

                Assert.IsFalse(entities.Any(entity => _world.EntityManager.HasComponent<IntGridData>(entity)));
                Assert.IsFalse(entities.Any(entity =>
                    _world.EntityManager.HasComponent<FireAlt.Mosaic.Data.TerrainData>(entity)));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void TerrainBake_NullLayerGapsUseCompactValidOrder()
        {
            var second = ScriptableObject.CreateInstance<IntGridDefinition>();
            second.name = "Second IntGrid";
            var terrainObject = new GameObject("Terrain With Gaps", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.isGlobal = false;
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(null);
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(null);
            terrain.intGridLayers.Add(second);

            try
            {
                var validLayers = terrain.ValidLayers().ToArray();
                Assert.AreEqual(2, validLayers.Length);
                Assert.AreEqual(0, validLayers[0].Index);
                Assert.AreEqual(_intGrid, validLayers[0].Definition);
                Assert.AreEqual(1, validLayers[1].Index);
                Assert.AreEqual(second, validLayers[1].Definition);

                var entities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
                var terrainEntity = FindEntity<FireAlt.Mosaic.Data.TerrainData>(entities);
                var layers = _world.EntityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);

                Assert.AreEqual(2, layers.Length);
                Assert.AreEqual(_intGrid.name,
                    _world.EntityManager.GetComponentData<IntGridData>(layers[0].IntGridEntity).DebugName.ToString());
                Assert.AreEqual(second.name,
                    _world.EntityManager.GetComponentData<IntGridData>(layers[1].IntGridEntity).DebugName.ToString());
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void TerrainBake_DuplicateValidDefinitionsStillFailAcrossNullGaps()
        {
            var terrainObject = new GameObject("Duplicate Terrain", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(null);
            terrain.intGridLayers.Add(_intGrid);

            try
            {
                var validLayers = terrain.ValidLayers().ToArray();
                var exception = Assert.Throws<System.Exception>(() =>
                    TilemapTerrainAuthoring.ValidateUniqueLayers(validLayers));
                StringAssert.Contains("Duplicate IntGridDefinition", exception.Message);
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void RuleGroupSerializedEdits_RemainDirtyUntilExplicitSave()
        {
            var ruleGroup = ScriptableObject.CreateInstance<RuleGroup>();
            ruleGroup.rules.Add(new RuleGroup.Rule());
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicRuleGroupSaveTests.asset");
            _temporaryAssets.Add(path);
            AssetDatabase.CreateAsset(ruleGroup, path);
            AssetDatabase.SaveAssetIfDirty(ruleGroup);

            var serializedRuleGroup = new SerializedObject(ruleGroup);
            var rule = serializedRuleGroup.FindProperty(nameof(RuleGroup.rules)).GetArrayElementAtIndex(0);
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            RuleController.ApplyEnabled(rule, false);
            RuleController.ApplyChance(rule, 25f);
            Undo.CollapseUndoOperations(undoGroup);

            Assert.AreEqual(0, (int)ruleGroup.rules[0].enabled);
            Assert.AreEqual(25f, ruleGroup.rules[0].ruleChance);
            Assert.IsTrue(EditorUtility.IsDirty(ruleGroup));

            Undo.PerformUndo();
            Assert.AreEqual(RuleGroup.Enabled.Enabled, ruleGroup.rules[0].enabled);
            Assert.AreEqual(100f, ruleGroup.rules[0].ruleChance);
            Undo.PerformRedo();
            Assert.AreEqual(0, (int)ruleGroup.rules[0].enabled);
            Assert.AreEqual(25f, ruleGroup.rules[0].ruleChance);
            Assert.IsTrue(EditorUtility.IsDirty(ruleGroup));

            AssetDatabase.SaveAssets();
            Assert.IsFalse(EditorUtility.IsDirty(ruleGroup));
            Undo.ClearAll();
        }

        [Test]
        public void WeightedSpriteList_AddThenRemoveLastEntryDoesNotBindDeletedIndex()
        {
            var ruleGroup = ScriptableObject.CreateInstance<RuleGroup>();
            ruleGroup.intGrid = _intGrid;
            var rule = new RuleGroup.Rule();
            rule.Bind(ruleGroup);
            ruleGroup.rules.Add(rule);

            try
            {
                var serializedRuleGroup = new SerializedObject(ruleGroup);
                var serializedRule = serializedRuleGroup.FindProperty(nameof(RuleGroup.rules))
                    .GetArrayElementAtIndex(0);
                var sprites = serializedRule.FindPropertyRelative(nameof(RuleGroup.Rule.TileSprites));
                sprites.arraySize++;
                sprites.GetArrayElementAtIndex(0).boxedValue = new SpriteResult();
                serializedRuleGroup.ApplyModifiedProperties();
                serializedRuleGroup.Update();
                sprites.DeleteArrayElementAtIndex(0);
                serializedRuleGroup.ApplyModifiedProperties();
                serializedRuleGroup.Update();
                Assert.AreEqual(0, sprites.arraySize);

                var element = EditorResources.WeightedListElementAsset.Instantiate();
                var controller = new WeightedListEntryController();
                controller.SetVisualElement<Sprite>(element);
                controller.BindData<Sprite>(0, sprites);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(ruleGroup);
            }
        }

        [Test]
        public void RuleResultValidation_NullSpriteFailsAndValidSpriteRecovers()
        {
            var ruleGroup = ScriptableObject.CreateInstance<RuleGroup>();
            var texture = new Texture2D(1, 1);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            ruleGroup.name = "Invalid Results";
            ruleGroup.intGrid = _intGrid;
            var rule = new RuleGroup.Rule();
            rule.Bind(ruleGroup);
            rule.TileSprites.Add(new SpriteResult());
            ruleGroup.rules.Add(rule);
            _intGrid.ruleGroups.Add(ruleGroup);

            try
            {
                Assert.IsFalse(BakerUtils.TryValidateRuleResults(_intGrid, out var validationError));
                StringAssert.Contains("has no Sprite assigned", validationError);

                rule.TileSprites[0].result = sprite;
                Assert.IsTrue(BakerUtils.TryValidateRuleResults(_intGrid, out validationError));
                Assert.IsNull(validationError);
            }
            finally
            {
                _intGrid.ruleGroups.Remove(ruleGroup);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(ruleGroup);
            }
        }

        [Test]
        public void TilemapBake_InvalidRuleUsesEmptyRendererAndValidRuleRecovers()
        {
            var group = ScriptableObject.CreateInstance<RuleGroup>();
            var texture = new Texture2D(1, 1);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            var tilemapObject = CreateTilemap("Recoverable Tilemap");
            group.name = "Recoverable Rules";
            group.intGrid = _intGrid;
            var rule = new RuleGroup.Rule();
            rule.Bind(group);
            rule.TileSprites.Add(new SpriteResult());
            group.rules.Add(rule);
            _intGrid.ruleGroups.Add(group);

            try
            {
                LogAssert.Expect(LogType.Error, new Regex("Mosaic did not bake.*has no Sprite assigned"));
                var invalidEntities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
                var invalidTilemap = FindEntity<TilemapRendererData>(invalidEntities);
                Assert.AreEqual(0, _world.EntityManager.GetBuffer<RuleBlobReferenceElement>(invalidTilemap).Length);

                rule.TileSprites[0].result = sprite;
                var validEntities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
                var validTilemap = FindEntity<TilemapRendererData>(validEntities);
                Assert.AreEqual(1, _world.EntityManager.GetBuffer<RuleBlobReferenceElement>(validTilemap).Length);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                _intGrid.ruleGroups.Remove(group);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(group);
                Object.DestroyImmediate(tilemapObject);
            }
        }

        [Test]
        public void RuleRowRebinding_EditsOnlyTheCurrentlyBoundSerializedProperty()
        {
            var ruleGroup = ScriptableObject.CreateInstance<RuleGroup>();
            var window = EditorWindow.CreateWindow<CallbackTestWindow>();
            ruleGroup.intGrid = _intGrid;
            ruleGroup.rules.Add(new RuleGroup.Rule());
            ruleGroup.rules.Add(new RuleGroup.Rule());

            var root = new VisualElement();
            var fields = typeof(RuleController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            var enabledToggle = (VisualElement)System.Activator.CreateInstance(
                fields.Single(field => field.Name == "_enabledToggle").FieldType);
            var chanceSlider = (VisualElement)System.Activator.CreateInstance(
                fields.Single(field => field.Name == "_chanceSlider").FieldType);
            enabledToggle.name = "EnabledToggle";
            chanceSlider.name = "ChanceSlider";
            chanceSlider.GetType().GetProperty("highValue")?.SetValue(chanceSlider, 100f);
            root.Add(enabledToggle);
            root.Add(chanceSlider);
            root.Add(new VisualElement { name = "MatrixCol" });
            root.Add(new VisualElement { name = "RuleTransformations" });
            root.Add(new VisualElement { name = "ResultTransformations" });
            window.rootVisualElement.Add(root);
            window.Show();

            try
            {
                var controller = new RuleController();
                controller.SetVisualElement(ruleGroup, root);
                var serializedRuleGroup = new SerializedObject(ruleGroup);
                var rules = serializedRuleGroup.FindProperty(nameof(RuleGroup.rules));
                controller.BindData(0, rules);
                controller.BindData(1, rules);

                enabledToggle.GetType().GetProperty("value")?.SetValue(enabledToggle, false);
                chanceSlider.GetType().GetProperty("value")?.SetValue(chanceSlider, 40f);

                Assert.AreEqual(RuleGroup.Enabled.Enabled, ruleGroup.rules[0].enabled);
                Assert.AreEqual(100f, ruleGroup.rules[0].ruleChance);
                Assert.AreEqual(0, (int)ruleGroup.rules[1].enabled);
                Assert.AreEqual(40f, ruleGroup.rules[1].ruleChance);
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(ruleGroup);
            }
        }

        [Test]
        public void TransformationButton_RebindingDoesNotRegisterDuplicateClickHandlers()
        {
            var ruleGroup = ScriptableObject.CreateInstance<RuleGroup>();
            var window = EditorWindow.CreateWindow<CallbackTestWindow>();
            ruleGroup.rules.Add(new RuleGroup.Rule());
            var serializedRuleGroup = new SerializedObject(ruleGroup);
            var rule = serializedRuleGroup.FindProperty(nameof(RuleGroup.rules)).GetArrayElementAtIndex(0);
            var transformation = rule.FindPropertyRelative(nameof(RuleGroup.Rule.ruleTransformation));
            var button = new TransformationButton(Transformation.MirrorX, string.Empty);
            window.rootVisualElement.Add(button);
            window.Show();
            button.Bind(transformation);
            button.Bind(transformation);

            try
            {
                using (var click = ClickEvent.GetPooled())
                {
                    click.target = button;
                    button.SendEvent(click);
                }

                Assert.AreEqual(Transformation.MirrorX, ruleGroup.rules[0].ruleTransformation);
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(ruleGroup);
            }
        }

        [Test]
        public void TilemapBake_ExpandsPackedRectanglesIntoInitialValues()
        {
            var tilemap = CreateTilemap("Packed Tilemap").GetComponent<TilemapAuthoring>();
            var positions = new[]
            {
                new Vector2Int(-2, 3),
                new Vector2Int(-1, 3),
                new Vector2Int(-2, 4),
                new Vector2Int(5, -6),
            };
            Assert.IsTrue(new MosaicPaintingTarget(tilemap).SetCells(positions, 1));

            var entities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
            var intGridEntity = FindEntity<IntGridData>(entities);
            var initialValues = _world.EntityManager.GetBuffer<IntGridInitialValueElement>(intGridEntity);
            var actualPositions = new List<Vector2Int>(initialValues.Length);
            foreach (var value in initialValues)
            {
                actualPositions.Add(new Vector2Int(value.Position.x, value.Position.y));
                Assert.AreEqual(1, (short)value.Value);
            }

            Assert.AreEqual(positions.Length, initialValues.Length);
            CollectionAssert.AreEquivalent(positions, actualPositions);
        }

        [Test]
        public void TerrainBake_ExpandsOnlyTheMatchingPackedLayer()
        {
            var second = ScriptableObject.CreateInstance<IntGridDefinition>();
            var terrainObject = new GameObject("Packed Terrain", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.isGlobal = false;
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(second);
            terrain.MutablePaintedLayers.Add(new SerializedIntGridLayer(_intGrid));
            terrain.MutablePaintedLayers.Add(new SerializedIntGridLayer(second));
            var positions = new[] { new Vector2Int(1, 2), new Vector2Int(2, 2), new Vector2Int(2, 3) };

            try
            {
                Assert.IsTrue(new MosaicPaintingTarget(terrain, _intGrid, 0).SetCells(positions, 1));
                var entities = EditorBakingWorld.BakeInto(new[] { _gridObject }, _world);
                var terrainEntity = FindEntity<FireAlt.Mosaic.Data.TerrainData>(entities);
                var layers = _world.EntityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);
                var firstValues = _world.EntityManager.GetBuffer<IntGridInitialValueElement>(layers[0].IntGridEntity);
                var secondValues = _world.EntityManager.GetBuffer<IntGridInitialValueElement>(layers[1].IntGridEntity);
                var actualPositions = new List<Vector2Int>(firstValues.Length);
                foreach (var value in firstValues)
                {
                    actualPositions.Add(new Vector2Int(value.Position.x, value.Position.y));
                }

                CollectionAssert.AreEquivalent(positions, actualPositions);
                Assert.AreEqual(0, secondValues.Length);
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(terrainObject);
            }
        }

        [Test]
        public void Initialization_AssignsLocalAndRendererHashesFromEntityLifetime()
        {
            var first = _world.EntityManager.CreateEntity(typeof(IntGridData), typeof(TilemapRendererData));
            var second = _world.EntityManager.CreateEntity(typeof(IntGridData));
            var terrain = _world.EntityManager.CreateEntity(typeof(TilemapRendererData));
            _world.EntityManager.AddBuffer<TilemapTerrainLayerElement>(terrain).Add(
                new TilemapTerrainLayerElement { IntGridEntity = second });
            var globalHash = new Unity.Entities.Hash128(21u, 22u, 23u, 24u);
            _world.EntityManager.SetComponentData(second, new IntGridData { Hash = default });
            var global = _world.EntityManager.CreateEntity(typeof(IntGridData), typeof(TilemapRendererData));
            _world.EntityManager.SetComponentData(global, new IntGridData { Hash = globalHash });
            _world.EntityManager.SetComponentData(global, new TilemapRendererData { MeshHash = globalHash });

            MosaicInitializationSystem.AssignRuntimeHashes(_world.EntityManager);

            var firstHash = _world.EntityManager.GetComponentData<IntGridData>(first).Hash;
            var secondHash = _world.EntityManager.GetComponentData<IntGridData>(second).Hash;
            Assert.AreNotEqual(default(Unity.Entities.Hash128), firstHash);
            Assert.AreNotEqual(default(Unity.Entities.Hash128), secondHash);
            Assert.AreNotEqual(firstHash, secondHash);
            Assert.AreEqual(firstHash,
                _world.EntityManager.GetComponentData<TilemapRendererData>(first).MeshHash);
            Assert.AreEqual(secondHash,
                _world.EntityManager.GetComponentData<TilemapRendererData>(terrain).MeshHash);
            Assert.AreEqual(globalHash, _world.EntityManager.GetComponentData<IntGridData>(global).Hash);
            Assert.AreEqual(globalHash,
                _world.EntityManager.GetComponentData<TilemapRendererData>(global).MeshHash);

            MosaicInitializationSystem.AssignRuntimeHashes(_world.EntityManager);
            Assert.AreEqual(firstHash, _world.EntityManager.GetComponentData<IntGridData>(first).Hash);

            _world.EntityManager.DestroyEntity(first);
            var replacement = _world.EntityManager.CreateEntity(typeof(IntGridData), typeof(TilemapRendererData));
            MosaicInitializationSystem.AssignRuntimeHashes(_world.EntityManager);
            Assert.AreNotEqual(firstHash, _world.EntityManager.GetComponentData<IntGridData>(replacement).Hash);
        }

        [Test]
        public void PaintingTarget_AuthoringTargetWritesSerializedCells()
        {
            var tilemap = CreateTilemap("Authoring Tilemap").GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);

            Assert.IsTrue(target.IsPaintable);
            Assert.IsTrue(target.SetCell(new Vector2Int(2, 3), 1));
            Assert.AreEqual(1, tilemap.PaintedCells.Count);
            Assert.AreEqual(new Vector2Int(2, 3), tilemap.PaintedCells[0].Position);
            Assert.AreEqual(1, tilemap.PaintedCells[0].Value);
        }

        [Test]
        public void PaintingStroke_UpdatesCellsBeforePackingAndBytesOnlyOnDispose()
        {
            var tilemap = CreateTilemap("Deferred Packing Tilemap").GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);
            var stroke = target.BeginStroke(1);
            try
            {
                Assert.IsTrue(stroke.SetCells(new[] { new Vector2Int(2, 3), new Vector2Int(3, 3) }));
                Assert.AreEqual(2, tilemap.PaintedCells.Count);
                Assert.IsEmpty(tilemap.PaintedData.Bytes);
            }
            finally
            {
                stroke.Dispose();
            }

            Assert.IsNotEmpty(tilemap.PaintedData.Bytes);
            Assert.AreEqual(1, tilemap.PaintedData.Rectangles.Count);
            Assert.AreEqual(new Vector2Int(2, 1), tilemap.PaintedData.Rectangles[0].Size);
        }

        [Test]
        public void PaintingStroke_NoOpLeavesPackedBytesUntouched()
        {
            var tilemap = CreateTilemap("No-op Packing Tilemap").GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);
            var position = new Vector2Int(-4, 7);
            Assert.IsTrue(target.SetCell(position, 1));
            var bytes = tilemap.PaintedData.Bytes.ToArray();

            using (var stroke = target.BeginStroke(1))
            {
                Assert.IsFalse(stroke.SetCells(new[] { position }));
            }

            CollectionAssert.AreEqual(bytes, tilemap.PaintedData.Bytes);
        }

        [Test]
        public void PackedStorage_RepresentativePrefabReducesPaintedYamlByAtLeast90Percent()
        {
            var prefabRoot = new GameObject("Packed Storage", typeof(GridAuthoring));
            var tilemapObject = new GameObject("Tilemap", typeof(TilemapAuthoring));
            tilemapObject.transform.SetParent(prefabRoot.transform);
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.intGrid = _intGrid;
            tilemap.renderingData.material = _material;
            var cells = new List<Vector2Int>(984);
            for (var rectangleIndex = 0; rectangleIndex < 92; rectangleIndex++)
            {
                var width = rectangleIndex < 64 ? 11 : 10;
                for (var x = 0; x < width; x++) cells.Add(new Vector2Int(x - 20, rectangleIndex * 2 - 100));
            }

            var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicPackedStorageTests.prefab");
            _temporaryAssets.Add(prefabPath);
            try
            {
                Assert.IsTrue(new MosaicPaintingTarget(tilemap).SetCells(cells, 1));
                Assert.AreEqual(92, tilemap.PaintedData.Rectangles.Count);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);

                var packedYamlSize = File.ReadLines(prefabPath).Single(line => line.Contains("_bytes:")).Length;
                var legacyYamlSize = cells.Sum(cell =>
                    $"  - _position: {{x: {cell.x}, y: {cell.y}}}\n    _value: 1\n".Length);
                Assert.Less(packedYamlSize, legacyYamlSize * 0.1);
            }
            finally
            {
                Object.DestroyImmediate(prefabRoot);
            }
        }

        [Test]
        public void PaintingStroke_AppendsAndUpdatesWithoutSorting()
        {
            _intGrid.intGridValues.Add(new IntGridValueDefinition { value = 2, name = "Updated" });
            var tilemap = CreateTilemap("Unordered Tilemap").GetComponent<TilemapAuthoring>();
            var retained = new Vector2Int(5, 5);
            var updated = new Vector2Int(2, -3);
            var appended = new Vector2Int(-10, -10);
            var target = new MosaicPaintingTarget(tilemap);
            Assert.IsTrue(target.SetCells(new[] { retained, updated }, 1));

            using var stroke = target.BeginStroke(2);
            Assert.IsTrue(stroke.SetCells(new[] { updated, appended, appended }));

            CollectionAssert.AreEqual(new[] { updated, appended }, stroke.ChangedCells);
            CollectionAssert.AreEqual(new[] { retained, updated, appended },
                tilemap.PaintedCells.Select(cell => cell.Position).ToArray());
            Assert.AreEqual(2, tilemap.PaintedCells[1].Value);
            Assert.AreEqual(2, tilemap.PaintedCells[2].Value);

            Assert.IsFalse(stroke.SetCells(new[] { updated, appended }));
            Assert.IsEmpty(stroke.ChangedCells);
        }

        [Test]
        public void PaintingStroke_SwapRemoveRepairsMovedCellIndex()
        {
            var tilemap = CreateTilemap("Erase Tilemap").GetComponent<TilemapAuthoring>();
            var retained = new Vector2Int(1, 1);
            var erased = new Vector2Int(2, 2);
            var moved = new Vector2Int(3, 3);
            var target = new MosaicPaintingTarget(tilemap);
            Assert.IsTrue(target.SetCells(new[] { retained, erased, moved }, 1));

            using var stroke = target.BeginStroke(0);
            Assert.IsTrue(stroke.SetCells(new[] { erased }));
            CollectionAssert.AreEqual(new[] { retained, moved },
                tilemap.PaintedCells.Select(cell => cell.Position).ToArray());

            Assert.IsTrue(stroke.SetCells(new[] { moved }));
            CollectionAssert.AreEqual(new[] { retained },
                tilemap.PaintedCells.Select(cell => cell.Position).ToArray());
            Assert.IsFalse(stroke.SetCells(new[] { erased, moved }));
            Assert.IsEmpty(stroke.ChangedCells);
        }

        [Test]
        public void PaintingStroke_TerrainWritesOnlySelectedLayer()
        {
            var otherIntGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            try
            {
                var terrainObject = new GameObject("Terrain", typeof(TilemapTerrainAuthoring));
                terrainObject.transform.SetParent(_gridObject.transform);
                var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
                terrain.intGridLayers.Add(_intGrid);
                terrain.intGridLayers.Add(otherIntGrid);
                terrain.MutablePaintedLayers.Add(new SerializedIntGridLayer(_intGrid));
                terrain.MutablePaintedLayers.Add(new SerializedIntGridLayer(otherIntGrid));
                terrain.renderingData.material = _material;

                var target = new MosaicPaintingTarget(terrain, _intGrid, 0);
                Assert.IsTrue(target.SetCells(new[] { new Vector2Int(3, 7), new Vector2Int(-1, 2) }, 1));

                Assert.AreEqual(2, terrain.PaintedLayers[0].Cells.Count);
                Assert.IsEmpty(terrain.PaintedLayers[1].Cells);
            }
            finally
            {
                Object.DestroyImmediate(otherIntGrid);
            }
        }

        [Test]
        public void PaintingStroke_LargeBatchAppendsImmediately()
        {
            const int EXISTING_CELL_COUNT = 20000;
            const int BATCH_CELL_COUNT = 5000;
            var tilemap = CreateTilemap("Large Batch Tilemap").GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);
            var existing = new List<Vector2Int>(EXISTING_CELL_COUNT);
            for (var i = 0; i < EXISTING_CELL_COUNT; i++) existing.Add(new Vector2Int(i, 0));
            Assert.IsTrue(target.SetCells(existing, 1));

            var positions = new List<Vector2Int>(BATCH_CELL_COUNT);
            for (var i = 0; i < BATCH_CELL_COUNT; i++) positions.Add(new Vector2Int(-i - 1, 1));

            using var stroke = target.BeginStroke(1);
            Assert.IsTrue(stroke.SetCells(positions));
            Assert.AreEqual(BATCH_CELL_COUNT, stroke.ChangedCells.Count);
            Assert.AreEqual(EXISTING_CELL_COUNT + BATCH_CELL_COUNT, tilemap.PaintedCells.Count);
            Assert.AreEqual(positions[0], tilemap.PaintedCells[EXISTING_CELL_COUNT].Position);
        }

        [Test]
        public void PaintingStroke_PrefabInstanceRecordsCellOverrides()
        {
            var prefabRoot = new GameObject("Painting Prefab", typeof(GridAuthoring));
            var tilemapObject = new GameObject("Tilemap", typeof(TilemapAuthoring));
            tilemapObject.transform.SetParent(prefabRoot.transform);
            var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/MosaicPaintingStrokeTests.prefab");
            _temporaryAssets.Add(prefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.SetParent(_gridObject.transform);
                var tilemap = instance.GetComponentInChildren<TilemapAuthoring>();
                tilemap.intGrid = _intGrid;
                tilemap.renderingData.material = _material;

                Assert.IsTrue(new MosaicPaintingTarget(tilemap).SetCell(new Vector2Int(4, 6), 1));

                var modifications = PrefabUtility.GetPropertyModifications(tilemap);
                Assert.IsNotNull(modifications);
                Assert.IsTrue(modifications.Any(modification =>
                    modification.propertyPath.StartsWith("_paintedData")));
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void PaintingTarget_EntityTargetIsReadOnly()
        {
            var target = CreateEntityPaintingTarget(float3.zero);

            Assert.IsTrue(target.IsValid);
            Assert.IsFalse(target.IsPaintable);
            Assert.IsFalse(target.SetCell(Vector2Int.zero, 1));
            Assert.AreEqual(0, target.Cells.Count);
        }

        [Test]
        public void Initialization_SameBakedSourceIgnoresBakeNamespace()
        {
            var grid = _gridObject.GetComponent<GridAuthoring>();
            var other = CreateTilemap("Different Authoring Source").GetComponent<TilemapAuthoring>();
            var objectId = _gridObject.GetEntityId();
            var componentId = grid.GetEntityId();
            var first = new EntityGuid(objectId, componentId, 1u, 0u);
            var secondBake = new EntityGuid(objectId, componentId, 2u, 0u);
            var differentSubEntity = new EntityGuid(objectId, componentId, 2u, 1u);
            var differentSource = new EntityGuid(other.gameObject.GetEntityId(), other.GetEntityId(), 2u, 0u);

            Assert.IsTrue(MosaicInitializationSystem.IsSameBakedSource(first, secondBake));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, differentSubEntity));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, differentSource));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, EntityGuid.Null));
        }

        [Test]
        public void Initialization_DeadSceneOwnerIsStale()
        {
            var sceneEntity = _world.EntityManager.CreateEntity();
            var target = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddSharedComponent(target, new SceneTag { SceneEntity = sceneEntity });

            Assert.IsFalse(MosaicInitializationSystem.IsStaleSceneEntity(_world.EntityManager, target));

            _world.EntityManager.DestroyEntity(sceneEntity);

            Assert.IsTrue(MosaicInitializationSystem.IsStaleSceneEntity(_world.EntityManager, target));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PaintingWindow_DisabledAuthoringTargetIsNotDiscovered(bool terrain, bool disableGameObject)
        {
            MonoBehaviour authoring;
            MosaicPaintingTarget target;
            if (terrain)
            {
                var terrainObject = new GameObject("Disabled Terrain", typeof(TilemapTerrainAuthoring));
                terrainObject.transform.SetParent(_gridObject.transform);
                var terrainAuthoring = terrainObject.GetComponent<TilemapTerrainAuthoring>();
                terrainAuthoring.intGridLayers.Add(_intGrid);
                terrainAuthoring.renderingData.material = _material;
                authoring = terrainAuthoring;
                target = new MosaicPaintingTarget(terrainAuthoring, _intGrid, 0);
            }
            else
            {
                authoring = CreateTilemap("Disabled Tilemap").GetComponent<TilemapAuthoring>();
                target = new MosaicPaintingTarget((TilemapAuthoring)authoring);
            }

            Assert.IsTrue(target.IsPaintable);
            if (disableGameObject) authoring.gameObject.SetActive(false);
            else authoring.enabled = false;
            Assert.IsFalse(target.IsPaintable);

            var targets = new List<MosaicPaintingTarget>();
            targets.AddRange(MosaicPaintingCatalog.DiscoverAuthoringCandidates(
                StageUtility.GetCurrentStageHandle()));

            Assert.IsFalse(targets.Any(target => ReferenceEquals(target.Owner, authoring)));
        }

        [Test]
        public void PaintingTarget_SourceIdentityDistinguishesDifferentAuthoringObjects()
        {
            var first = CreateTilemap("First Tilemap").GetComponent<TilemapAuthoring>();
            var second = CreateTilemap("Second Tilemap").GetComponent<TilemapAuthoring>();

            Assert.AreNotEqual(new MosaicPaintingTarget(first).Id, new MosaicPaintingTarget(second).Id);
        }

        [Test]
        public void PaintingController_OpenedSubSceneWaitsForPublishedRuntimeBinding()
        {
            var tilemap = CreateTilemap("Opened Tilemap").GetComponent<TilemapAuthoring>();
            var openedTarget = new MosaicPaintingTarget(tilemap);
            var entity = _world.EntityManager.CreateEntity();
            var intGridHash = new Unity.Entities.Hash128(51u, 0u, 0u, 0u);
            var rendererHash = new Unity.Entities.Hash128(52u, 0u, 0u, 0u);
            var binding = new MosaicPaintingRuntimeBinding(
                _world, entity, entity, intGridHash, rendererHash);
            var boundTarget = new MosaicPaintingTarget(tilemap, binding);

            Assert.IsFalse(MosaicPaintingController.AreOpenSubSceneTargetsReady(
                new[] { openedTarget }, System.Array.Empty<MosaicPaintingTarget>()));
            Assert.IsFalse(MosaicPaintingController.AreOpenSubSceneTargetsReady(
                new[] { openedTarget }, new[] { openedTarget }),
                "An authoring candidate without its runtime binding must keep the transition pending.");
            Assert.IsTrue(MosaicPaintingController.AreOpenSubSceneTargetsReady(
                new[] { openedTarget }, new[] { boundTarget }));
        }

        [Test]
        public void PaintingController_RecognizesSubSceneBeforeUnityMarksOpenedScene()
        {
            const string SUB_SCENE_PATH = "Assets/Scenes/Gameplay/Gameplay_Settings.unity";

            Assert.IsTrue(MosaicPaintingController.IsSubSceneAssetPath(SUB_SCENE_PATH, SUB_SCENE_PATH),
                "sceneOpened must recognize the asset path before Unity marks Scene.isSubScene.");
            Assert.IsFalse(MosaicPaintingController.IsSubSceneAssetPath(
                "Assets/Scenes/Gameplay.unity", SUB_SCENE_PATH));
        }

        [Test]
        public void PaintingController_RawVisibilityIncludesOutgoingSubSceneRenderers()
        {
            var sceneGuid = new Unity.Entities.Hash128(61u, 0u, 0u, 0u);
            var current = CreateEntityPaintingTarget(float3.zero);
            _world.EntityManager.AddSharedComponent(current.RuntimeBinding.RendererEntity,
                new SceneSection { SceneGUID = sceneGuid });
            var outgoing = CreateEntityPaintingTarget(new float3(1f),
                new Unity.Entities.Hash128(62u, 0u, 0u, 0u));
            _world.EntityManager.AddSharedComponent(outgoing.RuntimeBinding.RendererEntity,
                new SceneSection { SceneGUID = sceneGuid });
            var unrelated = CreateEntityPaintingTarget(new float3(2f),
                new Unity.Entities.Hash128(63u, 0u, 0u, 0u));
            _world.EntityManager.AddSharedComponent(unrelated.RuntimeBinding.RendererEntity,
                new SceneSection { SceneGUID = new Unity.Entities.Hash128(64u, 0u, 0u, 0u) });
            var visibilityTargets = new HashSet<MosaicPaintingVisibilityTarget>();

            MosaicPaintingController.AddSubSceneRendererVisibilityTargets(
                _world, new[] { current }, visibilityTargets);

            var renderers = visibilityTargets.Select(target => target.Binding.RendererEntity).ToArray();
            CollectionAssert.Contains(renderers, current.RuntimeBinding.RendererEntity);
            CollectionAssert.Contains(renderers, outgoing.RuntimeBinding.RendererEntity);
            CollectionAssert.DoesNotContain(renderers, unrelated.RuntimeBinding.RendererEntity);
        }

        [Test]
        public void PaintingCatalog_PrimaryEntityBindingUsesGameObjectIdentity()
        {
            var tilemap = CreateTilemap("Shared Primary Entity").GetComponent<TilemapAuthoring>();
            var candidate = new MosaicPaintingTarget(tilemap);
            var entity = _world.EntityManager.CreateEntity();
            var hash = new Unity.Entities.Hash128(41u, 0u, 0u, 0u);
            var expected = new MosaicPaintingRuntimeBinding(_world, entity, entity, hash, hash);
            var primaryEntityId = tilemap.GetComponentInParent<GridAuthoring>().GetEntityId();
            var bindings = new Dictionary<MosaicPaintingTargetId, MosaicPaintingRuntimeBinding>
            {
                [new MosaicPaintingTargetId(tilemap.gameObject.GetEntityId(), primaryEntityId, 0)] = expected,
            };

            Assert.AreNotEqual(tilemap.GetEntityId(), primaryEntityId);
            Assert.IsTrue(MosaicPaintingCatalog.TryGetBinding(candidate, bindings, out var binding));
            Assert.AreEqual(expected.IntGridEntity, binding.IntGridEntity);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaintingCatalog_ClosedSubSceneTargetWithoutEntityGuidIsReadOnly(bool terrain)
        {
            var entityManager = _world.EntityManager;
            var sceneGuid = new Unity.Entities.Hash128(42u, 0u, 0u, 0u);
            var intGridHash = new Unity.Entities.Hash128(43u, 0u, 0u, 0u);
            var rendererHash = new Unity.Entities.Hash128(44u, 0u, 0u, 0u);
            var intGridEntity = entityManager.CreateEntity();
            var rendererEntity = terrain ? entityManager.CreateEntity() : intGridEntity;
            var intGridData = new IntGridData { Hash = intGridHash, DebugName = "Closed IntGrid" };
            entityManager.AddComponentData(intGridEntity, intGridData);
            entityManager.AddComponentData(intGridEntity, new TilemapTransform { CellSize = 1f });
            entityManager.AddBuffer<IntGridValueElement>(intGridEntity).Add(new IntGridValueElement
            {
                Value = 1,
                Name = "Solid",
                Color = Color.red,
            });

            entityManager.AddComponentData(rendererEntity, new TilemapRendererData { MeshHash = rendererHash });
            entityManager.AddComponentData(rendererEntity, new LocalToWorld { Value = float4x4.identity });
            entityManager.AddComponent<MosaicRendererInitialized>(rendererEntity);
            entityManager.AddSharedComponent(rendererEntity, new SceneSection { SceneGUID = sceneGuid });
            if (terrain)
            {
                entityManager.AddComponent<FireAlt.Mosaic.Data.TerrainData>(rendererEntity);
                entityManager.AddBuffer<TilemapTerrainLayerElement>(rendererEntity)
                    .Add(new TilemapTerrainLayerElement { IntGridEntity = intGridEntity });
            }

            var layers = new NativeHashMap<Unity.Entities.Hash128, TilemapIntGridSingleton.IntGridLayer>(
                1, Allocator.Persistent);
            layers.Add(intGridHash, new TilemapIntGridSingleton.IntGridLayer(
                1, Allocator.Persistent, intGridData, terrain, intGridEntity));
            entityManager.CreateSingleton(new TilemapIntGridSingleton { IntGridLayers = layers });

            try
            {
                var targets = new List<MosaicPaintingTarget>();
                MosaicPaintingCatalog.DiscoverTargets(targets, System.Array.Empty<MosaicPaintingTarget>(),
                    _world, false, new HashSet<Unity.Entities.Hash128>());
                Assert.IsEmpty(targets, "A Prefab-stage scope must not admit unrelated closed SubScene entities.");

                MosaicPaintingCatalog.DiscoverTargets(targets, System.Array.Empty<MosaicPaintingTarget>(),
                    _world, true, new HashSet<Unity.Entities.Hash128>());

                Assert.AreEqual(1, targets.Count);
                Assert.IsTrue(targets[0].IsValid);
                Assert.IsTrue(targets[0].IsEntityTarget);
                Assert.IsFalse(targets[0].IsPaintable);
                Assert.AreEqual(terrain, targets[0].IsTerrain);
                Assert.AreEqual(1, targets[0].Values.Count);
                Assert.AreEqual(default(EntityId), targets[0].SourceId);
                Assert.IsTrue(MosaicPaintingWindow.HasValidTarget(targets),
                    "A ready closed-SubScene target must keep the Mosaic tool available.");

                var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
                try
                {
                    window.CreateGUI();
                    var targetsField = typeof(MosaicPaintingWindow).GetField("_targets",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var buildPalette = typeof(MosaicPaintingWindow).GetMethod("BuildPalette",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(targetsField);
                    Assert.IsNotNull(buildPalette);
                    var windowTargets = (List<MosaicPaintingTarget>)targetsField.GetValue(window);
                    windowTargets.Clear();
                    windowTargets.Add(targets[0]);
                    buildPalette.Invoke(window, null);
                    Assert.IsNull(window.rootVisualElement.Q<Button>(className: "mosaic-paint-value"),
                        "Closed-SubScene runtime targets must not be published as selectable paint values.");
                }
                finally
                {
                    Object.DestroyImmediate(window);
                }
            }
            finally
            {
                var layer = layers[intGridHash];
                layer.Dispose();
                layers.Dispose();
            }
        }

        [Test]
        public void PaintingPreview_ContextPrefabHidesOnlyMatchingSubSceneRenderer()
        {
            var rendererHash = new Unity.Entities.Hash128(4u, 0u, 0u, 0u);
            var entityManager = _world.EntityManager;
            var originatingEntityId = _gridObject.GetEntityId();
            var subSceneRenderer = entityManager.CreateEntity();
            entityManager.AddComponentData(subSceneRenderer, new TilemapRendererData { MeshHash = rendererHash });
            entityManager.AddComponentData(subSceneRenderer, new IntGridData { Hash = rendererHash });
            entityManager.AddComponentData(subSceneRenderer,
                new EntityGuid(originatingEntityId, default, 0u, 0u));
            entityManager.AddSharedComponent(subSceneRenderer, new SceneSection
            {
                SceneGUID = new Unity.Entities.Hash128(5u, 0u, 0u, 0u),
            });
            InternalEditorRenderData.SetSceneCullingMask(entityManager, subSceneRenderer, 11);

            var prefabRenderer = entityManager.CreateEntity();
            entityManager.AddComponentData(prefabRenderer, new TilemapRendererData { MeshHash = rendererHash });
            InternalEditorRenderData.SetSceneCullingMask(entityManager, prefabRenderer, 22);

            var preview = new MosaicPaintingPreview();
            var noTargets = new List<MosaicPaintingVisibilityTarget>();
            var binding = new MosaicPaintingRuntimeBinding(
                _world, subSceneRenderer, subSceneRenderer, rendererHash, rendererHash);
            var contextTargets = new List<MosaicPaintingVisibilityTarget>
            {
                new(binding, originatingEntityId),
            };

            preview.SetVisibility(_world, noTargets, contextTargets, false);

            Assert.AreEqual(0,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, subSceneRenderer));
            Assert.AreEqual(22,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, prefabRenderer));

            preview.SetVisibility(_world, noTargets, new List<MosaicPaintingVisibilityTarget>(), false);

            Assert.AreEqual(11,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, subSceneRenderer));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaintingWindow_RawCellsSortBackToFront(bool orthographic)
        {
            var far = new MosaicPaintingTarget(CreateTilemap("Far").GetComponent<TilemapAuthoring>());
            var near = new MosaicPaintingTarget(CreateTilemap("Near").GetComponent<TilemapAuthoring>());
            far.Owner.transform.position = Vector3.zero;
            near.Owner.transform.position = new Vector3(10f, 2f, 0f);

            var cameraObject = new GameObject("Sorting Camera", typeof(Camera));
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = orthographic;
                camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));

                var cameraPosition = camera.transform.position;
                var cameraForward = camera.transform.forward;
                var farCell = new MosaicPaintingWindow.RawCell(
                    far, new SerializedIntGridCell(Vector2Int.zero, 1), cameraPosition, cameraForward, 0);
                var nearCell = new MosaicPaintingWindow.RawCell(
                    near, new SerializedIntGridCell(new Vector2Int(-10, 0), 1), cameraPosition, cameraForward, 1);

                Assert.Less(MosaicPaintingWindow.CompareRawCells(farCell, nearCell), 0);
                Assert.Greater(MosaicPaintingWindow.CompareRawCells(nearCell, farCell), 0);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
