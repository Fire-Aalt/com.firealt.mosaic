using System;
using System.Collections.Generic;
using FireAlt.Core;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    [InitializeOnLoad]
    internal static class MosaicPaintingController
    {
        public const int MIN_BRUSH_SIZE = 1;
        public const int MAX_BRUSH_SIZE = 10;

        private static readonly List<MosaicPaintingTarget> Targets = new();
        private static readonly List<MosaicPaintingTarget> Candidates = new();
        private static readonly List<LinkedTilemapLayers> LinkedComponents = new();
        private static readonly List<MosaicPaintingSelection> Selections = new();
        private static readonly HashSet<MosaicPaintingVisibilityTarget> VisibilityTargets = new();
        private static readonly HashSet<MosaicPaintingVisibilityTarget> NextVisibilityTargets = new();
        private static readonly List<MosaicPaintingVisibilityTarget> ContextPrefabVisibilityTargets = new();
        private static readonly Dictionary<Hash128, HashSet<Entity>> PendingSubSceneLoads = new();
        private static readonly List<Hash128> CompletedSubSceneLoads = new();
        private static readonly MosaicPaintingPreview Preview = new();
        private static readonly MosaicPaintingPreviewInvalidation Invalidation = new();

        private static StageHandle _stage;
        private static PrefabStage _prefabStage;
        private static bool _refreshQueued;
        private static bool _rebuildQueued;
        private static bool _showIntGridColors;
        private static bool _windowOpen;
        private static int _brushSize = MIN_BRUSH_SIZE;
        private static MosaicPaintingSelectionId? _selectedId;
        private static bool _previewUpdatePending;
        private static uint _previewWorldVersion;

        static MosaicPaintingController()
        {
            _stage = StageUtility.GetCurrentStageHandle();
            _prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoEvent += OnUndoRedo;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            AssemblyReloadEvents.beforeAssemblyReload += Preview.Dispose;
            QueueRefresh();
        }

        internal static event Action SnapshotChanged;

        internal static IReadOnlyList<MosaicPaintingTarget> CatalogTargets => Targets;

        internal static IReadOnlyList<LinkedTilemapLayers> CatalogLinkedComponents => LinkedComponents;

        internal static MosaicPaintingSelection Selection { get; private set; }

        internal static MosaicPaintingSelectionId? SelectedId => _selectedId;

        internal static MosaicPaintingTarget Target => Selection?.Anchor;

        internal static short Value => Selection?.PrimaryValue ?? 0;

        internal static Color Color => Selection?.Color ?? Color.white;

        internal static int BrushSize
        {
            get => _brushSize;
            set => _brushSize = Mathf.Clamp(value, MIN_BRUSH_SIZE, MAX_BRUSH_SIZE);
        }

        internal static int BrushRadius => _brushSize - 1;

        internal static bool IsPainting => Selection != null;

        internal static void Select(MosaicPaintingSelection selection)
        {
            if (selection == null || !selection.IsValid)
            {
                ClearSelection();
                return;
            }

            Selection = selection;
            _selectedId = selection.Id;
            if (UnityEditor.EditorTools.ToolManager.activeToolType != typeof(MosaicPaintingTool))
            {
                UnityEditor.EditorTools.ToolManager.SetActiveTool<MosaicPaintingTool>();
            }

            SnapshotChanged?.Invoke();
        }

        internal static void ClearSelection()
        {
            Selection = null;
            _selectedId = null;
            SnapshotChanged?.Invoke();
        }

        internal static void ResolveSelection(IReadOnlyList<MosaicPaintingSelection> selections)
        {
            if (!_selectedId.HasValue)
            {
                Selection = null;
                return;
            }

            foreach (var selection in selections)
            {
                if (selection.Id != _selectedId.Value || !selection.IsValid) continue;
                Selection = selection;
                return;
            }

            ClearSelection();
        }

        internal static void CloseWindow()
        {
            _windowOpen = false;
            StopPainting();
            Preview.Dispose();
            VisibilityTargets.Clear();
            ContextPrefabVisibilityTargets.Clear();
            QueueRefresh();
        }

        internal static void OpenWindow()
        {
            _windowOpen = true;
            QueueRefresh();
        }

        internal static void NotifyChanged()
        {
            SnapshotChanged?.Invoke();
        }

        internal static void NotifyCellsChanged(MosaicPaintingTarget target,
            IReadOnlyCollection<Vector2Int> positions, short value)
        {
            OnCellsChanged(target, positions, value);
        }

        private static void StopPainting()
        {
            Selection = null;
            _selectedId = null;
            if (UnityEditor.EditorTools.ToolManager.activeToolType == typeof(MosaicPaintingTool))
            {
                UnityEditor.EditorTools.ToolManager.RestorePreviousPersistentTool();
            }

            SnapshotChanged?.Invoke();
        }

        private static void QueueRefresh()
        {
            _refreshQueued = true;
            _rebuildQueued = true;
        }

        private static void QueueTargetRefresh()
        {
            _refreshQueued = true;
        }

        internal static void SetShowIntGridColors(IReadOnlyList<MosaicPaintingTarget> targets, bool showIntGridColors)
        {
            var visibilityChanged = false;
            var showChanged = _showIntGridColors != showIntGridColors;
            _showIntGridColors = showIntGridColors;
            NextVisibilityTargets.Clear();
            foreach (var target in targets)
            {
                if (target.IsValid) NextVisibilityTargets.Add(target.VisibilityTarget);
            }

            if (PendingSubSceneLoads.Count == 0)
            {
                visibilityChanged = !VisibilityTargets.SetEquals(NextVisibilityTargets);
                VisibilityTargets.Clear();
                VisibilityTargets.UnionWith(NextVisibilityTargets);
            }
            else
            {
                foreach (var target in NextVisibilityTargets)
                {
                    if (VisibilityTargets.Add(target)) visibilityChanged = true;
                }
            }

            if (!showIntGridColors && (showChanged || visibilityChanged))
            {
                foreach (var target in targets) Preview.Reseed(target);
                RequestPreviewUpdates();
            }

            Preview.SetVisibility(VisibilityTargets, ContextPrefabVisibilityTargets, showIntGridColors);
            SceneView.RepaintAll();
        }

        internal static void RandomizeRuleEngineSeed(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            if (MosaicPaintingPreview.RandomizeRuleEngineSeed(World.DefaultGameObjectInjectionWorld, targets) == 0)
            {
                return;
            }

            RequestPreviewUpdates();
            SceneView.RepaintAll();
        }

        private static void OnEditorUpdate()
        {
            UpdatePendingSubSceneLoads();

            var currentStage = StageUtility.GetCurrentStageHandle();
            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (!_stage.Equals(currentStage) || !ReferenceEquals(_prefabStage, currentPrefabStage))
            {
                _stage = currentStage;
                _prefabStage = currentPrefabStage;
                PendingSubSceneLoads.Clear();
                StopPainting();
                QueueRefresh();
            }

            if (_refreshQueued && !EditorApplication.isCompiling
                               && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Refresh();
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || !_previewUpdatePending) return;

            EditorApplication.QueuePlayerLoopUpdate();
            if (!HasPreviewWorldAdvanced()) return;

            if (!_showIntGridColors)
            {
                foreach (var target in Targets) Preview.Reseed(target);
            }

            Preview.SetVisibility(VisibilityTargets, ContextPrefabVisibilityTargets, _showIntGridColors);
            QueueTargetRefresh();
            SceneView.RepaintAll();
        }

        private static void Refresh()
        {
            var rebuild = _rebuildQueued;
            _refreshQueued = false;
            _rebuildQueued = false;
            Candidates.Clear();
            Candidates.AddRange(MosaicPaintingCatalog.DiscoverAuthoringCandidates(_stage));
            MosaicPaintingCatalog.DiscoverLinkedComponents(LinkedComponents, _stage);
            Invalidation.Reset(Candidates, _stage);
            RefreshContextPrefabVisibilityTargets();
            if (rebuild && _windowOpen) Preview.Rebuild(Candidates);
            var pendingBindings = MosaicPaintingCatalog.DiscoverTargets(Targets, _stage, Candidates);
            BuildSelections();
            ResolveSnapshotSelection();
            Preview.SetVisibility(VisibilityTargets, ContextPrefabVisibilityTargets, _showIntGridColors);
            TrackClosedSubSceneLoads();
            _previewUpdatePending = _windowOpen && (rebuild || pendingBindings)
                                    || PendingSubSceneLoads.Count != 0;
            if (_previewUpdatePending) RequestPreviewUpdates();
            SnapshotChanged?.Invoke();
        }

        private static void RefreshContextPrefabVisibilityTargets()
        {
            ContextPrefabVisibilityTargets.Clear();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null || prefabStage.mode == PrefabStage.Mode.InIsolation) return;

            var instance = prefabStage.openedFromInstanceObject;
            if (instance == null || !instance.scene.isSubScene) return;

            foreach (var tilemap in instance.GetComponentsInChildren<TilemapAuthoring>(true))
            {
                AddContextPrefabVisibilityTarget(new MosaicPaintingTarget(tilemap));
            }

            foreach (var terrain in instance.GetComponentsInChildren<TilemapTerrainAuthoring>(true))
            {
                for (var i = 0; i < terrain.intGridLayers.Count; i++)
                {
                    AddContextPrefabVisibilityTarget(new MosaicPaintingTarget(terrain, terrain.intGridLayers[i], i));
                }
            }
        }

        private static void AddContextPrefabVisibilityTarget(MosaicPaintingTarget target)
        {
            if (!MosaicPaintingCatalog.TryFindBinding(target.Id, out var binding)) return;
            ContextPrefabVisibilityTargets.Add(new MosaicPaintingVisibilityTarget(binding, target.GameObjectSourceId));
        }

        private static void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            for (var i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                        if (Invalidation.IsRelevant(created.entityId)) QueueRefresh();
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy);
                        if (Invalidation.IsRelevant(hierarchy.entityId)) QueueRefresh();
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure);
                        if (Invalidation.IsRelevant(structure.entityId)) QueueRefresh();
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        if (Invalidation.IsRelevant(parent.entityId)
                            || Invalidation.IsRelevant(parent.newParentEntityId))
                        {
                            QueueRefresh();
                        }

                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var children);
                        if (Invalidation.IsRelevant(children.entityId)) QueueRefresh();
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        if (Invalidation.IsRelevant(destroyed.entityId)) QueueRefresh();
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (var entityId in prefabs.entityIds)
                        {
                            if (!Invalidation.IsRelevant(entityId)) continue;
                            QueueRefresh();
                            break;
                        }

                        break;
                }

                if (_refreshQueued) return;
            }
        }

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] modifications)
        {
            foreach (var modification in modifications)
            {
                var property = modification.currentValue;
                var target = property.target;
                if (target is LinkedTilemapLayers
                    || target is GameObject gameObject && gameObject.GetComponent<LinkedTilemapLayers>() != null)
                {
                    QueueTargetRefresh();
                    break;
                }

                if (target is Transform || !Invalidation.IsRelevant(target)) continue;
                if (MosaicPaintingTarget.IsPaintedCellProperty(target, property.propertyPath)) continue;

                if (target is GameObject or GridAuthoring or TilemapAuthoring or TilemapTerrainAuthoring)
                {
                    QueueRefresh();
                    break;
                }
            }

            return modifications;
        }

        private static void OnUndoRedo(in UndoRedoInfo _)
        {
            QueueRefresh();
        }

        private static void BuildSelections()
        {
            Selections.Clear();
            var tilemapTargets = new Dictionary<TilemapAuthoring, MosaicPaintingTarget>();
            foreach (var target in Targets)
            {
                if (target.Owner is TilemapAuthoring tilemap) tilemapTargets[tilemap] = target;
                if (!target.HasLoadedAuthoringScene) continue;
                foreach (var value in target.Values)
                {
                    Selections.Add(MosaicPaintingSelection.Create(target, value, _stage));
                }
            }

            foreach (var linked in LinkedComponents)
            {
                if (linked.layers == null) continue;
                for (var i = 0; i < linked.layers.Count; i++)
                {
                    var selection = MosaicPaintingSelection.Create(linked, i, tilemapTargets, _stage);
                    if (selection.ValidationMessage?.Contains("loaded and paintable") != true)
                    {
                        Selections.Add(selection);
                    }
                }
            }
        }

        private static void ResolveSnapshotSelection()
        {
            if (!_selectedId.HasValue)
            {
                Selection = null;
                return;
            }

            foreach (var selection in Selections)
            {
                if (selection.Id != _selectedId.Value || !selection.IsValid) continue;
                Selection = selection;
                return;
            }

            Selection = null;
            _selectedId = null;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (!scene.isSubScene)
            {
                QueueRefresh();
                return;
            }

            foreach (var subScene in Resources.FindObjectsOfTypeAll<SubScene>())
            {
                if (AssetDatabase.GetAssetPath(subScene.SceneAsset) == scene.path)
                {
                    PendingSubSceneLoads.Remove(subScene.SceneGUID);
                }
            }

            QueueTargetRefresh();
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (!scene.isSubScene) return;

            foreach (var subScene in Resources.FindObjectsOfTypeAll<SubScene>())
            {
                if (!subScene.isActiveAndEnabled || !subScene.AutoLoadScene
                    || AssetDatabase.GetAssetPath(subScene.SceneAsset) != scene.path)
                {
                    continue;
                }

                PendingSubSceneLoads[subScene.SceneGUID] = CollectMosaicRenderers(subScene.SceneGUID);
                return;
            }
        }

        private static void OnSceneClosed(Scene scene)
        {
            if (!scene.isSubScene)
            {
                QueueRefresh();
                return;
            }

            foreach (var subScene in Resources.FindObjectsOfTypeAll<SubScene>())
            {
                if (!subScene.isActiveAndEnabled || !subScene.AutoLoadScene
                    || AssetDatabase.GetAssetPath(subScene.SceneAsset) != scene.path)
                {
                    continue;
                }

                if (!PendingSubSceneLoads.ContainsKey(subScene.SceneGUID))
                {
                    PendingSubSceneLoads.Add(subScene.SceneGUID, CollectMosaicRenderers(subScene.SceneGUID));
                }
                QueueTargetRefresh();
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            QueueTargetRefresh();
        }

        private static void OnCellsChanged(MosaicPaintingTarget target,
            IReadOnlyCollection<Vector2Int> positions, short value)
        {
            if (_showIntGridColors) return;
            if (!target.IsEntityTarget) MosaicPaintingPreview.Apply(target, positions, value);
            RequestPreviewUpdates();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                Preview.Dispose();
                PendingSubSceneLoads.Clear();
                _previewUpdatePending = false;
                ClearSelection();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                QueueRefresh();
            }
        }

        private static void RequestPreviewUpdates()
        {
            _previewUpdatePending = true;
            var world = World.DefaultGameObjectInjectionWorld;
            _previewWorldVersion = world != null && world.IsCreated
                ? world.EntityManager.GlobalSystemVersion
                : 0;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static bool HasPreviewWorldAdvanced()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;

            var version = world.EntityManager.GlobalSystemVersion;
            if (version == _previewWorldVersion) return false;

            _previewWorldVersion = version;
            return true;
        }

        private static void UpdatePendingSubSceneLoads()
        {
            if (PendingSubSceneLoads.Count == 0) return;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;

            CompletedSubSceneLoads.Clear();
            foreach (var pending in PendingSubSceneLoads)
            {
                var sceneGuid = pending.Key;
                var sceneEntity = SceneSystem.GetSceneEntity(world.Unmanaged, sceneGuid);
                var waitingForReplacement = pending.Value != null && pending.Value.Count != 0;
                // Entity-scene renderers initialize over several frames. Keep the outgoing snapshot until
                // every replacement renderer and all of its IntGrid layers are registered and usable.
                if (!world.EntityManager.Exists(sceneEntity)
                    || !SceneSystem.IsSceneLoaded(world.Unmanaged, sceneEntity)
                    || HasOutgoingRenderers(world.EntityManager, sceneGuid, pending.Value)
                    || waitingForReplacement
                    && !HasReadyMosaicRenderers(world, sceneGuid, pending.Value.Count))
                {
                    continue;
                }

                CompletedSubSceneLoads.Add(sceneGuid);
            }

            foreach (var sceneGuid in CompletedSubSceneLoads) PendingSubSceneLoads.Remove(sceneGuid);
            if (CompletedSubSceneLoads.Count != 0) QueueTargetRefresh();
            if (PendingSubSceneLoads.Count != 0) EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void TrackClosedSubSceneLoads()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) return;

            var world = World.DefaultGameObjectInjectionWorld;
            foreach (var subScene in SubScene.AllSubScenes)
            {
                if (subScene == null || !subScene.isActiveAndEnabled || !subScene.AutoLoadScene || subScene.IsLoaded
                    || subScene.SceneGUID == default)
                {
                    continue;
                }

                if (world != null && world.IsCreated)
                {
                    var sceneEntity = SceneSystem.GetSceneEntity(world.Unmanaged, subScene.SceneGUID);
                    if (world.EntityManager.Exists(sceneEntity)
                        && SceneSystem.IsSceneLoaded(world.Unmanaged, sceneEntity))
                    {
                        continue;
                    }
                }

                if (!PendingSubSceneLoads.ContainsKey(subScene.SceneGUID))
                {
                    PendingSubSceneLoads.Add(subScene.SceneGUID, null);
                }
            }
        }

        private static HashSet<Entity> CollectMosaicRenderers(Hash128 sceneGuid)
        {
            var result = new HashSet<Entity>();
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return result;

            var entityManager = world.EntityManager;
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData, SceneSection>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in query.ToEntityArray(Allocator.Temp))
            {
                if (entityManager.GetSharedComponent<SceneSection>(entity).SceneGUID == sceneGuid)
                {
                    result.Add(entity);
                }
            }

            return result;
        }

        private static bool HasOutgoingRenderers(EntityManager entityManager, Hash128 sceneGuid,
            IReadOnlyCollection<Entity> outgoingRenderers)
        {
            if (outgoingRenderers == null) return false;
            foreach (var entity in outgoingRenderers)
            {
                if (!entityManager.Exists(entity) || MosaicInitializationSystem.IsStaleSceneEntity(entityManager, entity)
                                                  || !entityManager.HasComponent<SceneSection>(entity))
                {
                    continue;
                }

                if (entityManager.GetSharedComponent<SceneSection>(entity).SceneGUID == sceneGuid) return true;
            }

            return false;
        }

        private static bool HasReadyMosaicRenderers(World world, Hash128 sceneGuid, int requiredCount)
        {
            var entityManager = world.EntityManager;
            if (!entityManager.TryGetUnmanagedSingleton<TilemapIntGridSingleton>(out var singleton)) return false;

            var readyCount = 0;
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData, SceneSection, LocalToWorld, MosaicRendererInitialized>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in query.ToEntityArray(Allocator.Temp))
            {
                if (MosaicInitializationSystem.IsStaleSceneEntity(entityManager, entity)
                    || entityManager.GetSharedComponent<SceneSection>(entity).SceneGUID != sceneGuid
                    || !entityManager.IsComponentEnabled<MosaicRendererInitialized>(entity))
                {
                    continue;
                }

                if (entityManager.HasBuffer<TilemapTerrainLayerElement>(entity))
                {
                    var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(entity, true);
                    var ready = !layers.IsEmpty;
                    foreach (var layer in layers)
                    {
                        ready &= MosaicPaintingCatalog.TryCreateBinding(
                            world, layer.IntGridEntity, entity, singleton, out _);
                    }

                    if (!ready) continue;
                }
                else if (!MosaicPaintingCatalog.TryCreateBinding(world, entity, entity, singleton, out _))
                {
                    continue;
                }

                readyCount++;
                if (readyCount >= requiredCount) return true;
            }

            return false;
        }

        internal static bool BelongsToStage(Component component, StageHandle stage)
        {
            return component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded
                   && StageUtility.GetStageHandle(component.gameObject) == stage;
        }

        internal static bool IsAllowedAuthoringLocation(Component component, StageHandle stage)
        {
            if (!BelongsToStage(component, stage)) return false;

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null) return component.gameObject.scene.isSubScene;
            if (prefabStage.mode == PrefabStage.Mode.InIsolation) return true;

            var contextObject = prefabStage.openedFromInstanceObject;
            return contextObject != null && contextObject.scene.isSubScene;
        }
    }
}
