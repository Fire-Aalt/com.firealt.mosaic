using System;
using System.Collections.Generic;
using System.Linq;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using FireAlt.Mosaic.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FireAlt.Mosaic.Tests
{
    public sealed class LinkedTilemapLayersTests
    {
        public enum InvalidConfiguration
        {
            Empty,
            Null,
            Duplicate,
            Undefined,
        }

        private readonly List<IntGridDefinition> _intGrids = new();
        private readonly List<Texture2D> _textures = new();
        private GameObject _gridObject;
        private IntGridDefinition _intGrid;
        private Material _material;

        [SetUp]
        public void SetUp()
        {
            _gridObject = new GameObject("Linked Test Grid", typeof(GridAuthoring));
            _intGrid = CreateIntGrid("Linked Test IntGrid", Color.red, Color.blue);
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            _material = new Material(shader);
        }

        [TearDown]
        public void TearDown()
        {
            MosaicPaintingSession.BrushSize = MosaicPaintingSession.MIN_BRUSH_SIZE;
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool)) ToolManager.RestorePreviousPersistentTool();
            Object.DestroyImmediate(_material);
            foreach (var texture in _textures) Object.DestroyImmediate(texture);
            foreach (var intGrid in _intGrids) Object.DestroyImmediate(intGrid);
            Object.DestroyImmediate(_gridObject);
        }

        [Test]
        public void LinkedLayer_FirstOperationAnchorsAndReceivesItsConfiguredValue()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var linked = CreateLinked("Linked", (first, 1), (second, 2));
            var selection = CreateSelection(linked);
            var position = new Vector2Int(4, 7);

            Assert.AreSame(first, selection.Anchor.Owner);
            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke) Assert.IsTrue(stroke.SetCells(new[] { position }));

            AssertCell(first, position, 1);
            AssertCell(second, position, 2);

            linked.layers[0].Operations.Reverse();
            var reordered = CreateSelection(linked);
            Assert.IsFalse(selection.IsValid, "The stale operation order must be rejected before another stroke.");
            Assert.AreSame(second, reordered.Anchor.Owner);
        }

        [Test]
        public void LinkedLayer_LmbAppliesDifferentValuesIncludingConfiguredRemoval()
        {
            var painted = CreateTilemap("Painted");
            var removed = CreateTilemap("Removed");
            var position = new Vector2Int(2, 3);
            Assert.IsTrue(new MosaicPaintingTarget(removed).SetCell(position, 1));
            var linked = CreateLinked("Linked", (painted, 2), (removed, 0));
            var selection = CreateSelection(linked);

            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke) Assert.IsTrue(stroke.SetCells(new[] { position }));

            AssertCell(painted, position, 2);
            Assert.IsEmpty(removed.PaintedCells);
        }

        [Test]
        public void LinkedLayer_RmbClearsEveryOperationTarget()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var position = new Vector2Int(-2, 5);
            Assert.IsTrue(new MosaicPaintingTarget(first).SetCell(position, 1));
            Assert.IsTrue(new MosaicPaintingTarget(second).SetCell(position, 2));
            var selection = CreateSelection(CreateLinked("Linked", (first, 1), (second, 2)));

            Assert.IsTrue(selection.TryBeginStroke(true, out var stroke));
            using (stroke) Assert.IsTrue(stroke.SetCells(new[] { position }));

            Assert.IsEmpty(first.PaintedCells);
            Assert.IsEmpty(second.PaintedCells);
        }

        [Test]
        public void LinkedLayer_DragReusesEveryTargetStrokeAcrossSegments()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var selection = CreateSelection(CreateLinked("Linked", (first, 1), (second, 2)));
            var start = new Vector2Int(0, 0);
            var middle = new Vector2Int(1, 0);
            var end = new Vector2Int(2, 0);

            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke)
            {
                Assert.IsTrue(stroke.SetCells(new[] { start }));
                Assert.IsTrue(stroke.SetCells(new[] { middle, end }));
            }

            CollectionAssert.AreEqual(new[] { start, middle, end },
                first.PaintedCells.Select(cell => cell.Position).ToArray());
            CollectionAssert.AreEqual(new[] { start, middle, end },
                second.PaintedCells.Select(cell => cell.Position).ToArray());
        }

        [Test]
        public void LinkedLayer_OneUndoRedoRestoresEveryTarget()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var position = new Vector2Int(8, 9);
            var selection = CreateSelection(CreateLinked("Linked", (first, 1), (second, 2)));

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Paint Linked Mosaic Layers");
            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke) Assert.IsTrue(stroke.SetCells(new[] { position }));
            Undo.CollapseUndoOperations(undoGroup);

            AssertCell(first, position, 1);
            AssertCell(second, position, 2);
            Undo.PerformUndo();
            Assert.IsEmpty(first.PaintedCells);
            Assert.IsEmpty(second.PaintedCells);
            Undo.PerformRedo();
            AssertCell(first, position, 1);
            AssertCell(second, position, 2);
        }

        [TestCase(InvalidConfiguration.Empty)]
        [TestCase(InvalidConfiguration.Null)]
        [TestCase(InvalidConfiguration.Duplicate)]
        [TestCase(InvalidConfiguration.Undefined)]
        public void LinkedLayer_InvalidConfigurationBlocksEntireLayer(InvalidConfiguration configuration)
        {
            var target = CreateTilemap("Target");
            var linked = CreateLinked("Linked");
            var operations = linked.layers[0].Operations;
            switch (configuration)
            {
                case InvalidConfiguration.Empty:
                    break;
                case InvalidConfiguration.Null:
                    operations.Add(new LayerOperation());
                    break;
                case InvalidConfiguration.Duplicate:
                    operations.Add(new LayerOperation { target = target, valueToSet = 1 });
                    operations.Add(new LayerOperation { target = target, valueToSet = 2 });
                    break;
                case InvalidConfiguration.Undefined:
                    operations.Add(new LayerOperation { target = target, valueToSet = 3 });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration, null);
            }

            var selection = CreateSelection(linked);
            Assert.IsFalse(selection.IsValid);
            Assert.IsNotEmpty(selection.ValidationMessage);
            Assert.IsFalse(selection.TryBeginStroke(false, out _));
            Assert.IsEmpty(target.PaintedCells);
        }

        [Test]
        public void LinkedLayer_OutOfStageTargetBlocksEntireLayer()
        {
            var previewScene = EditorSceneManager.NewPreviewScene();
            var previewGrid = new GameObject("Preview Grid", typeof(GridAuthoring));
            try
            {
                SceneManager.MoveGameObjectToScene(previewGrid, previewScene);
                var targetObject = new GameObject("Preview Target", typeof(TilemapAuthoring));
                targetObject.transform.SetParent(previewGrid.transform);
                var target = targetObject.GetComponent<TilemapAuthoring>();
                target.intGrid = _intGrid;
                target.renderingData.material = _material;
                var linked = CreateLinked("Linked", (target, 1));
                var stage = StageUtility.GetCurrentStageHandle();
                Assert.AreNotEqual(stage, StageUtility.GetStageHandle(target.gameObject));

                var targets = new Dictionary<TilemapAuthoring, MosaicPaintingTarget>
                {
                    [target] = new MosaicPaintingTarget(target),
                };
                var selection = MosaicPaintingSelection.Create(linked, 0, targets, stage);

                Assert.IsFalse(selection.IsValid);
                StringAssert.Contains("current stage", selection.ValidationMessage);
                Assert.IsFalse(selection.TryBeginStroke(false, out _));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        [Test]
        public void LinkedLayer_IncrementalEventsReportChangedTargetsAndAppliedValues()
        {
            var painted = CreateTilemap("Painted");
            var removed = CreateTilemap("Removed");
            var position = new Vector2Int(1, -4);
            Assert.IsTrue(new MosaicPaintingTarget(removed).SetCell(position, 1));
            var selection = CreateSelection(CreateLinked("Linked", (painted, 2), (removed, 0)));
            var changes = new List<(MosaicPaintingTarget Target, short Value)>();
            void OnCellsChanged(MosaicPaintingTarget target, IReadOnlyCollection<Vector2Int> positions, short value)
            {
                Assert.Contains(position, positions.ToList());
                changes.Add((target, value));
            }

            MosaicPaintingSession.CellsChanged += OnCellsChanged;
            try
            {
                Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
                using (stroke)
                {
                    Assert.IsTrue(stroke.SetCells(new[] { position }));
                    Assert.IsFalse(stroke.SetCells(new[] { position }));
                }
            }
            finally
            {
                MosaicPaintingSession.CellsChanged -= OnCellsChanged;
            }

            Assert.AreEqual(2, changes.Count);
            Assert.AreSame(painted, changes.Single(change => change.Value == 2).Target.Owner);
            Assert.AreSame(removed, changes.Single(change => change.Value == 0).Target.Owner);
        }

        [Test]
        public void LinkedLayer_IdentityAndSelectionSurviveRefreshWhileValid()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var linked = CreateLinked("Linked", (first, 1), (second, 2));
            var original = CreateSelection(linked);
            var refreshed = CreateSelection(linked);

            MosaicPaintingSession.Select(original);
            Assert.AreSame(original, MosaicPaintingSession.Selection);
            Assert.AreEqual(original.Id, refreshed.Id);
            MosaicPaintingSession.Select(refreshed);
            Assert.AreSame(refreshed, MosaicPaintingSession.Selection);
            Assert.AreSame(first, MosaicPaintingSession.Target.Owner);
        }

        [Test]
        public void PaintingWindow_GroupsTerrainAndPresentsLinkedButtonsAlphabetically()
        {
            var terrainObject = new GameObject("Terrain Owner", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            terrain.intGridLayers.Add(CreateIntGrid("Second Terrain Layer", Color.green));

            var tilemap = CreateTilemap("Linked Target");
            var icon = new Texture2D(1, 1);
            _textures.Add(icon);
            var zulu = CreateLinked("Zulu Links", (tilemap, 1));
            zulu.layers[0].name = string.Empty;
            zulu.layers[0].color = new Color(0f, 1f, 1f, 0f);
            var alpha = CreateLinked("Alpha Links", (tilemap, 2));
            alpha.layers[0].name = "Linked Paint";
            alpha.layers[0].icon = icon;

            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var foldouts = window.rootVisualElement.Query<Foldout>().ToList();
                var terrainFoldout = foldouts.Single(foldout => ReferenceEquals(foldout.userData, terrain));
                Assert.AreEqual(2, terrainFoldout.Query<Foldout>().ToList()
                    .Count(foldout => foldout.userData is MosaicPaintingTarget));

                var linkedFoldouts = foldouts.Where(foldout => foldout.ClassListContains("mosaic-paint-linked"))
                    .ToList();
                Assert.AreEqual(new[] { "Alpha Links", "Zulu Links" },
                    linkedFoldouts.Select(foldout => foldout.text).ToArray());

                var alphaButton = linkedFoldouts[0].Query<Button>(className: "mosaic-paint-value").First();
                Assert.AreEqual("Linked Paint", alphaButton.Q<Label>().text);
                Assert.AreSame(icon, alphaButton.Q<Image>().image);

                var zuluButton = linkedFoldouts[1].Query<Button>(className: "mosaic-paint-value").First();
                Assert.AreEqual("Layer 1", zuluButton.Q<Label>().text);
                Assert.AreEqual(Color.cyan, zuluButton.Q<Image>().style.backgroundColor.value);
                Assert.AreEqual(1f, zuluButton.style.backgroundColor.value.a);
                Assert.AreEqual(Color.cyan, zuluButton
                    .Q<VisualElement>(className: "mosaic-paint-value__accent").style.backgroundColor.value);

                MosaicPaintingSession.BrushSize = 3;
                Assert.AreEqual(3, MosaicPaintingSession.BrushSize);
                Assert.AreEqual(2, MosaicPaintingSession.BrushRadius);
                Assert.IsTrue(MosaicPaintingTool.IsWithinBrushRadius(2, 0));
                Assert.IsFalse(MosaicPaintingTool.IsWithinBrushRadius(2, 2));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }
        
        [Test]
        public void IntGridColorFields_DisableAlphaPicking()
        {
            var linkedColor = typeof(LinkedLayer).GetField(nameof(LinkedLayer.color))
                ?.GetCustomAttributes(typeof(ColorUsageAttribute), false).Cast<ColorUsageAttribute>().Single();
            var valueColor = typeof(IntGridValueDefinition).GetField(nameof(IntGridValueDefinition.color))
                ?.GetCustomAttributes(typeof(ColorUsageAttribute), false).Cast<ColorUsageAttribute>().Single();

            Assert.IsFalse(linkedColor.showAlpha);
            Assert.IsFalse(valueColor.showAlpha);
        }

        private IntGridDefinition CreateIntGrid(string name, params Color[] colors)
        {
            var intGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            intGrid.name = name;
            for (var i = 0; i < colors.Length; i++)
            {
                intGrid.intGridValues.Add(new IntGridValueDefinition
                {
                    value = (short)(i + 1),
                    name = $"Value {i + 1}",
                    color = colors[i],
                });
            }

            _intGrids.Add(intGrid);
            return intGrid;
        }

        private TilemapAuthoring CreateTilemap(string name)
        {
            var tilemapObject = new GameObject(name, typeof(TilemapAuthoring));
            tilemapObject.transform.SetParent(_gridObject.transform);
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.intGrid = _intGrid;
            tilemap.renderingData.material = _material;
            return tilemap;
        }

        private LinkedTilemapLayers CreateLinked(string name,
            params (TilemapAuthoring Target, int Value)[] operations)
        {
            var linkedObject = new GameObject(name, typeof(LinkedTilemapLayers));
            linkedObject.transform.SetParent(_gridObject.transform);
            var linked = linkedObject.GetComponent<LinkedTilemapLayers>();
            var layer = new LinkedLayer();
            foreach (var operation in operations)
            {
                layer.Operations.Add(new LayerOperation
                {
                    target = operation.Target,
                    valueToSet = (short)operation.Value,
                });
            }

            linked.layers.Add(layer);
            return linked;
        }

        private MosaicPaintingSelection CreateSelection(LinkedTilemapLayers linked)
        {
            var targets = linked.layers[0].Operations
                .Where(operation => operation?.target != null)
                .Select(operation => operation.target)
                .Distinct()
                .ToDictionary(tilemap => tilemap, tilemap => new MosaicPaintingTarget(tilemap));
            return MosaicPaintingSelection.Create(linked, 0, targets, StageUtility.GetCurrentStageHandle());
        }

        private static void AssertCell(TilemapAuthoring target, Vector2Int position, short value)
        {
            Assert.AreEqual(1, target.PaintedCells.Count);
            Assert.AreEqual(position, target.PaintedCells[0].Position);
            Assert.AreEqual(value, target.PaintedCells[0].Value);
        }
    }
}
