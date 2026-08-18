using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FireAlt.Mosaic.Editor
{
    [InitializeOnLoad]
    internal static class MosaicPaintingPreviewService
    {
        private const int PREVIEW_UPDATE_FRAMES = 4;

        private static readonly List<MosaicPaintingTarget> Targets = new();
        private static readonly MosaicPaintingPreview Preview = new();
        private static readonly MosaicPaintingPreviewInvalidation Invalidation = new();

        private static StageHandle _stage;
        private static bool _refreshQueued;
        private static bool _showIntGridColors;
        private static int _previewUpdatesRemaining;
        private static uint _previewWorldVersion;

        static MosaicPaintingPreviewService()
        {
            _stage = StageUtility.GetCurrentStageHandle();
            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += QueueRefresh;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            AssemblyReloadEvents.beforeAssemblyReload += Preview.Dispose;
            MosaicPaintingSession.CellsChanged += OnCellsChanged;
            QueueRefresh();
        }

        internal static event Action Refreshed;

        private static void QueueRefresh()
        {
            _refreshQueued = true;
        }

        internal static void SetShowIntGridColors(IReadOnlyList<MosaicPaintingTarget> targets, bool showIntGridColors)
        {
            _showIntGridColors = showIntGridColors;
            if (!showIntGridColors)
            {
                foreach (var target in targets) Preview.Reseed(target);
                RequestPreviewUpdates();
            }

            Preview.SetVisibility(targets, showIntGridColors);
            SceneView.RepaintAll();
        }

        internal static void AddAuthoringTargets(List<MosaicPaintingTarget> targets, StageHandle stage)
        {
            foreach (var tilemap in Resources.FindObjectsOfTypeAll<TilemapAuthoring>())
            {
                if (!BelongsToStage(tilemap, stage) || tilemap.gameObject.scene.isSubScene) continue;
                targets.Add(new MosaicPaintingTarget(tilemap));
            }

            foreach (var terrain in Resources.FindObjectsOfTypeAll<TilemapTerrainAuthoring>())
            {
                if (!BelongsToStage(terrain, stage) || terrain.gameObject.scene.isSubScene) continue;
                for (var i = 0; i < terrain.intGridLayers.Count; i++)
                {
                    targets.Add(new MosaicPaintingTarget(terrain, terrain.intGridLayers[i], i));
                }
            }
        }

        private static void OnEditorUpdate()
        {
            var currentStage = StageUtility.GetCurrentStageHandle();
            if (!_stage.Equals(currentStage))
            {
                _stage = currentStage;
                _refreshQueued = true;
            }

            if (_refreshQueued && !EditorApplication.isCompiling
                               && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Refresh();
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode || _previewUpdatesRemaining == 0) return;

            EditorApplication.QueuePlayerLoopUpdate();
            if (!HasPreviewWorldAdvanced()) return;

            if (!_showIntGridColors)
            {
                foreach (var target in Targets) Preview.Reseed(target);
            }

            Preview.SetVisibility(Targets, _showIntGridColors);
            _previewUpdatesRemaining--;
            SceneView.RepaintAll();
        }

        private static void Refresh()
        {
            _refreshQueued = false;
            Targets.Clear();
            AddAuthoringTargets(Targets, _stage);
            Invalidation.Reset(Targets);
            Preview.Rebuild(Targets);
            Preview.SetVisibility(Targets, _showIntGridColors);
            RequestPreviewUpdates();
            Refreshed?.Invoke();
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

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            QueueRefresh();
        }

        private static void OnSceneClosed(Scene scene)
        {
            QueueRefresh();
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
                _previewUpdatesRemaining = 0;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                QueueRefresh();
            }
        }

        private static void RequestPreviewUpdates()
        {
            _previewUpdatesRemaining = PREVIEW_UPDATE_FRAMES;
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

        private static bool BelongsToStage(Component component, StageHandle stage)
        {
            return component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded
                   && StageUtility.GetStageHandle(component.gameObject) == stage;
        }
    }
}