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

        private readonly List<MosaicPaintingTarget> _targets = new();
        private readonly List<Button> _valueButtons = new();

        private MosaicPaintingShortcutContext _shortcutContext;
        private ScrollView _palette;
        private ToolbarToggle _detailsToggle;
        private ToolbarToggle _boundsToggle;
        private SliderInt _brushRadius;
        private bool _details = true;
        private bool _showBounds;
        private bool _refreshQueued;
        private bool _selectFirstValue;
        private StageHandle _stage;
        private string _selectedTargetId;
        private short _selectedValue;

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

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.styleSheets.Add(EditorResources.PaintingStyleSheet);

            var toolbar = new Toolbar();
            _detailsToggle = new ToolbarToggle
            {
                text = "◉  Show details",
                tooltip = "Show RuleEngine and Mosaic presentation output instead of raw IntGrid colors. Ctrl+H",
                value = _details,
            };
            _detailsToggle.RegisterValueChangedCallback(evt => SetDetails(evt.newValue));
            toolbar.Add(_detailsToggle);

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

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            toolbar.Add(spacer);

            var refresh = new ToolbarButton(QueueRefresh)
            {
                text = "Refresh",
                tooltip = "Rediscover tilemaps in the current stage",
            };
            toolbar.Add(refresh);
            root.Add(toolbar);

            var brushControls = new VisualElement();
            brushControls.AddToClassList("mosaic-paint-controls");
            _brushRadius = new SliderInt("Brush Radius", MosaicPaintingSession.MIN_BRUSH_RADIUS,
                MosaicPaintingSession.MAX_BRUSH_RADIUS)
            {
                value = MosaicPaintingSession.BrushRadius,
                showInputField = true,
                tooltip = "Circular brush radius in cells. A radius of 0 paints one cell.",
            };
            _brushRadius.AddToClassList("mosaic-paint-brush-radius");
            _brushRadius.RegisterValueChangedCallback(evt =>
            {
                MosaicPaintingSession.BrushRadius = evt.newValue;
                SceneView.RepaintAll();
            });
            brushControls.Add(_brushRadius);
            root.Add(brushControls);

            var controlsHelp = new HelpBox(
                "LMB paints, RMB erases, and Alt temporarily restores normal Scene View navigation. "
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

            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.hierarchyChanged += QueueRefresh;
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            SceneView.duringSceneGui += DuringSceneGui;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            MosaicPaintingSession.Changed += OnPaintingChanged;

            QueueRefresh();
        }

        private void OnDisable()
        {
            if (ReferenceEquals(ActiveWindow, this)) ActiveWindow = null;

            EditorApplication.projectChanged -= QueueRefresh;
            EditorApplication.hierarchyChanged -= QueueRefresh;
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            SceneView.duringSceneGui -= DuringSceneGui;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            MosaicPaintingSession.Changed -= OnPaintingChanged;

            if (_shortcutContext != null) ShortcutManager.UnregisterContext(_shortcutContext);
            _shortcutContext = null;

            MosaicPaintingPreviewService.SetDetails(_targets, true);
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool)) ToolManager.RestorePreviousPersistentTool();
        }

        private void OnEditorUpdate()
        {
            var currentStage = StageUtility.GetCurrentStageHandle();
            if (!_stage.Equals(currentStage))
            {
                MosaicPaintingPreviewService.SetDetails(_targets, true);
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
            MosaicPaintingPreviewService.QueueRefresh();
            BuildPalette();

            if (_selectFirstValue)
            {
                _selectFirstValue = false;
                SelectFirstValue();
            }

            ValidateSelection();
            MosaicPaintingPreviewService.SetDetails(_targets, _details);

            SceneView.RepaintAll();
        }

        private void SelectFirstValue()
        {
            foreach (var target in _targets)
            {
                if (!target.IsValid || target.Values.Count == 0) continue;

                var value = target.Values[0];
                _selectedTargetId = target.Id;
                _selectedValue = value.value;
                MosaicPaintingSession.Select(target, value);
                RefreshButtonSelection();
                return;
            }
        }

        private void ValidateSelection()
        {
            if (TryFindSelectedTarget(out _)) return;

            _selectedTargetId = null;
            _selectedValue = 0;
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
            {
                ToolManager.RestorePreviousPersistentTool();
            }
        }

        private void DiscoverTargets()
        {
            _targets.Clear();
            var currentStage = StageUtility.GetCurrentStageHandle();
            _stage = currentStage;

            if (PrefabStageUtility.GetCurrentPrefabStage() == null)
            {
                DiscoverEditorWorldTargets();
            }

            MosaicPaintingPreviewService.AddAuthoringTargets(_targets, currentStage);

            _targets.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
            ValidateDuplicateHashes();
        }

        private void DiscoverEditorWorldTargets()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.Editor) == 0) return;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var intGridEntities = new Dictionary<Hash128, Entity>();
            var intGridQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, SceneSection>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in intGridQuery.ToEntityArray(Allocator.Temp))
            {
                intGridEntities[entityManager.GetComponentData<IntGridData>(entity).Hash] = entity;
            }

            var terrainQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<FireAlt.Mosaic.Data.TerrainData, TilemapRendererData, TilemapTerrainLayerElement, SceneSection>()
                .Build(entityManager);
            foreach (var terrainEntity in terrainQuery.ToEntityArray(Allocator.Temp))
            {
                var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(terrainEntity);
                for (var i = 0; i < layers.Length; i++)
                {
                    if (!intGridEntities.TryGetValue(layers[i].IntGridHash, out var intGridEntity)) continue;
                    var name = entityManager.GetComponentData<IntGridData>(intGridEntity).DebugName.ToString();
                    _targets.Add(new MosaicPaintingTarget(world, intGridEntity, terrainEntity,
                        $"Terrain / Layer {i + 1} / {name}", true, i));
                }
            }

            var tilemapQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, TilemapRendererData, SceneSection>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in tilemapQuery.ToEntityArray(Allocator.Temp))
            {
                var name = entityManager.GetComponentData<IntGridData>(entity).DebugName.ToString();
                _targets.Add(new MosaicPaintingTarget(world, entity, entity, name, false, 0));
            }
        }

        private void ValidateDuplicateHashes()
        {
            var hashes = new Dictionary<Hash128, MosaicPaintingTarget>();
            foreach (var target in _targets)
            {
                if (hashes.TryGetValue(target.IntGridHash, out var existing))
                {
                    const string message = "Another active tilemap uses the same runtime IntGrid hash.";
                    existing.AdditionalValidationMessage = message;
                    target.AdditionalValidationMessage = message;
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
            _valueButtons.Clear();

            if (_targets.Count == 0)
            {
                _palette.Add(new HelpBox("No Mosaic tilemaps were found in the current scene or prefab stage.",
                    HelpBoxMessageType.Info));
                return;
            }

            foreach (var target in _targets)
            {
                var foldout = new Foldout
                {
                    text = target.DisplayName,
                    value = true,
                };
                foldout.AddToClassList("mosaic-paint-group");

                if (!target.IsValid)
                {
                    foldout.Add(new HelpBox(target.ValidationMessage, HelpBoxMessageType.Error));
                    _palette.Add(foldout);
                    continue;
                }

                foreach (var value in target.Values)
                {
                    foldout.Add(CreateValueButton(target, value));
                }

                _palette.Add(foldout);
            }
        }

        private Button CreateValueButton(MosaicPaintingTarget target, IntGridValueDefinition value)
        {
            var button = new Button(() => SelectValue(target, value));
            button.AddToClassList("mosaic-paint-value");
            button.userData = (target.Id, value.value);

            var normalColor = Color.Lerp(new Color(0.12f, 0.12f, 0.12f, 1f), value.color, 0.32f);
            button.style.backgroundColor = normalColor;

            var accent = new VisualElement();
            accent.AddToClassList("mosaic-paint-value__accent");
            accent.style.backgroundColor = value.color;
            button.Add(accent);

            var preview = new Image
            {
                image = value.texture,
                scaleMode = ScaleMode.ScaleToFit,
            };
            preview.AddToClassList("mosaic-paint-value__preview");
            if (value.texture == null) preview.style.backgroundColor = value.color;
            button.Add(preview);

            var label = new Label(string.IsNullOrWhiteSpace(value.name) ? value.value.ToString() : value.name);
            label.AddToClassList("mosaic-paint-value__label");
            label.style.color = Color.Lerp(Color.white, value.color, 0.35f);
            button.Add(label);

            if (_selectedTargetId == target.Id && _selectedValue == value.value)
            {
                button.AddToClassList(SELECTED_CLASS);
            }

            _valueButtons.Add(button);
            return button;
        }

        private void SelectValue(MosaicPaintingTarget target, IntGridValueDefinition value)
        {
            if (_selectedTargetId == target.Id && _selectedValue == value.value)
            {
                _selectedTargetId = null;
                _selectedValue = 0;
                MosaicPaintingSession.Clear();
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
                {
                    ToolManager.RestorePreviousPersistentTool();
                }

                RefreshButtonSelection();
                SceneView.RepaintAll();
                return;
            }

            _selectedTargetId = target.Id;
            _selectedValue = value.value;
            MosaicPaintingSession.Select(target, value);
            RefreshButtonSelection();
            SceneView.RepaintAll();
        }

        private void RefreshButtonSelection()
        {
            foreach (var button in _valueButtons)
            {
                button.RemoveFromClassList(SELECTED_CLASS);
                if (button.userData is ValueTuple<string, short> data
                    && data.Item1 == _selectedTargetId && data.Item2 == _selectedValue)
                {
                    button.AddToClassList(SELECTED_CLASS);
                }
            }
        }

        private void SetDetails(bool details)
        {
            if (_details == details) return;
            _details = details;
            _detailsToggle?.SetValueWithoutNotify(details);

            MosaicPaintingPreviewService.SetDetails(_targets, details);

            SceneView.RepaintAll();
        }

        private void ToggleDetails()
        {
            SetDetails(!_details);
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_showBounds) DrawRenderBounds();
            if (!_details) DrawRawCells();
        }

        private void DrawRenderBounds()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.Editor) == 0) return;

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

        private void DrawRawCells()
        {
            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;

            foreach (var target in _targets)
            {
                if (!target.IsValid) continue;
                foreach (var cell in target.Cells)
                {
                    var color = target.TryGetValueDefinition(cell.Value, out var definition)
                        ? definition.color
                        : Color.magenta;
                    var fill = color;
                    fill.a = 0.55f;
                    color.a = 0.9f;

                    target.GetCellCorners(cell.Position, CellCorners);
                    Handles.DrawSolidRectangleWithOutline(CellCorners, fill, color);
                }
            }

            Handles.zTest = previousZTest;
        }

        private void OnPaintingChanged()
        {
            if (!MosaicPaintingSession.IsPainting)
            {
                _selectedTargetId = null;
                _selectedValue = 0;
            }

            RefreshButtonSelection();
        }

        private bool TryFindSelectedTarget(out MosaicPaintingTarget target)
        {
            foreach (var candidate in _targets)
            {
                if (candidate.Id != _selectedTargetId) continue;
                target = candidate;
                return true;
            }

            target = null;
            return false;
        }

        private void OnUndoRedo()
        {
            QueueRefresh();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                MosaicPaintingPreviewService.SetDetails(_targets, true);
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
