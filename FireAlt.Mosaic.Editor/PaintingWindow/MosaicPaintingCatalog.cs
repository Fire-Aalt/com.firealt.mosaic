using System;
using System.Collections.Generic;
using FireAlt.Core;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal static class MosaicPaintingCatalog
    {
        internal static List<MosaicPaintingTarget> DiscoverAuthoringCandidates(StageHandle stage)
        {
            var targets = new List<MosaicPaintingTarget>();
            foreach (var tilemap in Resources.FindObjectsOfTypeAll<TilemapAuthoring>())
            {
                if (MosaicPaintingController.IsAllowedAuthoringLocation(tilemap, stage))
                {
                    targets.Add(new MosaicPaintingTarget(tilemap));
                }
            }

            foreach (var terrain in Resources.FindObjectsOfTypeAll<TilemapTerrainAuthoring>())
            {
                if (!MosaicPaintingController.IsAllowedAuthoringLocation(terrain, stage)) continue;
                for (var i = 0; i < terrain.intGridLayers.Count; i++)
                {
                    targets.Add(new MosaicPaintingTarget(terrain, terrain.intGridLayers[i], i));
                }
            }

            return targets;
        }

        internal static void DiscoverLinkedComponents(List<LinkedTilemapLayers> components, StageHandle stage)
        {
            components.Clear();
            foreach (var component in Resources.FindObjectsOfTypeAll<LinkedTilemapLayers>())
            {
                if (MosaicPaintingController.IsAllowedAuthoringLocation(component, stage)) components.Add(component);
            }

            components.Sort((left, right) =>
            {
                var comparison = string.CompareOrdinal(left.gameObject.name, right.gameObject.name);
                return comparison != 0 ? comparison : left.gameObject.GetEntityId().GetHashCode()
                    .CompareTo(right.gameObject.GetEntityId().GetHashCode());
            });
        }

        internal static bool DiscoverTargets(List<MosaicPaintingTarget> targets, StageHandle stage,
            IReadOnlyList<MosaicPaintingTarget> candidates = null)
        {
            candidates ??= DiscoverAuthoringCandidates(stage);
            var world = World.DefaultGameObjectInjectionWorld;
            return DiscoverTargets(targets, candidates, world, PrefabStageUtility.GetCurrentPrefabStage() == null,
                GetSubSceneGuids(stage));
        }

        internal static bool DiscoverTargets(List<MosaicPaintingTarget> targets,
            IReadOnlyList<MosaicPaintingTarget> candidates, World world, bool includeAllSceneEntities,
            HashSet<Hash128> subSceneGuids)
        {
            targets.Clear();

            var loadedSources = new HashSet<EntityId>();
            foreach (var candidate in candidates) loadedSources.Add(candidate.GameObjectSourceId);

            var bindings = new Dictionary<MosaicPaintingTargetId, MosaicPaintingRuntimeBinding>();
            // Closed entity-scene data strips EntityGuid. Those entities cannot bind back to authoring,
            // but their exact runtime bindings are still valid visualization and window-control targets.
            var anonymousTargets = new List<MosaicPaintingTarget>();
            if (world != null && world.IsCreated)
            {
                DiscoverBindings(world, includeAllSceneEntities, subSceneGuids, loadedSources,
                    bindings, anonymousTargets);
            }

            var pending = false;
            foreach (var candidate in candidates)
            {
                if (!candidate.Owner.isActiveAndEnabled || !candidate.IsValid) continue;
                if (!TryGetBinding(candidate, bindings, out var binding))
                {
                    pending = true;
                    continue;
                }

                var target = candidate.Owner switch
                {
                    TilemapAuthoring tilemap => new MosaicPaintingTarget(tilemap, binding),
                    TilemapTerrainAuthoring terrain => new MosaicPaintingTarget(
                        terrain, candidate.IntGrid, candidate.LayerIndex, binding),
                    _ => null,
                };
                if (target != null && target.IsValid) targets.Add(target);
            }

            foreach (var binding in bindings)
            {
                if (loadedSources.Contains(binding.Key.GameObjectId)) continue;
                targets.Add(CreateEntityTarget(binding.Value, binding.Key.LayerIndex));
            }

            targets.AddRange(anonymousTargets);

            targets.Sort((left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
            ValidateDuplicateHashes(targets);
            return pending;
        }

        internal static bool TryFindBinding(MosaicPaintingTargetId id, out MosaicPaintingRuntimeBinding binding)
        {
            binding = default;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;

            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            if (!entityManager.TryGetUnmanagedSingleton<TilemapIntGridSingleton>(out var singleton)) return false;

            var terrainQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Data.TerrainData, TilemapRendererData, TilemapTerrainLayerElement, EntityGuid,
                    MosaicRendererInitialized>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var rendererEntity in terrainQuery.ToEntityArray(Allocator.Temp))
            {
                var guid = entityManager.GetComponentData<EntityGuid>(rendererEntity);
                if (guid.OriginatingEntityId != id.GameObjectId) continue;

                var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(rendererEntity, true);
                if (id.LayerIndex >= 0 && id.LayerIndex < layers.Length)
                {
                    TryCreateBinding(world, layers[id.LayerIndex].IntGridEntity, rendererEntity,
                        singleton, out binding);
                }

                break;
            }
            terrainQuery.Dispose();
            if (binding.IsCreated) return true;

            var tilemapQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, TilemapRendererData, EntityGuid, MosaicRendererInitialized>()
                .WithNone<Data.TerrainData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in tilemapQuery.ToEntityArray(Allocator.Temp))
            {
                var guid = entityManager.GetComponentData<EntityGuid>(entity);
                if (guid.OriginatingEntityId != id.GameObjectId || id.LayerIndex != 0) continue;

                TryCreateBinding(world, entity, entity, singleton, out binding);
                break;
            }
            tilemapQuery.Dispose();

            return binding.IsCreated;
        }

        private static void DiscoverBindings(World world, bool includeAllSceneEntities,
            HashSet<Hash128> subSceneGuids, HashSet<EntityId> loadedSources,
            Dictionary<MosaicPaintingTargetId, MosaicPaintingRuntimeBinding> bindings,
            List<MosaicPaintingTarget> anonymousTargets)
        {
            var entityManager = world.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            if (!entityManager.TryGetUnmanagedSingleton<TilemapIntGridSingleton>(out var singleton)) return;

            var terrainQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Data.TerrainData, TilemapRendererData, TilemapTerrainLayerElement,
                    MosaicRendererInitialized>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var rendererEntity in terrainQuery.ToEntityArray(Allocator.Temp))
            {
                if (!IsInScope(entityManager, rendererEntity, includeAllSceneEntities,
                        subSceneGuids, loadedSources)) continue;
                var layers = entityManager.GetBuffer<TilemapTerrainLayerElement>(rendererEntity, true);
                for (var i = 0; i < layers.Length; i++)
                {
                    if (entityManager.HasComponent<EntityGuid>(rendererEntity))
                    {
                        var rendererGuid = entityManager.GetComponentData<EntityGuid>(rendererEntity);
                        TryAddBinding(world, layers[i].IntGridEntity, rendererEntity,
                            new MosaicPaintingTargetId(rendererGuid.OriginatingEntityId,
                                rendererGuid.OriginatingSubEntityId, i), singleton, bindings);
                    }
                    else if (TryCreateBinding(world, layers[i].IntGridEntity, rendererEntity,
                                 singleton, out var binding))
                    {
                        anonymousTargets.Add(CreateEntityTarget(binding, i));
                    }
                }
            }
            terrainQuery.Dispose();

            var tilemapQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<IntGridData, TilemapRendererData, MosaicRendererInitialized>()
                .WithNone<Data.TerrainData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            foreach (var entity in tilemapQuery.ToEntityArray(Allocator.Temp))
            {
                if (!IsInScope(entityManager, entity, includeAllSceneEntities,
                        subSceneGuids, loadedSources)) continue;
                if (entityManager.HasComponent<EntityGuid>(entity))
                {
                    var guid = entityManager.GetComponentData<EntityGuid>(entity);
                    TryAddBinding(world, entity, entity, new MosaicPaintingTargetId(
                        guid.OriginatingEntityId, guid.OriginatingSubEntityId, 0), singleton, bindings);
                }
                else if (TryCreateBinding(world, entity, entity, singleton, out var binding))
                {
                    anonymousTargets.Add(CreateEntityTarget(binding, 0));
                }
            }
            tilemapQuery.Dispose();
        }

        private static void TryAddBinding(World world, Entity intGridEntity, Entity rendererEntity,
            MosaicPaintingTargetId id, TilemapIntGridSingleton singleton,
            Dictionary<MosaicPaintingTargetId, MosaicPaintingRuntimeBinding> bindings)
        {
            if (TryCreateBinding(world, intGridEntity, rendererEntity, singleton, out var binding))
            {
                bindings[id] = binding;
            }
        }

        internal static bool TryCreateBinding(World world, Entity intGridEntity, Entity rendererEntity,
            TilemapIntGridSingleton singleton, out MosaicPaintingRuntimeBinding binding)
        {
            binding = default;
            var entityManager = world.EntityManager;
            if (!entityManager.Exists(intGridEntity) || !entityManager.HasComponent<IntGridData>(intGridEntity)
                || !entityManager.IsComponentEnabled<IntGridData>(intGridEntity)
                || !entityManager.IsComponentEnabled<MosaicRendererInitialized>(rendererEntity))
            {
                return false;
            }

            var intGridHash = entityManager.GetComponentData<IntGridData>(intGridEntity).Hash;
            var rendererHash = entityManager.GetComponentData<TilemapRendererData>(rendererEntity).MeshHash;
            if (intGridHash == default || rendererHash == default
                                       || !singleton.IntGridLayers.TryGetValue(intGridHash, out var layer)
                                       || layer.IntGridEntity != intGridEntity)
            {
                return false;
            }

            binding = new MosaicPaintingRuntimeBinding(
                world, intGridEntity, rendererEntity, intGridHash, rendererHash);
            return true;
        }

        private static MosaicPaintingTarget CreateEntityTarget(MosaicPaintingRuntimeBinding binding,
            int layerIndex)
        {
            var entityManager = binding.World.EntityManager;
            var intGridData = entityManager.GetComponentData<IntGridData>(binding.IntGridEntity);
            var isTerrain = binding.IntGridEntity != binding.RendererEntity;
            var displayName = isTerrain
                ? $"Terrain / Layer {layerIndex + 1} / {intGridData.DebugName}"
                : intGridData.DebugName.ToString();
            return new MosaicPaintingTarget(binding.World, binding.IntGridEntity,
                binding.RendererEntity, displayName, isTerrain, layerIndex);
        }

        internal static bool IsInScope(EntityManager entityManager, Entity entity, bool includeAllSceneEntities,
            HashSet<Hash128> subSceneGuids, HashSet<EntityId> loadedSources)
        {
            if (MosaicInitializationSystem.IsStaleSceneEntity(entityManager, entity)) return false;
            if (entityManager.HasComponent<SceneSection>(entity))
            {
                return includeAllSceneEntities
                       || subSceneGuids.Contains(entityManager.GetSharedComponent<SceneSection>(entity).SceneGUID);
            }

            if (!entityManager.HasComponent<EntityGuid>(entity)) return false;
            var source = entityManager.GetComponentData<EntityGuid>(entity).OriginatingEntityId;
            return loadedSources.Contains(source);
        }

        internal static bool TryGetBinding(MosaicPaintingTarget candidate,
            IReadOnlyDictionary<MosaicPaintingTargetId, MosaicPaintingRuntimeBinding> bindings,
            out MosaicPaintingRuntimeBinding binding)
        {
            if (bindings.TryGetValue(candidate.Id, out binding)) return true;

            foreach (var pair in bindings)
            {
                if (pair.Key.GameObjectId == candidate.GameObjectSourceId
                    && pair.Key.LayerIndex == candidate.LayerIndex)
                {
                    binding = pair.Value;
                    return true;
                }
            }

            binding = default;
            return false;
        }

        private static HashSet<Hash128> GetSubSceneGuids(StageHandle stage)
        {
            var result = new HashSet<Hash128>();
            foreach (var subScene in SubScene.AllSubScenes)
            {
                if (subScene != null && subScene.isActiveAndEnabled && subScene.SceneGUID != default
                    && MosaicPaintingController.BelongsToStage(subScene, stage))
                {
                    result.Add(subScene.SceneGUID);
                }
            }

            return result;
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
    }
}
