using System;
using System.Collections.Generic;
using System.Linq;
using FireAlt.Core;
using FireAlt.Core.Editor;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal struct MosaicPaintingPreviewEntity : IComponentData
    {
    }

    internal sealed class MosaicPaintingPreview : IDisposable
    {
        private World _world;

        public void Rebuild(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            Rebuild(World.DefaultGameObjectInjectionWorld, targets);
        }

        internal void Rebuild(World world, IReadOnlyList<MosaicPaintingTarget> targets)
        {
            DisposeInjectedEntities(world);

            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.Editor) == 0) return;

            var roots = targets.Where(target => target.IsValid && !target.IsSubScene)
                .Select(target => target.Grid.gameObject).Distinct().ToArray();
            if (roots.Length == 0) return;

            DisposeLegacyEntities(world.EntityManager, roots);
            var entities = EditorBakingWorld.BakeInto(roots, world);
            var entityManager = world.EntityManager;
            foreach (var entity in entities)
            {
                entityManager.AddComponent<MosaicPaintingPreviewEntity>(entity);
            }

            AddTransformSync(entityManager, entities, targets);
            _world = world;
        }

        public void SetVisibility(IReadOnlyList<MosaicPaintingTarget> targets, bool showIntGridColors)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.Editor) == 0) return;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();

            var rendererQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TilemapRendererData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            var rendererEntities = rendererQuery.ToEntityArray(Allocator.Temp);
            var rendererData = rendererQuery.ToComponentDataArray<TilemapRendererData>(Allocator.Temp);

            var intGridQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            var intGridEntities = intGridQuery.ToEntityArray(Allocator.Temp);
            var intGridData = intGridQuery.ToComponentDataArray<IntGridData>(Allocator.Temp);

            var layerQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<TilemapIntGridSingleton>());
            var hasLayers = !layerQuery.IsEmpty;
            var layers = hasLayers ? layerQuery.GetSingleton<TilemapIntGridSingleton>() : default;

            foreach (var target in targets)
            {
                if (!target.IsValid) continue;
                var mask = showIntGridColors ? 0 : target.SceneCullingMask;

                for (var i = 0; i < rendererEntities.Length; i++)
                {
                    if (rendererData[i].MeshHash == target.RendererHash)
                    {
                        SetEntityAndLinkedGroup(entityManager, rendererEntities[i], mask);
                    }
                }

                for (var i = 0; i < intGridEntities.Length; i++)
                {
                    if (intGridData[i].Hash != target.IntGridHash
                        || !entityManager.HasBuffer<WeightedEntityElement>(intGridEntities[i]))
                    {
                        continue;
                    }

                    var weightedEntities = entityManager.GetBuffer<WeightedEntityElement>(intGridEntities[i])
                        .ToNativeArray(Allocator.Temp);
                    foreach (var weightedEntity in weightedEntities)
                    {
                        SetEntityAndLinkedGroup(entityManager, weightedEntity.Value, mask);
                    }
                }

                if (!hasLayers || !layers.IntGridLayers.TryGetValue(target.IntGridHash, out var layer)) continue;
                foreach (var spawnedEntity in layer.SpawnedEntities)
                {
                    SetEntityAndLinkedGroup(entityManager, spawnedEntity.Value, mask);
                }
            }
        }

        public void Reseed(MosaicPaintingTarget target)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated) Reseed(world, target);
        }

        internal static void Reseed(World world, MosaicPaintingTarget target)
        {
            if (!TryGetLayers(world, out var layers) || !layers.ContainsKey(target.IntGridHash)) return;
            ref var layer = ref layers.GetValueAsRef(target.IntGridHash);

            var previousPositions = new NativeList<int2>(layer.IntGrid.Count, Allocator.Temp);
            foreach (var cell in layer.IntGrid) previousPositions.Add(cell.Key);
            foreach (var position in previousPositions) layer.SetValue(position, 0);
            foreach (var cell in target.Cells)
            {
                layer.SetValue(new int2(cell.Position.x, cell.Position.y), cell.Value);
            }
        }

        public static void Apply(MosaicPaintingTarget target, IReadOnlyCollection<Vector2Int> positions, short value)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated) Apply(world, target, positions, value);
        }

        internal static void Apply(World world, MosaicPaintingTarget target,
            IReadOnlyCollection<Vector2Int> positions, short value)
        {
            if (!TryGetLayers(world, out var layers) || !layers.ContainsKey(target.IntGridHash)) return;
            ref var layer = ref layers.GetValueAsRef(target.IntGridHash);

            foreach (var position in positions)
            {
                layer.SetValue(new int2(position.x, position.y), value);
            }
        }

        public void Dispose()
        {
            DisposeInjectedEntities(_world ?? World.DefaultGameObjectInjectionWorld);
        }

        private static bool TryGetLayers(World world,
            out NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> layers)
        {
            layers = default;
            if (world == null || !world.IsCreated) return false;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<TilemapIntGridSingleton>());
            if (query.IsEmpty) return false;

            layers = query.GetSingleton<TilemapIntGridSingleton>().IntGridLayers;
            return true;
        }

        private static void SetEntityAndLinkedGroup(EntityManager entityManager, Entity entity, ulong mask)
        {
            if (!entityManager.Exists(entity)) return;

            InternalEditorRenderData.Set(entityManager, entity, mask);
            if (!entityManager.HasBuffer<LinkedEntityGroup>(entity)) return;

            var linkedEntities = entityManager.GetBuffer<LinkedEntityGroup>(entity).ToNativeArray(Allocator.Temp);
            foreach (var linkedEntity in linkedEntities)
            {
                if (linkedEntity.Value != entity && entityManager.Exists(linkedEntity.Value))
                {
                    InternalEditorRenderData.Set(entityManager, linkedEntity.Value, mask);
                }
            }
        }

        private static void AddTransformSync(EntityManager entityManager, Entity[] entities,
            IReadOnlyList<MosaicPaintingTarget> targets)
        {
            foreach (var target in targets)
            {
                if (!target.IsValid || target.IsSubScene) continue;

                foreach (var entity in entities)
                {
                    if (entityManager.HasComponent<TilemapRendererData>(entity)
                        && entityManager.GetComponentData<TilemapRendererData>(entity).MeshHash == target.RendererHash
                        && !entityManager.HasComponent<HybridEntitySync>(entity))
                    {
                        entityManager.AddComponentData(entity, new HybridEntitySync(target.Owner));
                        break;
                    }
                }
            }

            foreach (var entity in entities)
            {
                if (!entityManager.HasComponent<GridData>(entity)
                    || !entityManager.HasComponent<EntityGuid>(entity))
                {
                    continue;
                }

                var source = UnityEditor.EditorUtility.EntityIdToObject(
                    entityManager.GetComponentData<EntityGuid>(entity).OriginatingEntityId) as GameObject;
                var grid = source == null ? null : source.GetComponent<GridAuthoring>();
                if (grid != null && !entityManager.HasComponent<HybridEntitySync>(entity))
                {
                    entityManager.AddComponentData(entity, new HybridEntitySync(grid));
                }
            }
        }

        private static void DisposeLegacyEntities(EntityManager entityManager, IReadOnlyList<GameObject> roots)
        {
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<EntityGuid>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(entityManager);
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (!entityManager.Exists(entity)) continue;
                var source = UnityEditor.EditorUtility.EntityIdToObject(
                    entityManager.GetComponentData<EntityGuid>(entity).OriginatingEntityId) as GameObject;
                if (source == null) continue;

                foreach (var root in roots)
                {
                    if (source == root || source.transform.IsChildOf(root.transform))
                    {
                        entityManager.DestroyEntity(entity);
                        break;
                    }
                }
            }
        }

        private void DisposeInjectedEntities(World world)
        {
            if (world != null && world.IsCreated)
            {
                var entityManager = world.EntityManager;
                entityManager.CompleteAllTrackedJobs();
                var query = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<MosaicPaintingPreviewEntity>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                    .Build(entityManager);
                entityManager.DestroyEntity(query);
            }

            if (_world == world) _world = null;
        }
    }

    [InitializeOnLoad]
    internal static class MosaicPaintingPreviewService
    {
        private const int PREVIEW_UPDATE_FRAMES = 4;

        private static readonly List<MosaicPaintingTarget> Targets = new();
        private static readonly MosaicPaintingPreview Preview = new();

        private static StageHandle _stage;
        private static bool _refreshQueued;
        private static bool _showIntGridColors;
        private static int _previewUpdatesRemaining;
        private static uint _previewWorldVersion;

        static MosaicPaintingPreviewService()
        {
            _stage = StageUtility.GetCurrentStageHandle();
            EditorApplication.projectChanged += QueueRefresh;
            EditorApplication.hierarchyChanged += QueueRefresh;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Preview.Dispose;
            MosaicPaintingSession.CellsChanged += OnCellsChanged;
            QueueRefresh();
        }

        internal static void QueueRefresh()
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
            Preview.Rebuild(Targets);
            Preview.SetVisibility(Targets, _showIntGridColors);
            RequestPreviewUpdates();
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
