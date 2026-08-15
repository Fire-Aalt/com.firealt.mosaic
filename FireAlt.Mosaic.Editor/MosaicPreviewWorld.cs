using System;
using System.Collections.Generic;
using FireAlt.Core.EntityCommands;
using FireAlt.Core.Extensions;
using FireAlt.Core.Groups;
using FireAlt.Core.Rendering;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPreviewWorld : IDisposable
    {
        internal readonly struct Binding
        {
            public Binding(World world, Hash128 hash, Hash128 renderHash, Entity intGridEntity,
                Entity renderEntity)
            {
                World = world;
                Hash = hash;
                RenderHash = renderHash;
                IntGridEntity = intGridEntity;
                RenderEntity = renderEntity;
            }

            public World World { get; }

            public Hash128 Hash { get; }

            public Hash128 RenderHash { get; }

            public Entity IntGridEntity { get; }

            public Entity RenderEntity { get; }
        }

        private readonly Dictionary<string, Binding> _bindings = new();
        private BlobAssetStore _blobAssetStore;
        private InitializationSystemGroup _initializationGroup;
        private PresentationSystemGroup _presentationGroup;
        private World _world;

        public bool IsCreated => _world != null && _world.IsCreated;

        public void Rebuild(IReadOnlyList<MosaicPaintingTarget> targets)
        {
            Dispose();

            var manualTargets = new List<MosaicPaintingTarget>();
            foreach (var target in targets)
            {
                if (!target.IsSubScene && target.IsValid) manualTargets.Add(target);
            }

            if (manualTargets.Count == 0) return;

            _world = new World("Mosaic Painting Preview", WorldFlags.Editor);
            _blobAssetStore = new BlobAssetStore(128);
            CreateSystems();

            var gridEntities = new Dictionary<GridAuthoring, Entity>();
            var bakedOwners = new HashSet<MonoBehaviour>();
            foreach (var target in manualTargets)
            {
                if (!bakedOwners.Add(target.Owner)) continue;

                if (!gridEntities.TryGetValue(target.Grid, out var gridEntity))
                {
                    gridEntity = _world.EntityManager.CreateEntity();
                    var gridCommands = new EntityManagerCommands(_world.EntityManager, gridEntity, _blobAssetStore);
                    target.Grid.Bake(ref gridCommands);
                    gridEntities.Add(target.Grid, gridEntity);
                }

                BakeOwner(target.Owner, gridEntity, manualTargets);
            }

            Update();
            Update();
            Update();
        }

        public void Update()
        {
            if (!IsCreated) return;
            _initializationGroup.Update();
            _presentationGroup.Update();
        }

        public bool TryGetBinding(MosaicPaintingTarget target, out Binding binding)
        {
            return _bindings.TryGetValue(target.Id, out binding);
        }

        public void Reseed(MosaicPaintingTarget target)
        {
            if (TryGetBinding(target, out var binding)) Reseed(binding, target);
        }

        public static void Reseed(Binding binding, MosaicPaintingTarget target)
        {
            if (!binding.World.IsCreated) return;

            var entityManager = binding.World.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<TilemapIntGridSingleton>());
            if (query.IsEmpty || !entityManager.Exists(binding.IntGridEntity)
                              || !entityManager.HasComponent<IntGridData>(binding.IntGridEntity))
            {
                return;
            }

            var singleton = query.GetSingleton<TilemapIntGridSingleton>();
            if (!singleton.IntGridLayers.ContainsKey(binding.Hash)) return;

            ref var layer = ref singleton.IntGridLayers.GetValueAsRef(binding.Hash);
            var previousPositions = new NativeList<int2>(layer.IntGrid.Count, Allocator.Temp);
            foreach (var cell in layer.IntGrid) previousPositions.Add(cell.Key);
            foreach (var position in previousPositions) layer.SetValue(position, 0);
            foreach (var cell in target.Cells)
            {
                layer.SetValue(new int2(cell.Position.x, cell.Position.y), cell.Value);
            }
        }

        public static void Apply(Binding binding, IReadOnlyCollection<Vector2Int> positions, short value)
        {
            if (!binding.World.IsCreated) return;

            var entityManager = binding.World.EntityManager;
            entityManager.CompleteAllTrackedJobs();
            var query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<TilemapIntGridSingleton>());
            if (query.IsEmpty) return;

            var singleton = query.GetSingleton<TilemapIntGridSingleton>();
            if (!singleton.IntGridLayers.ContainsKey(binding.Hash)) return;

            ref var layer = ref singleton.IntGridLayers.GetValueAsRef(binding.Hash);
            foreach (var position in positions)
            {
                layer.SetValue(new int2(position.x, position.y), value);
            }
        }

        public bool TryGetRenderData(MosaicPaintingTarget target, out Mesh mesh, out Material material)
        {
            mesh = null;
            material = null;
            if (!TryGetBinding(target, out var binding) || !binding.World.IsCreated) return false;

            var entityManager = binding.World.EntityManager;
            var presentationQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<PresentationDataSingleton>());
            if (presentationQuery.IsEmpty) return false;

            var data = presentationQuery.GetSingleton<PresentationDataSingleton>().Value.Value;
            if (!data.MeshMap.TryGetValue(binding.RenderHash, out mesh) || mesh == null) return false;

            if (target.IsTerrain)
            {
                if (!data.TerrainMap.TryGetValue(binding.RenderHash, out var terrainData)) return false;
                material = terrainData.Material;
            }
            else if (entityManager.Exists(binding.RenderEntity)
                     && entityManager.HasComponent<RuntimeMaterial>(binding.RenderEntity))
            {
                material = entityManager.GetComponentData<RuntimeMaterial>(binding.RenderEntity).Value.Value;
            }

            return material != null;
        }

        public void Dispose()
        {
            _bindings.Clear();

            if (_world != null && _world.IsCreated)
            {
                _world.EntityManager.CompleteAllTrackedJobs();
                _world.Dispose();
            }

            _world = null;
            _initializationGroup = null;
            _presentationGroup = null;

            if (_blobAssetStore.IsCreated) _blobAssetStore.Dispose();
            _blobAssetStore = default;
        }

        private void BakeOwner(MonoBehaviour owner, Entity gridEntity,
            IReadOnlyList<MosaicPaintingTarget> manualTargets)
        {
            var entityManager = _world.EntityManager;
            var renderEntity = entityManager.CreateEntity();
            var commands = new EntityManagerCommands(entityManager, renderEntity, _blobAssetStore);

            switch (owner)
            {
                case TilemapAuthoring tilemap:
                    tilemap.Bake(ref commands, gridEntity);
                    entityManager.AddComponentData(renderEntity, new LocalToWorld
                    {
                        Value = tilemap.transform.localToWorldMatrix,
                    });

                    var intGridData = entityManager.GetComponentData<IntGridData>(renderEntity);
                    var tilemapTarget = FindTarget(manualTargets, owner, 0);
                    if (tilemapTarget != null)
                    {
                        _bindings[tilemapTarget.Id] = new Binding(_world, intGridData.Hash, intGridData.Hash,
                            renderEntity, renderEntity);
                    }

                    break;
                case TilemapTerrainAuthoring terrain:
                    terrain.Bake(ref commands, gridEntity);
                    entityManager.AddComponentData(renderEntity, new LocalToWorld
                    {
                        Value = terrain.transform.localToWorldMatrix,
                    });

                    var layerHashes = entityManager.GetBuffer<TilemapTerrainLayerElement>(renderEntity);
                    var terrainData = entityManager.GetComponentData<Data.TerrainData>(renderEntity);
                    var intGridEntities = FindTerrainLayerEntities(entityManager, layerHashes);
                    for (var i = 0; i < layerHashes.Length; i++)
                    {
                        var terrainTarget = FindTarget(manualTargets, owner, i);
                        if (terrainTarget == null) continue;

                        _bindings[terrainTarget.Id] = new Binding(_world, layerHashes[i].IntGridHash,
                            terrainData.TerrainHash, intGridEntities[i], renderEntity);
                    }

                    break;
            }
        }

        private void CreateSystems()
        {
            _initializationGroup = _world.GetOrCreateSystemManaged<InitializationSystemGroup>();
            _presentationGroup = _world.GetOrCreateSystemManaged<PresentationSystemGroup>();

            var runtimeBaking = _world.GetOrCreateSystemManaged<RuntimeBakingSystemGroup>();
            runtimeBaking.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<RuntimeMaterialSystem>());
            runtimeBaking.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<MosaicInitializationSystem>());
            _initializationGroup.AddSystemToUpdateList(runtimeBaking);

            var tilemapUpdate = _world.GetOrCreateSystemManaged<TilemapUpdateSystemGroup>();
            tilemapUpdate.AddSystemToUpdateList(_world.GetOrCreateSystem<RuleEngineSystem>());
            tilemapUpdate.AddSystemToUpdateList(_world.GetOrCreateSystem<IntGridMeshDataSystem>());
            tilemapUpdate.AddSystemToUpdateList(_world.GetOrCreateSystem<TerrainMeshDataSystem>());

            _presentationGroup.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<MosaicPresentationSystem>());
            _presentationGroup.AddSystemToUpdateList(tilemapUpdate);

            runtimeBaking.SortSystems();
            tilemapUpdate.SortSystems();
            _initializationGroup.SortSystems();
            _presentationGroup.SortSystems();
        }

        private static MosaicPaintingTarget FindTarget(IReadOnlyList<MosaicPaintingTarget> targets,
            MonoBehaviour owner, int layerIndex)
        {
            foreach (var target in targets)
            {
                if (target.Owner == owner && target.LayerIndex == layerIndex) return target;
            }

            return null;
        }

        private static Entity[] FindTerrainLayerEntities(EntityManager entityManager,
            DynamicBuffer<TilemapTerrainLayerElement> layerHashes)
        {
            var result = new Entity[layerHashes.Length];
            var query = new EntityQueryBuilder(Unity.Collections.Allocator.Temp)
                .WithAll<IntGridData>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build(entityManager);
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            var data = query.ToComponentDataArray<IntGridData>(Unity.Collections.Allocator.Temp);
            for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
            {
                for (var layerIndex = 0; layerIndex < layerHashes.Length; layerIndex++)
                {
                    if (data[entityIndex].Hash == layerHashes[layerIndex].IntGridHash)
                    {
                        result[layerIndex] = entities[entityIndex];
                    }
                }
            }

            return result;
        }
    }
}
