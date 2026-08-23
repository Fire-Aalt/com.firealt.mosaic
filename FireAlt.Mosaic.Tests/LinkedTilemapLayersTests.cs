using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using EntityHash128 = Unity.Entities.Hash128;
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
        private string _prefabPath;

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
            MosaicPaintingController.BrushSize = MosaicPaintingController.MIN_BRUSH_SIZE;
            MosaicPaintingController.ClearSelection();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool)) ToolManager.RestorePreviousPersistentTool();
            if (_prefabPath == null)
            {
                Object.DestroyImmediate(_gridObject);
            }
            else
            {
                PrefabStageUtility.GetCurrentPrefabStage()?.ClearDirtiness();
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(_prefabPath);
            }

            Object.DestroyImmediate(_material);
            foreach (var texture in _textures) Object.DestroyImmediate(texture);
            foreach (var intGrid in _intGrids) Object.DestroyImmediate(intGrid);
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
        public void LinkedLayer_RectangleFillAndClearUsesOneCellBatch()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var selection = CreateSelection(CreateLinked("Linked", (first, 1), (second, 2)));
            var cells = new HashSet<Vector2Int>();
            Assert.IsTrue(MosaicPaintingTool.TryAddRectangleCells(
                new Vector2Int(-1, 2), new Vector2Int(1, 3), cells));

            Assert.IsTrue(selection.TryBeginStroke(false, out var fillStroke));
            using (fillStroke) Assert.IsTrue(fillStroke.SetCells(cells));
            AssertCells(first, 1, cells.ToArray());
            AssertCells(second, 2, cells.ToArray());

            Assert.IsTrue(selection.TryBeginStroke(true, out var clearStroke));
            using (clearStroke) Assert.IsTrue(clearStroke.SetCells(cells));
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
            var start = new Vector2Int(8, 9);
            var end = new Vector2Int(9, 9);
            var selection = CreateSelection(CreateLinked("Linked", (first, 1), (second, 2)));

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Paint Linked Mosaic Layers");
            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke)
            {
                Assert.IsTrue(stroke.SetCells(new[] { start }));
                Assert.IsTrue(stroke.SetCells(new[] { start, end }));
            }
            Undo.CollapseUndoOperations(undoGroup);

            AssertCells(first, 1, start, end);
            AssertCells(second, 2, start, end);
            Undo.PerformUndo();
            Assert.IsEmpty(first.PaintedCells);
            Assert.IsEmpty(second.PaintedCells);
            Undo.PerformRedo();
            AssertCells(first, 1, start, end);
            AssertCells(second, 2, start, end);
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
        public void LinkedLayer_IncrementalStrokeUpdatesOnlyChangedCells()
        {
            var painted = CreateTilemap("Painted");
            var removed = CreateTilemap("Removed");
            var position = new Vector2Int(1, -4);
            Assert.IsTrue(new MosaicPaintingTarget(removed).SetCell(position, 1));
            var selection = CreateSelection(CreateLinked("Linked", (painted, 2), (removed, 0)));
            Assert.IsTrue(selection.TryBeginStroke(false, out var stroke));
            using (stroke)
            {
                Assert.IsTrue(stroke.SetCells(new[] { position }));
                Assert.IsFalse(stroke.SetCells(new[] { position }));
            }

            AssertCell(painted, position, 2);
            Assert.IsEmpty(removed.PaintedCells);
        }

        [Test]
        public void LinkedLayer_IdentityAndSelectionSurviveRefreshWhileValid()
        {
            var first = CreateTilemap("First");
            var second = CreateTilemap("Second");
            var linked = CreateLinked("Linked", (first, 1), (second, 2));
            var original = CreateSelection(linked);
            var refreshed = CreateSelection(linked);

            MosaicPaintingController.Select(original);
            Assert.AreSame(original, MosaicPaintingController.Selection);
            Assert.AreEqual(original.Id, refreshed.Id);
            MosaicPaintingController.Select(refreshed);
            Assert.AreSame(refreshed, MosaicPaintingController.Selection);
            Assert.AreSame(first, MosaicPaintingController.Target.Owner);
        }

        [Test]
        public void PaintingWindow_SelectionChangeKeepsExistingButtonForTransition()
        {
            var tilemap = CreateTilemap("Animated Selection");
            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var targets = GetWindowList<MosaicPaintingTarget>(window, "_targets");
                targets.Clear();
                targets.Add(new MosaicPaintingTarget(tilemap));
                GetWindowList<LinkedTilemapLayers>(window, "_linkedComponents").Clear();
                InvokeBuildPalette(window);

                var button = window.rootVisualElement.Q<Button>(className: "mosaic-paint-value");
                var selection = GetWindowList<MosaicPaintingSelection>(window, "_selections").First();
                var refreshQueued = typeof(MosaicPaintingWindow).GetField("_refreshQueued",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(button);
                Assert.IsNotNull(refreshQueued);
                refreshQueued.SetValue(window, false);

                MosaicPaintingController.Select(selection);

                Assert.AreSame(button, window.rootVisualElement.Q<Button>(className: "mosaic-paint-value"));
                Assert.IsTrue(button.ClassListContains("mosaic-paint-value--selected"));
                Assert.IsFalse((bool)refreshQueued.GetValue(window),
                    "Selection must update the existing button instead of rebuilding the palette.");
            }
            finally
            {
                MosaicPaintingTool.ExitPainting();
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void PaintingWindow_EscapeHandlerClearsSelection()
        {
            var tilemap = CreateTilemap("Escape Selection");
            var selection = MosaicPaintingSelection.Create(new MosaicPaintingTarget(tilemap),
                _intGrid.intGridValues[0], StageUtility.GetCurrentStageHandle());
            MosaicPaintingController.Select(selection);
            Assert.IsTrue(MosaicPaintingController.IsPainting);

            using var evt = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None);
            MosaicPaintingWindow.ExitPainting(evt);

            Assert.IsFalse(MosaicPaintingController.IsPainting);
            Assert.IsNull(MosaicPaintingController.Selection);
            Assert.IsFalse(MosaicPaintingController.SelectedId.HasValue);
        }

        [Test]
        public void PaintingWindow_GroupsTerrainAndPresentsLinkedButtonsAlphabetically()
        {
            EnterPrefabIsolation();
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
                var targets = GetWindowList<MosaicPaintingTarget>(window, "_targets");
                targets.Clear();
                targets.Add(new MosaicPaintingTarget(terrain, terrain.intGridLayers[0], 0));
                targets.Add(new MosaicPaintingTarget(terrain, terrain.intGridLayers[1], 1));
                targets.Add(new MosaicPaintingTarget(tilemap));
                var linkedComponents = GetWindowList<LinkedTilemapLayers>(window, "_linkedComponents");
                linkedComponents.Clear();
                linkedComponents.Add(alpha);
                linkedComponents.Add(zulu);
                InvokeBuildPalette(window);
                var foldouts = window.rootVisualElement.Query<Foldout>().ToList();
                var terrainFoldout = foldouts.Single(foldout => ReferenceEquals(foldout.userData, terrain));
                Assert.IsTrue(terrainFoldout.ClassListContains("mosaic-paint-group--top-level"));
                var terrainLayerFoldouts = terrainFoldout.Query<Foldout>().ToList()
                    .Where(foldout => foldout.userData is MosaicPaintingTarget).ToList();
                Assert.AreEqual(2, terrainLayerFoldouts.Count);
                Assert.IsTrue(terrainLayerFoldouts.All(foldout =>
                    foldout.ClassListContains("mosaic-paint-group--nested")));

                var linkedFoldouts = foldouts.Where(foldout => foldout.ClassListContains("mosaic-paint-linked"))
                    .ToList();
                Assert.AreEqual(new[] { "Alpha Links", "Zulu Links" },
                    linkedFoldouts.Select(foldout => foldout.text).ToArray());
                Assert.IsTrue(linkedFoldouts.All(foldout =>
                    foldout.ClassListContains("mosaic-paint-group--top-level")));

                var tilemapFoldout = foldouts.Single(foldout =>
                    foldout.userData is MosaicPaintingTarget target && ReferenceEquals(target.Owner, tilemap));
                Assert.IsTrue(tilemapFoldout.ClassListContains("mosaic-paint-group--top-level"));
                Assert.IsTrue(window.rootVisualElement.ClassListContains(EditorGUIUtility.isProSkin
                    ? "mosaic-paint-theme--dark"
                    : "mosaic-paint-theme--light"));

                var alphaButton = linkedFoldouts[0].Query<Button>(className: "mosaic-paint-value").First();
                Assert.AreEqual("Linked Paint", alphaButton.Q<Label>().text);
                Assert.AreSame(icon, alphaButton.Q<Image>().image);

                var zuluButton = linkedFoldouts[1].Query<Button>(className: "mosaic-paint-value").First();
                Assert.AreEqual("Layer 1", zuluButton.Q<Label>().text);
                Assert.AreEqual(Color.cyan, zuluButton.Q<Image>().style.backgroundColor.value);
                Assert.AreEqual(1f, zuluButton.style.backgroundColor.value.a);
                Assert.AreEqual(Color.cyan, zuluButton
                    .Q<VisualElement>(className: "mosaic-paint-value__accent").style.backgroundColor.value);

                var contextRows = window.rootVisualElement
                    .Query<VisualElement>(className: "mosaic-paint-value-row").ToList();
                Assert.IsTrue(contextRows.Any(row => ReferenceEquals(row.userData, terrain)));
                Assert.IsTrue(contextRows.Any(row => ReferenceEquals(row.userData, tilemap)));
                Assert.IsTrue(contextRows.Any(row => ReferenceEquals(row.userData, alpha)));
                Assert.IsTrue(contextRows.Any(row => ReferenceEquals(row.userData, zulu)));

                MosaicPaintingController.BrushSize = 3;
                Assert.AreEqual(3, MosaicPaintingController.BrushSize);
                Assert.AreEqual(2, MosaicPaintingController.BrushRadius);
                Assert.IsTrue(MosaicPaintingTool.IsWithinBrushRadius(2, 0));
                Assert.IsFalse(MosaicPaintingTool.IsWithinBrushRadius(2, 2));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [TestCase(2, 1)]
        [TestCase(-2, 1)]
        [TestCase(2, -1)]
        [TestCase(-2, -1)]
        public void PaintingTool_RectangleCellsAreInclusiveInEveryDirection(int endX, int endY)
        {
            MosaicPaintingController.BrushSize = MosaicPaintingController.MAX_BRUSH_SIZE;
            var cells = new HashSet<Vector2Int>();

            Assert.IsTrue(MosaicPaintingTool.TryAddRectangleCells(
                Vector2Int.zero, new Vector2Int(endX, endY), cells));

            var expected = Enumerable.Range(Math.Min(0, endX), Math.Abs(endX) + 1)
                .SelectMany(x => Enumerable.Range(Math.Min(0, endY), Math.Abs(endY) + 1)
                    .Select(y => new Vector2Int(x, y))).ToArray();
            CollectionAssert.AreEquivalent(expected, cells);
        }

        [Test]
        public void PaintingTool_RectangleRequiresAnotherGridCell()
        {
            var cells = new HashSet<Vector2Int>();

            Assert.IsFalse(MosaicPaintingTool.TryAddRectangleCells(Vector2Int.one, Vector2Int.one, cells));
            Assert.IsEmpty(cells);
        }

        [Test]
        public void PaintingSelection_ReportsRawTerrainAndLinkedOriginatingComponents()
        {
            EnterPrefabIsolation();
            var tilemap = CreateTilemap("Raw Target");
            var terrainObject = new GameObject("Terrain Owner", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var terrain = terrainObject.GetComponent<TilemapTerrainAuthoring>();
            terrain.renderingData.material = _material;
            terrain.intGridLayers.Add(_intGrid);
            var linked = CreateLinked("Linked Owner", (tilemap, 1));
            var stage = StageUtility.GetCurrentStageHandle();

            var rawSelection = MosaicPaintingSelection.Create(new MosaicPaintingTarget(tilemap),
                _intGrid.intGridValues[0], stage);
            var terrainSelection = MosaicPaintingSelection.Create(new MosaicPaintingTarget(terrain, _intGrid, 0),
                _intGrid.intGridValues[0], stage);
            var linkedSelection = CreateSelection(linked);

            Assert.AreSame(tilemap, rawSelection.OriginatingComponent);
            Assert.AreSame(terrain, terrainSelection.OriginatingComponent);
            Assert.AreSame(linked, linkedSelection.OriginatingComponent);
        }

        [Test]
        public void PaintingWindow_DisabledLinkedValueKeepsEnabledContextOwner()
        {
            EnterPrefabIsolation();
            var tilemap = CreateTilemap("Invalid Target");
            var linked = CreateLinked("Invalid Linked", (tilemap, 99));
            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var targets = GetWindowList<MosaicPaintingTarget>(window, "_targets");
                targets.Clear();
                targets.Add(new MosaicPaintingTarget(tilemap));
                var linkedComponents = GetWindowList<LinkedTilemapLayers>(window, "_linkedComponents");
                linkedComponents.Clear();
                linkedComponents.Add(linked);
                InvokeBuildPalette(window);

                var linkedFoldout = window.rootVisualElement.Query<Foldout>().ToList()
                    .Single(foldout => ReferenceEquals(foldout.userData, linked));
                var button = linkedFoldout.Q<Button>(className: "mosaic-paint-value");
                var row = button.parent;
                Assert.IsFalse(button.enabledSelf);
                Assert.IsTrue(row.enabledSelf);
                Assert.IsTrue(row.ClassListContains("mosaic-paint-value-row"));
                Assert.AreSame(linked, row.userData);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void PaintingWindow_NormalSceneAuthoringLocationsAreRejected()
        {
            var tilemap = CreateTilemap("Outside Tilemap");
            var terrainObject = new GameObject("Outside Terrain", typeof(TilemapTerrainAuthoring));
            terrainObject.transform.SetParent(_gridObject.transform);
            var linked = CreateLinked("Outside Linked", (tilemap, 1));
            var stage = StageUtility.GetCurrentStageHandle();

            Assert.IsFalse(MosaicPaintingController.IsAllowedAuthoringLocation(tilemap, stage));
            Assert.IsFalse(MosaicPaintingController.IsAllowedAuthoringLocation(
                terrainObject.GetComponent<TilemapTerrainAuthoring>(), stage));
            Assert.IsFalse(MosaicPaintingController.IsAllowedAuthoringLocation(linked, stage));
        }

        [Test]
        public void PaintingWindow_HideRawTargetValuesFiltersOnlyAffectedTilemaps()
        {
            EnterPrefabIsolation();
            var defaultTarget = CreateTilemap("Default Target");
            var hiddenTarget = CreateTilemap("Hidden Target");
            var unrelatedTarget = CreateTilemap("Unrelated Target");
            hiddenTarget.intGrid = CreateIntGrid("Hidden IntGrid", Color.red, Color.blue);
            unrelatedTarget.intGrid = CreateIntGrid("Unrelated IntGrid", Color.red, Color.blue);
            var defaultLinked = CreateLinked("Default Links", (defaultTarget, 1));
            var hiddenLinked = CreateLinked("Hidden Links", (hiddenTarget, 2));
            hiddenLinked.hideRawTargetValues = true;

            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var targets = GetWindowList<MosaicPaintingTarget>(window, "_targets");
                targets.Clear();
                targets.Add(new MosaicPaintingTarget(defaultTarget));
                targets.Add(new MosaicPaintingTarget(hiddenTarget));
                targets.Add(new MosaicPaintingTarget(unrelatedTarget));
                var linkedComponents = GetWindowList<LinkedTilemapLayers>(window, "_linkedComponents");
                linkedComponents.Clear();
                linkedComponents.Add(defaultLinked);
                linkedComponents.Add(hiddenLinked);
                InvokeBuildPalette(window);
                var foldouts = window.rootVisualElement.Query<Foldout>().ToList();
                var rawOwners = foldouts.Where(foldout => foldout.userData is MosaicPaintingTarget)
                    .Select(foldout => ((MosaicPaintingTarget)foldout.userData).Owner).ToList();

                Assert.IsFalse(defaultLinked.hideRawTargetValues);
                Assert.Contains(defaultTarget, rawOwners);
                Assert.IsFalse(rawOwners.Contains(hiddenTarget));
                Assert.Contains(unrelatedTarget, rawOwners);

                var hiddenSelection = CreateSelection(hiddenLinked);
                Assert.IsTrue(hiddenSelection.IsValid, hiddenSelection.ValidationMessage);
                Assert.AreSame(hiddenTarget, hiddenSelection.Anchor.Owner);

                var hiddenFoldout = foldouts.Single(foldout => ReferenceEquals(foldout.userData, hiddenLinked));
                var hiddenButton = hiddenFoldout.Q<Button>(className: "mosaic-paint-value");
                Assert.IsTrue(hiddenButton.enabledSelf, hiddenButton.tooltip);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void PaintingWindow_PendingTargetAndDependentLinkedLayerAreHidden()
        {
            EnterPrefabIsolation();
            var tilemap = CreateTilemap("Pending Target");
            var linked = CreateLinked("Pending Links", (tilemap, 1));
            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var targets = GetWindowList<MosaicPaintingTarget>(window, "_targets");
                targets.Clear();
                var linkedComponents = GetWindowList<LinkedTilemapLayers>(window, "_linkedComponents");
                linkedComponents.Clear();
                linkedComponents.Add(linked);
                InvokeBuildPalette(window);

                var foldouts = window.rootVisualElement.Query<Foldout>().ToList();
                Assert.IsFalse(foldouts.Any(foldout => ReferenceEquals(foldout.userData, linked)));
                Assert.IsFalse(foldouts.Any(foldout => foldout.userData is MosaicPaintingTarget));
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

        [Test]
        public void PaintingWindow_CtrlHTogglesRawIntGridVisibility()
        {
            var window = ScriptableObject.CreateInstance<MosaicPaintingWindow>();
            try
            {
                window.CreateGUI();
                var toggle = typeof(MosaicPaintingWindow).GetMethod("ToggleDetails",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var showField = typeof(MosaicPaintingWindow).GetField("_showIntGridColors",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.IsNotNull(toggle);
                Assert.IsNotNull(showField);
                Assert.IsFalse((bool)showField.GetValue(window));
                toggle.Invoke(window, null);
                Assert.IsTrue((bool)showField.GetValue(window));
                toggle.Invoke(window, null);
                Assert.IsFalse((bool)showField.GetValue(window));
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        private static List<T> GetWindowList<T>(MosaicPaintingWindow window, string fieldName)
        {
            var field = typeof(MosaicPaintingWindow).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (List<T>)field.GetValue(window);
        }

        private static void InvokeBuildPalette(MosaicPaintingWindow window)
        {
            var method = typeof(MosaicPaintingWindow).GetMethod("BuildPalette",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(window, null);
        }

        private IntGridDefinition CreateIntGrid(string name, params Color[] colors)
        {
            var intGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            intGrid.name = name;
            var hashField = typeof(IntGridDefinition).GetField("<Hash>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(hashField);
            hashField.SetValue(intGrid, new EntityHash128((uint)_intGrids.Count + 1, 0, 0, 0));
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

        private void EnterPrefabIsolation()
        {
            _prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/LinkedTilemapLayersTests.prefab");
            PrefabUtility.SaveAsPrefabAsset(_gridObject, _prefabPath);
            Object.DestroyImmediate(_gridObject);
            _gridObject = PrefabStageUtility.OpenPrefab(_prefabPath).prefabContentsRoot;
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

        private static void AssertCells(TilemapAuthoring target, short value, params Vector2Int[] positions)
        {
            Assert.AreEqual(positions.Length, target.PaintedCells.Count);
            CollectionAssert.AreEquivalent(positions, target.PaintedCells.Select(cell => cell.Position).ToArray());
            Assert.IsTrue(target.PaintedCells.All(cell => cell.Value == value));
        }
    }
}
