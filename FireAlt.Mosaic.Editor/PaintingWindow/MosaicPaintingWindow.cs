using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Unity.Rendering;
using Unity.Scenes;
using Unity.Transforms;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingShortcutContext : IShortcutContext
    {
        public bool active => MosaicPaintingWindow.ActiveWindow != null;
    }

    public sealed class MosaicPaintingWindow : EditorWindow
    {
        private const string SELECTED_CLASS = "mosaic-paint-value--selected";
        private static readonly Vector3[] CellCorners = new Vector3[4];
        private static readonly Comparison<RawCell> RawCellComparison = CompareRawCells;

        private readonly List<MosaicPaintingTarget> _targets = new();
        private readonly List<RawCell> _rawCells = new();
        private readonly List<LinkedTilemapLayers> _linkedComponents = new();
        private readonly List<MosaicPaintingSelection> _selections = new();
        private readonly List<Button> _valueButtons = new();

        private MosaicPaintingShortcutContext _shortcutContext;
        private ScrollView _palette;
        private ToolbarToggle _intGridColorsToggle;
        private ToolbarToggle _boundsToggle;
        private SliderInt _brushSize;
        private bool _showIntGridColors;
        private bool _showBounds;
        private bool _refreshQueued;
        private bool _selectFirstValue;
        private StageHandle _stage;
        private string _selectedSelectionId;

        internal static MosaicPaintingWindow ActiveWindow { get; private set; }

        [MenuItem("Window/Mosaic/Painting")]
        public static void Open()
        {
            OpenWindow();
        }

        internal static void OpenAndSelectFirst()
        {
            var window = OpenWindow();
            window._selectFirstValue = true;
            window.Refresh();
        }

        private static MosaicPaintingWindow OpenWindow()
        {
            var window = GetWindow<MosaicPaintingWindow>();
            window.titleContent = new GUIContent("Mosaic Painting");
            window.minSize = new Vector2(280f, 260f);
            window.Show();
            return window;
        }

        [Shortcut("Mosaic/Toggle Details", typeof(MosaicPaintingShortcutContext), KeyCode.H,
            ShortcutModifiers.Control)]
        private static void ToggleDetailsShortcut()
        {
            ActiveWindow?.ToggleDetails();
        }

        private static void ExitPainting(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape || !MosaicPaintingSession.IsPainting) return;

            evt.StopImmediatePropagation();
            MosaicPaintingTool.ExitPainting();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.EnableInClassList("mosaic-paint-theme--dark", EditorGUIUtility.isProSkin);
            root.EnableInClassList("mosaic-paint-theme--light", !EditorGUIUtility.isProSkin);
            root.styleSheets.Add(EditorResources.PaintingStyleSheet);

            var toolbar = new Toolbar();
            _intGridColorsToggle = new ToolbarToggle
            {
                text = "◉  Show IntGrid",
                tooltip = "Show raw IntGrid colors instead of RuleEngine and Mosaic presentation output. Ctrl+H",
                value = _showIntGridColors,
            };
            _intGridColorsToggle.RegisterValueChangedCallback(evt => SetShowIntGridColors(evt.newValue));
            toolbar.Add(_intGridColorsToggle);

            _boundsToggle = new ToolbarToggle
            {
                text = "Bounds",
                tooltip = "Show tilemap RenderBounds in the Scene View",
                value = _showBounds,
            };
            _boundsToggle.RegisterValueChangedCallback(evt =>
            {
                _showBounds = evt.newValue;
                SceneView.RepaintAll();
            });
            toolbar.Add(_boundsToggle);

            var randomize = new ToolbarButton(() => MosaicPaintingPreviewService.RandomizeRuleEngineSeed(_targets))
            {
                text = "Randomize",
                tooltip = "Randomize the seed used by RuleEngine and refresh the current Mosaic output",
            };
            toolbar.Add(randomize);
            root.Add(toolbar);

            var brushControls = new VisualElement();
            brushControls.AddToClassList("mosaic-paint-controls");
            _brushSize = new SliderInt("Brush Size", MosaicPaintingSession.MIN_BRUSH_SIZE,
                MosaicPaintingSession.MAX_BRUSH_SIZE)
            {
                value = MosaicPaintingSession.BrushSize,
                showInputField = true,
                tooltip = "Circular brush size. A size of 1 paints one cell.",
            };
            _brushSize.AddToClassList("mosaic-paint-brush-radius");
            _brushSize.RegisterValueChangedCallback(evt =>
            {
                MosaicPaintingSession.BrushSize = evt.newValue;
                SceneView.RepaintAll();
            });
            brushControls.Add(_brushSize);
            root.Add(brushControls);

            var controlsHelp = new HelpBox(
                "LMB paints, RMB erases, and Alt or Shift temporarily restores normal Scene View navigation. "
                + "Click the selected value again or press Escape to leave painting.",
                HelpBoxMessageType.None);
            controlsHelp.AddToClassList("mosaic-paint-help");
            root.Add(controlsHelp);

            _palette = new ScrollView(ScrollViewMode.Vertical);
            _palette.AddToClassList("mosaic-paint-palette");
            root.Add(_palette);

            Refresh();
        }

        private void OnEnable()
        {
            ActiveWindow = this;
            _shortcutContext = new MosaicPaintingShortcutContext();
            ShortcutManager.RegisterContext(_shortcutContext);
            rootVisualElement.RegisterCallback<KeyDownEvent>(ExitPainting, TrickleDown.TrickleDown);

            EditorApplication.update += OnEditorUpdate;
            SceneView.duringSceneGui += DuringSceneGui;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            MosaicPaintingPreviewService.Refreshed += QueueRefresh;
            MosaicPaintingSession.Changed += OnPaintingChanged;

            QueueRefresh();
        }

        private void OnDisable()
        {
            if (ReferenceEquals(ActiveWindow, this)) ActiveWindow = null;

            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= DuringSceneGui;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            MosaicPaintingPreviewService.Refreshed -= QueueRefresh;
            MosaicPaintingSession.Changed -= OnPaintingChanged;
            rootVisualElement.UnregisterCallback<KeyDownEvent>(ExitPainting, TrickleDown.TrickleDown);

            if (_shortcutContext != null) ShortcutManager.UnregisterContext(_shortcutContext);
            _shortcutContext = null;

            MosaicPaintingPreviewService.SetShowIntGridColors(_targets, false);
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool)) ToolManager.RestorePreviousPersistentTool();
        }

        private void OnEditorUpdate()
        {
            var currentStage = StageUtility.GetCurrentStageHandle();
            if (!_stage.Equals(currentStage))
            {
                MosaicPaintingPreviewService.SetShowIntGridColors(_targets, false);
                MosaicPaintingSession.Clear();
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
                {
                    ToolManager.RestorePreviousPersistentTool();
                }

                _stage = currentStage;
                _refreshQueued = true;
            }

            if (_refreshQueued && !EditorApplication.isCompiling)
            {
                _refreshQueued = false;
                Refresh();
            }

        }

        private void Refresh()
        {
            _refreshQueued = false;
            if (_palette == null) return;

            DiscoverTargets();
            BuildPalette();

            if (_selectFirstValue)
            {
                _selectFirstValue = false;
                SelectFirstValue();
            }

            ValidateSelection();
            MosaicPaintingPreviewService.SetShowIntGridColors(_targets, _showIntGridColors);

            SceneView.RepaintAll();
        }

        private void SelectFirstValue()
        {
            foreach (var selection in _selections)
            {
                if (!selection.IsValid) continue;

                _selectedSelectionId = selection.Id;
                MosaicPaintingSession.Select(selection);
                RefreshButtonSelection();
                return;
            }
        }

        private void ValidateSelection()
        {
            if (TryFindSelectedSelection(out var selection) && selection.IsValid)
            {
                MosaicPaintingSession.Select(selection);
                return;
            }

            _selectedSelectionId = null;
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
            {
                ToolManager.RestorePreviousPersistentTool();
            }
        }

        private void DiscoverTargets()
        {
            var currentStage = StageUtility.GetCurrentStageHandle();
            _stage = currentStage;
            DiscoverTargets(_targets, currentStage);
            DiscoverLinkedComponents(_linkedComponents, currentStage);
        }

        internal static bool HasTargets()
        {
            var targets = new List<MosaicPaintingTarget>();
            var stage = StageUtility.GetCurrentStageHandle();
            DiscoverTargets(targets, stage);
            foreach (var target in targets)
            {
                if (target.IsValid) return true;
            }

            var targetMap = CreateTilemapTargetMap(targets);
            var linkedComponents = new List<LinkedTilemapLayers>();
            DiscoverLinkedComponents(linkedComponents, stage);
            foreach (var component in linkedComponents)
            {
                if (component.layers == null) continue;
                for (var i = 0; i < component.layers.Count; i++)
                {
                    if (MosaicPaintingSelection.Create(component, i, targetMap, stage).IsValid) return true;
                }
            }

            return false;
        }

        private static void DiscoverTargets(List<MosaicPaintingTarget> targets, StageHandle currentStage)
        {
            targets.Clear();
            var inactiveAuthoringHashes = new HashSet<Hash128>();

            if (PrefabStageUtility.GetCurrentPrefabStage() == null)
            {
                DiscoverEditorWorldTargets(targets, currentStage);
            }

            MosaicPaintingPreviewService.AddAuthoringTargets(targets, currentStage, inactiveAuthoringHashes);
            RemoveEntityTargetsShadowedByAuthoring(targets, inactiveAuthoringHashes);

            targets.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
            ValidateDuplicateHashes(targets);
        }

        private static void DiscoverLinkedComponents(List<LinkedTilemapLayers> components, StageHandle stage)
        {
            components.Clear();
            foreach (var component in Resources.FindObjectsOfTypeAll<LinkedTilemapLayers>())
            {
                if (MosaicPaintingPreviewService.IsAllowedAuthoringLocation(component, stage)) components.Add(component);
            }

            components.Sort((left, right) =>
            {
                var comparison = string.CompareOrdinal(left.gameObject.name, right.gameObject.name);
                return comparison != 0 ? comparison : string.CompareOrdinal(
                    GlobalObjectId.GetGlobalObjectIdSlow(left).ToString(),
                    GlobalObjectId.GetGlobalObjectIdSlow(right).ToString());
            });
        }

        private static Dictionary<TilemapAuthoring, MosaicPaintingTarget> CreateTilemapTargetMap(
            IEnumerable<MosaicPaintingTarget> targets)
        {
            var result = new Dictionary<TilemapAuthoring, MosaicPaintingTarget>();
            foreach (var target in targets)
            {
                if (target.Owner is TilemapAuthoring tilemap && target.IsPaintable) result[tilemap] = target;
            }

            return result;
        }

        internal static void RemoveEntityTargetsShadowedByAuthoring(List<MosaicPaintingTarget> targets,
            IReadOnlyCollection<Hash128> inactiveAuthoringHashes = null)
        {
            var authoringHashes = new HashSet<Hash128>();
            foreach (var target in targets)
            {
                if (!target.IsEntityTarget) authoringHashes.Add(target.IntGridHash);
            }

            if (inactiveAuthoringHashes != null) authoringHashes.UnionWith(inactiveAuthoringHashes);

            targets.RemoveAll(target => target.IsEntityTarget && authoringHashes.Contains(target.IntGridHash));
        }

        private static void DiscoverEditorWorldTargets(List<MosaicPaintingTarget> targets, StageHandle currentStage)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var subSceneGuids = new HashSet<Hash128>();
            foreach (var subScene in SubScene.AllSubScenes)
            {
                if (subScene != null && subScene.isActiveAndEnabled && subScene.SceneGUID != default
                    && MosaicPaintingPreviewService.BelongsToStage(subScene, currentStage))
                {
                    subSceneGuids.Add(subScene.SceneGUID);
                }
            }

            var intGridEntities = new Dictionary<Hash128, Entity>();
            var intGridQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, SceneSection>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in intGridQuery.ToEntityArray(Allocator.Temp))
            {
                if (!IsSubSceneEntity(entityManager, entity, subSceneGuids)) continue;
                intGridEntities[entityManager.GetComponentData<IntGridData>(entity).Hash] = entity;
            }

            var terrainQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Data.TerrainData, TilemapRendererData, TilemapTerrainLayerElement, SceneSection>()
                .Build(entityManager);
            foreach (var terrainEntity in terrainQuery.ToEntityArray(Allocator.Temp))
            {
                if (!IsSubSceneEntity(entityManager, terrainEntity, subSceneGuids)) continue;
                var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);
                for (var i = 0; i < layers.Length; i++)
                {
                    if (!intGridEntities.TryGetValue(layers[i].IntGridHash, out var intGridEntity)) continue;
                    var name = entityManager.GetComponentData<IntGridData>(intGridEntity).DebugName.ToString();
                    targets.Add(new MosaicPaintingTarget(world, intGridEntity, terrainEntity,
                        $"Terrain / Layer {i + 1} / {name}", true, i));
                }
            }

            var tilemapQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, TilemapRendererData, SceneSection>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in tilemapQuery.ToEntityArray(Allocator.Temp))
            {
                if (!IsSubSceneEntity(entityManager, entity, subSceneGuids)) continue;
                var name = entityManager.GetComponentData<IntGridData>(entity).DebugName.ToString();
                targets.Add(new MosaicPaintingTarget(world, entity, entity, name, false, 0));
            }
        }

        internal static bool IsSubSceneEntity(EntityManager entityManager, Entity entity,
            HashSet<Hash128> subSceneGuids)
        {
            return !MosaicInitializationSystem.IsStaleSceneEntity(entityManager, entity)
                   && subSceneGuids.Contains(entityManager.GetSharedComponent<SceneSection>(entity).SceneGUID);
        }

        private static void ValidateDuplicateHashes(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            var hashes = new Dictionary<Hash128, MosaicPaintingTarget>();
            foreach (var target in targets)
            {
                if (hashes.TryGetValue(target.IntGridHash, out var existing))
                {
                    const string MESSAGE = "Another active tilemap uses the same runtime IntGrid hash.";
                    existing.AdditionalValidationMessage = MESSAGE;
                    target.AdditionalValidationMessage = MESSAGE;
                }
                else
                {
                    hashes.Add(target.IntGridHash, target);
                }
            }
        }

        private void BuildPalette()
        {
            _palette.Clear();
            _selections.Clear();
            _valueButtons.Clear();

            var hasPaintingTargets = false;
            var hiddenRawTargets = CollectHiddenRawTargets();
            var terrainTargets = new Dictionary<TilemapTerrainAuthoring, List<MosaicPaintingTarget>>();
            foreach (var target in _targets)
            {
                if (!target.HasLoadedAuthoringScene) continue;
                hasPaintingTargets = true;

                if (target.Owner is TilemapTerrainAuthoring terrain)
                {
                    if (!terrainTargets.TryGetValue(terrain, out var layers))
                    {
                        layers = new List<MosaicPaintingTarget>();
                        terrainTargets.Add(terrain, layers);
                    }

                    layers.Add(target);
                    continue;
                }

                if (target.Owner is TilemapAuthoring tilemap && hiddenRawTargets.Contains(tilemap)) continue;
                _palette.Add(CreateTargetFoldout(target, target.DisplayName));
            }

            var terrainOwners = new List<TilemapTerrainAuthoring>(terrainTargets.Keys);
            terrainOwners.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
            foreach (var terrain in terrainOwners)
            {
                var terrainFoldout = CreateGroupFoldout(terrain.name, terrain);
                terrainFoldout.AddToClassList("mosaic-paint-terrain");

                var layers = terrainTargets[terrain];
                layers.Sort((left, right) => left.LayerIndex.CompareTo(right.LayerIndex));
                foreach (var target in layers)
                {
                    var layerName = $"Layer {target.LayerIndex + 1} / {target.IntGrid?.name ?? "Missing IntGrid"}";
                    terrainFoldout.Add(CreateTargetFoldout(target, layerName, true));
                }

                _palette.Add(terrainFoldout);
            }

            var tilemapTargets = CreateTilemapTargetMap(_targets);
            foreach (var component in _linkedComponents)
            {
                hasPaintingTargets = true;
                var linkedFoldout = CreateGroupFoldout(component.gameObject.name, component);
                linkedFoldout.AddToClassList("mosaic-paint-linked");

                var linkedLayers = component.layers ?? new List<LinkedLayer>();
                for (var i = 0; i < linkedLayers.Count; i++)
                {
                    var selection = MosaicPaintingSelection.Create(component, i, tilemapTargets, _stage);
                    _selections.Add(selection);
                    var button = CreateValueButton(selection);
                    button.SetEnabled(selection.IsValid);
                    if (!selection.IsValid) button.tooltip = selection.ValidationMessage;
                    linkedFoldout.Add(button);
                    if (!selection.IsValid)
                    {
                        linkedFoldout.Add(new HelpBox(selection.ValidationMessage, HelpBoxMessageType.Error));
                    }
                }

                _palette.Add(linkedFoldout);
            }

            if (!hasPaintingTargets)
            {
                _palette.Add(new HelpBox(
                    "No editable Mosaic tilemaps were found in the current stage. Open a SubScene to paint its IntGrid layers.",
                    HelpBoxMessageType.Info));
            }
        }

        private HashSet<TilemapAuthoring> CollectHiddenRawTargets()
        {
            var hiddenTargets = new HashSet<TilemapAuthoring>();
            foreach (var component in _linkedComponents)
            {
                if (!component.hideRawTargetValues || component.layers == null) continue;
                foreach (var layer in component.layers)
                {
                    if (layer?.Operations == null) continue;
                    foreach (var operation in layer.Operations)
                    {
                        if (operation?.target != null) hiddenTargets.Add(operation.target);
                    }
                }
            }

            return hiddenTargets;
        }

        private static Foldout CreateGroupFoldout(string text, object userData, bool nested = false)
        {
            var foldout = new Foldout
            {
                text = text,
                value = true,
                userData = userData,
            };
            foldout.AddToClassList("mosaic-paint-group");
            foldout.AddToClassList(nested ? "mosaic-paint-group--nested" : "mosaic-paint-group--top-level");
            return foldout;
        }

        private Foldout CreateTargetFoldout(MosaicPaintingTarget target, string text, bool nested = false)
        {
            var foldout = CreateGroupFoldout(text, target, nested);

            if (!target.IsValid)
            {
                foldout.Add(new HelpBox(target.AdditionalValidationMessage, HelpBoxMessageType.Error));
                return foldout;
            }

            foreach (var value in target.Values)
            {
                var selection = MosaicPaintingSelection.Create(target, value, _stage);
                _selections.Add(selection);
                foldout.Add(CreateValueButton(selection));
            }

            return foldout;
        }

        private Button CreateValueButton(MosaicPaintingSelection selection)
        {
            var button = new Button(() => SelectValue(selection));
            button.AddToClassList("mosaic-paint-value");
            button.userData = selection.Id;

            var color = selection.Color;
            color.a = 1f;
            var normalColor = Color.Lerp(new Color(0.12f, 0.12f, 0.12f, 1f), color, 0.32f);
            button.style.backgroundColor = normalColor;

            var accent = new VisualElement();
            accent.AddToClassList("mosaic-paint-value__accent");
            accent.style.backgroundColor = color;
            button.Add(accent);

            var preview = new Image
            {
                image = selection.Icon,
                scaleMode = ScaleMode.ScaleToFit,
            };
            preview.AddToClassList("mosaic-paint-value__preview");
            if (selection.Icon == null) preview.style.backgroundColor = color;
            button.Add(preview);

            var label = new Label(selection.Name);
            label.AddToClassList("mosaic-paint-value__label");
            label.style.color = Color.Lerp(Color.white, color, 0.35f);
            button.Add(label);

            if (_selectedSelectionId == selection.Id)
            {
                button.AddToClassList(SELECTED_CLASS);
            }

            _valueButtons.Add(button);
            return button;
        }

        private void SelectValue(MosaicPaintingSelection selection)
        {
            if (!selection.IsValid)
            {
                ValidateSelection();
                return;
            }

            if (_selectedSelectionId == selection.Id)
            {
                _selectedSelectionId = null;
                MosaicPaintingSession.Clear();
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
                {
                    ToolManager.RestorePreviousPersistentTool();
                }

                RefreshButtonSelection();
                SceneView.RepaintAll();
                return;
            }

            _selectedSelectionId = selection.Id;
            MosaicPaintingSession.Select(selection);
            RefreshButtonSelection();
            SceneView.RepaintAll();
        }

        private void RefreshButtonSelection()
        {
            foreach (var button in _valueButtons)
            {
                button.RemoveFromClassList(SELECTED_CLASS);
                if (button.userData is string id && id == _selectedSelectionId)
                {
                    button.AddToClassList(SELECTED_CLASS);
                }
            }
        }

        private void SetShowIntGridColors(bool showIntGridColors)
        {
            if (_showIntGridColors == showIntGridColors) return;
            _showIntGridColors = showIntGridColors;
            _intGridColorsToggle?.SetValueWithoutNotify(showIntGridColors);

            MosaicPaintingPreviewService.SetShowIntGridColors(_targets, showIntGridColors);

            SceneView.RepaintAll();
        }

        private void ToggleDetails()
        {
            SetShowIntGridColors(_showIntGridColors);
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_showBounds) DrawRenderBounds();
            if (_showIntGridColors) DrawRawCells(sceneView);
        }

        private void DrawRenderBounds()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData, RenderBounds, LocalToWorld>()
                .Build(entityManager);

            var previousColor = Handles.color;
            var previousMatrix = Handles.matrix;
            var previousZTest = Handles.zTest;
            Handles.color = Color.yellow;
            Handles.zTest = CompareFunction.LessEqual;

            foreach (var entity in query.ToEntityArray(Allocator.Temp))
            {
                var rendererData = entityManager.GetComponentData<TilemapRendererData>(entity);
                if (!ContainsRenderer(rendererData.MeshHash)) continue;

                var localToWorld = entityManager.GetComponentData<LocalToWorld>(entity).Value;
                var bounds = entityManager.GetComponentData<RenderBounds>(entity).Value;
                Handles.matrix = new Matrix4x4(localToWorld.c0, localToWorld.c1, localToWorld.c2, localToWorld.c3);
                Handles.DrawWireCube(new Vector3(bounds.Center.x, bounds.Center.y, bounds.Center.z),
                    new Vector3(bounds.Extents.x, bounds.Extents.y, bounds.Extents.z) * 2f);
            }

            Handles.color = previousColor;
            Handles.matrix = previousMatrix;
            Handles.zTest = previousZTest;
            query.Dispose();
        }

        private bool ContainsRenderer(Hash128 hash)
        {
            foreach (var target in _targets)
            {
                if (target.IsValid && target.RendererHash == hash) return true;
            }

            return false;
        }

        private void DrawRawCells(SceneView sceneView)
        {
            var camera = sceneView.camera;
            if (camera == null) return;

            _rawCells.Clear();
            var order = 0;
            var cameraPosition = camera.transform.position;
            var cameraForward = camera.transform.forward;
            foreach (var target in _targets)
            {
                if (!target.IsValid) continue;
                foreach (var cell in target.Cells)
                {
                    _rawCells.Add(new RawCell(target, cell, cameraPosition, cameraForward, order++));
                }
            }

            _rawCells.Sort(RawCellComparison);

            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;

            foreach (var rawCell in _rawCells)
            {
                var color = rawCell.Target.TryGetValueDefinition(rawCell.Cell.Value, out var definition)
                    ? definition.color
                    : Color.magenta;
                var fill = color;
                fill.a = 0.55f;
                color.a = 0.9f;

                rawCell.Target.GetCellCorners(rawCell.Cell.Position, CellCorners);
                Handles.DrawSolidRectangleWithOutline(CellCorners, fill, color);
            }

            Handles.zTest = previousZTest;
        }

        internal static int CompareRawCells(RawCell left, RawCell right)
        {
            var comparison = right.Depth.CompareTo(left.Depth);
            return comparison != 0 ? comparison : left.Order.CompareTo(right.Order);
        }

        internal readonly struct RawCell
        {
            public RawCell(MosaicPaintingTarget target, SerializedIntGridCell cell, Vector3 cameraPosition,
                Vector3 cameraForward, int order)
            {
                Target = target;
                Cell = cell;
                Order = order;
                Depth = Vector3.Dot(target.GetCellCenter(cell.Position) - cameraPosition, cameraForward);
            }

            public MosaicPaintingTarget Target { get; }

            public SerializedIntGridCell Cell { get; }

            public float Depth { get; }

            public int Order { get; }
        }

        private void OnPaintingChanged()
        {
            if (!MosaicPaintingSession.IsPainting)
            {
                _selectedSelectionId = null;
            }

            RefreshButtonSelection();
        }

        private bool TryFindSelectedSelection(out MosaicPaintingSelection selection)
        {
            foreach (var candidate in _selections)
            {
                if (candidate.Id != _selectedSelectionId) continue;
                selection = candidate;
                return true;
            }

            selection = null;
            return false;
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                MosaicPaintingPreviewService.SetShowIntGridColors(_targets, true);
                MosaicPaintingSession.Clear();
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
                {
                    ToolManager.RestorePreviousPersistentTool();
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                QueueRefresh();
            }
        }

        private void QueueRefresh()
        {
            _refreshQueued = true;
        }
    }
}
