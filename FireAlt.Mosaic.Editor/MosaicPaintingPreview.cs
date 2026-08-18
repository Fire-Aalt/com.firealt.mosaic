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
        private readonly Dictionary<Entity, CullingState> _hiddenEntities = new();
        private readonly HashSet<Entity> _entitiesToHide = new();
        private readonly List<Entity> _entitiesToRestore = new();
        private World _world;
        private World _visibilityWorld;

        private readonly struct CullingState
        {
            public CullingState(bool hadSceneCullingMask, ulong sceneCullingMask)
            {
                HadSceneCullingMask = hadSceneCullingMask;
                SceneCullingMask = sceneCullingMask;
            }

            public bool HadSceneCullingMask { get; }

            public ulong SceneCullingMask { get; }
        }

        public void Rebuild(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            Rebuild(World.DefaultGameObjectInjectionWorld, targets);
        }

        internal void Rebuild(World world, IReadOnlyList<MosaicPaintingTarget> targets)
        {
            DisposeInjectedEntities(world);

            if (world == null) return;

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

            if (_visibilityWorld != null && _visibilityWorld != world) RestoreHiddenEntities();
            _visibilityWorld = world;
            if (showIntGridColors) _entitiesToHide.Clear();
            else RestoreHiddenEntities();

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

                for (var i = 0; i < rendererEntities.Length; i++)
                {
                    if (rendererData[i].MeshHash == target.RendererHash)
                    {
                        SetHierarchyVisibility(entityManager, rendererEntities[i], showIntGridColors);
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
                        SetHierarchyVisibility(entityManager, weightedEntity.Value, showIntGridColors);
                    }
                }

                if (!hasLayers || !layers.IntGridLayers.TryGetValue(target.IntGridHash, out var layer)) continue;
                foreach (var spawnedEntity in layer.SpawnedEntities)
                {
                    SetHierarchyVisibility(entityManager, spawnedEntity.Value, showIntGridColors);
                }
            }

            if (!showIntGridColors) return;

            _entitiesToRestore.Clear();
            foreach (var hiddenEntity in _hiddenEntities.Keys)
            {
                if (!_entitiesToHide.Contains(hiddenEntity)) _entitiesToRestore.Add(hiddenEntity);
            }

            foreach (var entity in _entitiesToRestore)
            {
                RestoreHiddenEntity(entityManager, entity, _hiddenEntities[entity]);
                _hiddenEntities.Remove(entity);
            }

            foreach (var entity in _entitiesToHide) HideEntity(entityManager, entity);
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

        internal static uint RandomizeRuleEngineSeed(World world, IReadOnlyList<MosaicPaintingTarget> targets)
        {
            if (world == null || !world.IsCreated) return 0;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            if (!entityManager.TryGetUnmanagedSingleton<TilemapCommandBufferSingleton>(out var commandBuffer))
            {
                return 0;
            }

            var currentSeed = commandBuffer.GlobalSeed.Value;
            var seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            if (seed == currentSeed) seed = seed == int.MaxValue - 1 ? 1u : seed + 1u;
            commandBuffer.SetGlobalSeed(seed);

            foreach (var target in targets) Reseed(world, target);
            return seed;
        }

        public static void Apply(MosaicPaintingTarget target, IReadOnlyCollection<Vector2Int> positions, short value)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null) Apply(world, target, positions, value);
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
            RestoreHiddenEntities();
            DisposeInjectedEntities(_world ?? World.DefaultGameObjectInjectionWorld);
        }

        private static bool TryGetLayers(World world, 
            out NativeHashMap<Hash128, TilemapIntGridSingleton.IntGridLayer> layers)
        {
            layers = default;
            if (world == null) return false;

            if (!world.EntityManager.TryGetUnmanagedSingleton<TilemapIntGridSingleton>(out var singleton))
            {
                return false;
            }
            
            layers = singleton.IntGridLayers;
            return true;
        }

        private void SetHierarchyVisibility(EntityManager entityManager, Entity hierarchyRoot, bool hidden)
        {
            if (!entityManager.Exists(hierarchyRoot)) return;

            SetEntityVisibility(entityManager, hierarchyRoot, hidden);
            if (entityManager.TryGetBuffer<LinkedEntityGroup>(hierarchyRoot, out var leg))
            {
                foreach (var linkedEntity in leg.AsNativeArray())
                {
                    if (linkedEntity.Value != hierarchyRoot && entityManager.Exists(linkedEntity.Value))
                    {
                        SetEntityVisibility(entityManager, linkedEntity.Value, hidden);
                    }
                }
            }
        }

        private void SetEntityVisibility(EntityManager entityManager, Entity entity, bool hidden)
        {
            if (hidden)
            {
                _entitiesToHide.Add(entity);
            }
            else if (entityManager.HasComponent<SceneSection>(entity)
                     && InternalEditorRenderData.GetSceneCullingMask(entityManager, entity) == 0)
            {
                InternalEditorRenderData.SetSceneCullingMask(entityManager, entity,
                    EditorSceneManager.DefaultSceneCullingMask | (1UL << 59));
            }
        }

        private void HideEntity(EntityManager entityManager, Entity entity)
        {
            if (!_hiddenEntities.ContainsKey(entity))
            {
                var hadSceneCullingMask = InternalEditorRenderData.HasSceneCullingMask(entityManager, entity);
                var sceneCullingMask = InternalEditorRenderData.GetSceneCullingMask(entityManager, entity);
                if (sceneCullingMask == 0 && entityManager.HasComponent<SceneSection>(entity))
                {
                    hadSceneCullingMask = true;
                    sceneCullingMask = EditorSceneManager.DefaultSceneCullingMask | (1UL << 59);
                }

                _hiddenEntities.Add(entity, new CullingState(hadSceneCullingMask, sceneCullingMask));
            }

            InternalEditorRenderData.SetSceneCullingMask(entityManager, entity, 0);
        }

        private void RestoreHiddenEntities()
        {
            if (_visibilityWorld != null && _visibilityWorld.IsCreated)
            {
                var entityManager = _visibilityWorld.EntityManager;
                entityManager.CompleteAllTrackedJobs();
                foreach (var hiddenEntity in _hiddenEntities)
                {
                    RestoreHiddenEntity(entityManager, hiddenEntity.Key, hiddenEntity.Value);
                }
            }

            _hiddenEntities.Clear();
            _entitiesToHide.Clear();
            _entitiesToRestore.Clear();
            _visibilityWorld = null;
        }

        private static void RestoreHiddenEntity(EntityManager entityManager, Entity entity, CullingState state)
        {
            if (!entityManager.Exists(entity)) return;

            if (state.HadSceneCullingMask)
            {
                InternalEditorRenderData.SetSceneCullingMask(entityManager, entity, state.SceneCullingMask);
            }
            else
            {
                InternalEditorRenderData.RemoveSceneCullingMask(entityManager, entity);
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

                var source = EditorUtility.EntityIdToObject(
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
                var source = EditorUtility.EntityIdToObject(
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
            if (world != null)
            {
                var entityManager = world.EntityManager;
                var query = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<MosaicPaintingPreviewEntity>()
                    .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                    .Build(entityManager);
                entityManager.DestroyEntity(query);
            }

            if (_world == world) _world = null;
        }
    }
}
