using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingShortcutContext : IShortcutContext
    {
        public bool active => MosaicPaintingWindow.ActiveWindow != null;
    }

    public sealed class MosaicPaintingWindow : EditorWindow
    {
        private readonly struct RenderKey : IEquatable<RenderKey>
        {
            public RenderKey(World world, Entity entity)
            {
                World = world;
                Entity = entity;
            }

            public World World { get; }

            public Entity Entity { get; }

            public bool Equals(RenderKey other)
            {
                return ReferenceEquals(World, other.World) && Entity == other.Entity;
            }

            public override bool Equals(object obj)
            {
                return obj is RenderKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(World, Entity);
            }
        }

        private const string SELECTED_CLASS = "mosaic-paint-value--selected";
        private static readonly Vector3[] CellCorners = new Vector3[4];

        private readonly List<MosaicPaintingTarget> _targets = new();
        private readonly Dictionary<RenderKey, bool> _hiddenRenderers = new();
        private readonly List<Button> _valueButtons = new();

        private MosaicPreviewWorld _previewWorld;
        private MosaicPaintingShortcutContext _shortcutContext;
        private ScrollView _palette;
        private ToolbarToggle _detailsToggle;
        private SliderInt _brushRadius;
        private bool _details;
        private bool _refreshQueued;
        private int _previewUpdatesRemaining;
        private StageHandle _stage;
        private string _selectedTargetId;
        private short _selectedValue;

        internal static MosaicPaintingWindow ActiveWindow { get; private set; }

        [MenuItem("Window/Mosaic/Painting")]
        public static void Open()
        {
            var window = GetWindow<MosaicPaintingWindow>();
            window.titleContent = new GUIContent("Mosaic Painting");
            window.minSize = new Vector2(280f, 260f);
            window.Show();
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
            _previewWorld = new MosaicPreviewWorld();
            _shortcutContext = new MosaicPaintingShortcutContext();
            ShortcutManager.RegisterContext(_shortcutContext);

            EditorApplication.hierarchyChanged += QueueRefresh;
            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            SceneView.duringSceneGui += DuringSceneGui;
            PrefabStage.prefabStageOpened += OnPrefabStageChanged;
            PrefabStage.prefabStageClosing += OnPrefabStageChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            MosaicPaintingSession.Changed += OnPaintingChanged;
            MosaicPaintingSession.CellsChanged += OnCellsChanged;

            QueueRefresh();
        }

        private void OnDisable()
        {
            if (ReferenceEquals(ActiveWindow, this)) ActiveWindow = null;

            EditorApplication.hierarchyChanged -= QueueRefresh;
            EditorApplication.projectChanged -= QueueRefresh;
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            SceneView.duringSceneGui -= DuringSceneGui;
            PrefabStage.prefabStageOpened -= OnPrefabStageChanged;
            PrefabStage.prefabStageClosing -= OnPrefabStageChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            MosaicPaintingSession.Changed -= OnPaintingChanged;
            MosaicPaintingSession.CellsChanged -= OnCellsChanged;

            if (_shortcutContext != null) ShortcutManager.UnregisterContext(_shortcutContext);
            _shortcutContext = null;

            RestoreRenderers();
            DisposePreview();
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool)) ToolManager.RestorePreviousPersistentTool();
        }

        private void OnEditorUpdate()
        {
            var currentStage = StageUtility.GetCurrentStageHandle();
            if (!_stage.Equals(currentStage))
            {
                RestoreRenderers();
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

            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (_details && _previewUpdatesRemaining > 0)
            {
                _previewWorld?.Update();
                _previewUpdatesRemaining--;
                SceneView.RepaintAll();
            }
        }

        private void Refresh()
        {
            if (_palette == null) return;

            RestoreRenderers();
            DiscoverTargets();
            _previewWorld?.Rebuild(_targets);
            BuildPalette();

            if (!TryFindSelectedTarget(out _))
            {
                _selectedTargetId = null;
                _selectedValue = 0;
                MosaicPaintingSession.Clear();
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
                {
                    ToolManager.RestorePreviousPersistentTool();
                }
            }

            if (_details)
            {
                ReseedAll();
                RequestPreviewUpdates();
            }
            else
            {
                HideRuntimeRenderers();
            }

            SceneView.RepaintAll();
        }

        private void DiscoverTargets()
        {
            _targets.Clear();
            var currentStage = StageUtility.GetCurrentStageHandle();
            _stage = currentStage;

            foreach (var tilemap in Resources.FindObjectsOfTypeAll<TilemapAuthoring>())
            {
                if (!BelongsToStage(tilemap, currentStage)) continue;
                _targets.Add(new MosaicPaintingTarget(tilemap));
            }

            foreach (var terrain in Resources.FindObjectsOfTypeAll<TilemapTerrainAuthoring>())
            {
                if (!BelongsToStage(terrain, currentStage)) continue;
                for (var i = 0; i < terrain.intGridLayers.Count; i++)
                {
                    _targets.Add(new MosaicPaintingTarget(terrain, terrain.intGridLayers[i], i));
                }
            }

            _targets.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
            ValidateDuplicateHashes();
        }

        private void ValidateDuplicateHashes()
        {
            var hashes = new Dictionary<Hash128, MosaicPaintingTarget>();
            var terrainDefinitions = new HashSet<(TilemapTerrainAuthoring Owner, IntGridDefinition Definition)>();
            foreach (var target in _targets)
            {
                if (target.IntGrid == null) continue;

                if (target.Owner is TilemapTerrainAuthoring terrainOwner
                    && !terrainDefinitions.Add((terrainOwner, target.IntGrid)))
                {
                    target.AdditionalValidationMessage = "This terrain contains the same IntGridDefinition more than once.";
                    foreach (var existingTarget in _targets)
                    {
                        if (existingTarget.Owner == terrainOwner && existingTarget.IntGrid == target.IntGrid)
                        {
                            existingTarget.AdditionalValidationMessage = target.AdditionalValidationMessage;
                        }
                    }

                    continue;
                }

                var isGlobal = target.Owner switch
                {
                    TilemapAuthoring tilemap => tilemap.isGlobal,
                    TilemapTerrainAuthoring terrain => terrain.isGlobal,
                    _ => false,
                };

                if (!isGlobal) continue;
                if (hashes.TryGetValue(target.IntGrid.Hash, out var existing))
                {
                    const string message = "Another active owner uses the same global IntGrid hash.";
                    existing.AdditionalValidationMessage = message;
                    target.AdditionalValidationMessage = message;
                }
                else
                {
                    hashes.Add(target.IntGrid.Hash, target);
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

                if (!target.IsSubScene && target.HasEntityResults)
                {
                    foldout.Add(new HelpBox(
                        "Manual preview shows sprite and terrain results. Entity-prefab results require normal SubScene baking.",
                        HelpBoxMessageType.Info));
                }

                foreach (var value in target.IntGrid.intGridValues)
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

            if (details)
            {
                RestoreRenderers();
                ReseedAll();
                RequestPreviewUpdates();
            }
            else
            {
                _previewUpdatesRemaining = 0;
                HideRuntimeRenderers();
            }

            SceneView.RepaintAll();
        }

        private void ToggleDetails()
        {
            SetDetails(!_details);
        }

        private void DuringSceneGui(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_details)
            {
                DrawManualPreviews(sceneView);
                return;
            }

            DrawRawCells();
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

        private void DrawManualPreviews(SceneView sceneView)
        {
            if (_previewWorld == null) return;

            foreach (var target in _targets)
            {
                if (target.IsSubScene || !target.IsValid
                    || !_previewWorld.TryGetRenderData(target, out var mesh, out var material))
                {
                    continue;
                }

                var renderingData = target.RenderingData;
                var renderParams = new RenderParams(material)
                {
                    camera = sceneView.camera,
                    layer = target.Owner.gameObject.layer,
                    renderingLayerMask = renderingData.renderingLayerMask,
                    shadowCastingMode = renderingData.shadowCastingMode,
                    receiveShadows = renderingData.receiveShadows,
                    sceneCullingMask = target.Owner.gameObject.sceneCullingMask,
                };
                Graphics.RenderMesh(renderParams, mesh, 0, target.Owner.transform.localToWorldMatrix);
            }
        }

        private void HideRuntimeRenderers()
        {
            foreach (var target in _targets)
            {
                if (!TryGetBinding(target, out var binding)) continue;
                SetRendererEnabled(binding.World, binding.RenderEntity, false);
                HideSpawnedRenderers(binding);
            }
        }

        private void HideSpawnedRenderers(MosaicPreviewWorld.Binding binding)
        {
            if (!binding.World.IsCreated) return;
            var entityManager = binding.World.EntityManager;
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<TilemapIntGridSingleton>());
            if (query.IsEmpty) return;

            var singleton = query.GetSingleton<TilemapIntGridSingleton>();
            if (!singleton.IntGridLayers.TryGetValue(binding.Hash, out var layer)) return;

            foreach (var pair in layer.SpawnedEntities)
            {
                if (!entityManager.Exists(pair.Value)) continue;
                SetRendererEnabled(binding.World, pair.Value, false);
                if (!entityManager.HasBuffer<LinkedEntityGroup>(pair.Value)) continue;

                foreach (var linked in entityManager.GetBuffer<LinkedEntityGroup>(pair.Value))
                {
                    SetRendererEnabled(binding.World, linked.Value, false);
                }
            }
        }

        private void SetRendererEnabled(World world, Entity entity, bool enabled)
        {
            if (world == null || !world.IsCreated) return;
            var entityManager = world.EntityManager;
            if (!entityManager.Exists(entity) || !entityManager.HasComponent<MaterialMeshInfo>(entity)) return;

            var key = new RenderKey(world, entity);
            if (!_hiddenRenderers.ContainsKey(key))
            {
                _hiddenRenderers.Add(key, entityManager.IsComponentEnabled<MaterialMeshInfo>(entity));
            }

            entityManager.SetComponentEnabled<MaterialMeshInfo>(entity, enabled);
        }

        private void RestoreRenderers()
        {
            foreach (var pair in _hiddenRenderers)
            {
                if (pair.Key.World == null || !pair.Key.World.IsCreated) continue;
                var entityManager = pair.Key.World.EntityManager;
                if (!entityManager.Exists(pair.Key.Entity)
                    || !entityManager.HasComponent<MaterialMeshInfo>(pair.Key.Entity))
                {
                    continue;
                }

                entityManager.SetComponentEnabled<MaterialMeshInfo>(pair.Key.Entity, pair.Value);
            }

            _hiddenRenderers.Clear();
        }

        private void ReseedAll()
        {
            foreach (var target in _targets) Reseed(target);
        }

        private void Reseed(MosaicPaintingTarget target)
        {
            if (target == null) return;
            if (!target.IsSubScene) _previewWorld?.Reseed(target);
            else if (TryResolveSubSceneBinding(target, out var binding)) MosaicPreviewWorld.Reseed(binding, target);
        }

        private bool TryGetBinding(MosaicPaintingTarget target, out MosaicPreviewWorld.Binding binding)
        {
            binding = default;
            if (target.IsSubScene) return TryResolveSubSceneBinding(target, out binding);
            return _previewWorld != null && _previewWorld.TryGetBinding(target, out binding);
        }

        private static bool TryResolveSubSceneBinding(MosaicPaintingTarget target,
            out MosaicPreviewWorld.Binding binding)
        {
            binding = default;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.Editor) == 0) return false;

            var entityManager = world.EntityManager;
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityGuid>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            var entities = query.ToEntityArray(Allocator.Temp);
            var guids = query.ToComponentDataArray<EntityGuid>(Allocator.Temp);
            var originatingId = target.Owner.gameObject.GetEntityId();

            if (!target.IsTerrain)
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (guids[i].OriginatingEntityId != originatingId
                        || !entityManager.HasComponent<IntGridData>(entity))
                    {
                        continue;
                    }

                    var hash = entityManager.GetComponentData<IntGridData>(entity).Hash;
                    var renderHash = entityManager.HasComponent<TilemapRendererData>(entity)
                        ? entityManager.GetComponentData<TilemapRendererData>(entity).MeshHash
                        : hash;
                    binding = new MosaicPreviewWorld.Binding(world, hash, renderHash, entity, entity);
                    return true;
                }

                return false;
            }

            var renderEntity = Entity.Null;
            for (var i = 0; i < entities.Length; i++)
            {
                if (guids[i].OriginatingEntityId == originatingId
                    && entityManager.HasBuffer<TilemapTerrainLayerElement>(entities[i]))
                {
                    renderEntity = entities[i];
                    break;
                }
            }

            if (renderEntity == Entity.Null) return false;
            var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(renderEntity);
            if (target.LayerIndex < 0 || target.LayerIndex >= layers.Length) return false;
            var layerHash = layers[target.LayerIndex].IntGridHash;

            var intGridEntity = Entity.Null;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (guids[i].OriginatingEntityId != originatingId
                    || !entityManager.HasComponent<IntGridData>(entity)
                    || entityManager.GetComponentData<IntGridData>(entity).Hash != layerHash)
                {
                    continue;
                }

                intGridEntity = entity;
                break;
            }

            if (intGridEntity == Entity.Null) return false;
            var terrainRenderHash = entityManager.HasComponent<TilemapRendererData>(renderEntity)
                ? entityManager.GetComponentData<TilemapRendererData>(renderEntity).MeshHash
                : layerHash;
            binding = new MosaicPreviewWorld.Binding(world, layerHash, terrainRenderHash, intGridEntity, renderEntity);
            return true;
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

        private void OnCellsChanged(MosaicPaintingTarget target, IReadOnlyCollection<Vector2Int> positions,
            short value)
        {
            if (_details && TryGetBinding(target, out var binding))
            {
                MosaicPreviewWorld.Apply(binding, positions, value);
                RequestPreviewUpdates();
            }

            SceneView.RepaintAll();
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

        private void OnPrefabStageChanged(PrefabStage stage)
        {
            QueueRefresh();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                RestoreRenderers();
                DisposePreview();
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

        private void DisposePreview()
        {
            _previewUpdatesRemaining = 0;
            _previewWorld?.Dispose();
        }

        private void RequestPreviewUpdates()
        {
            _previewUpdatesRemaining = 3;
        }

        private void BeforeAssemblyReload()
        {
            RestoreRenderers();
            DisposePreview();
        }

        private static bool BelongsToStage(Component component, StageHandle stage)
        {
            return component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded
                   && StageUtility.GetStageHandle(component.gameObject) == stage;
        }
    }
}
