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
                        SetSceneCullingMaskForHierarchy(entityManager, rendererEntities[i], mask);
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
                        SetSceneCullingMaskForHierarchy(entityManager, weightedEntity.Value, mask);
                    }
                }

                if (!hasLayers || !layers.IntGridLayers.TryGetValue(target.IntGridHash, out var layer)) continue;
                foreach (var spawnedEntity in layer.SpawnedEntities)
                {
                    SetSceneCullingMaskForHierarchy(entityManager, spawnedEntity.Value, mask);
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

        private static void SetSceneCullingMaskForHierarchy(EntityManager entityManager, Entity hierarchyRoot, ulong mask)
        {
            if (!entityManager.Exists(hierarchyRoot)) return;

            InternalEditorRenderData.SetSceneCullingMask(entityManager, hierarchyRoot, mask);
            if (entityManager.TryGetBuffer<LinkedEntityGroup>(hierarchyRoot, out var leg))
            {
                foreach (var linkedEntity in leg.AsNativeArray())
                {
                    if (linkedEntity.Value != hierarchyRoot && entityManager.Exists(linkedEntity.Value))
                    {
                        InternalEditorRenderData.SetSceneCullingMask(entityManager, linkedEntity.Value, mask);
                    }
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
