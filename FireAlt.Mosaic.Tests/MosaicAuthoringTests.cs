using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FireAlt.Core;
using FireAlt.Core.Editor;
using FireAlt.Core.Extensions;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using FireAlt.Mosaic.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace FireAlt.Mosaic.Tests
{
    public sealed class MosaicAuthoringTests
    {
        private struct TestCleanup : ICleanupComponentData
        {
        }

        private readonly List<string> _temporaryAssets = new();
        private GameObject _gridObject;
        private IntGridDefinition _intGrid;
        private Material _material;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = new World(nameof(MosaicAuthoringTests), WorldFlags.Editor);
            _gridObject = new GameObject("Grid", typeof(GridAuthoring));
            _intGrid = ScriptableObject.CreateInstance<IntGridDefinition>();
            _intGrid.name = "Test IntGrid";
            _intGrid.intGridValues.Add(new IntGridValueDefinition
            {
                value = 1,
                name = "Solid",
                color = Color.red,
            });

            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            _material = new Material(shader);
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated) _world.Dispose();
            Object.DestroyImmediate(_material);
            foreach (var assetPath in _temporaryAssets) AssetDatabase.DeleteAsset(assetPath);
            _temporaryAssets.Clear();
            Object.DestroyImmediate(_intGrid);
            Object.DestroyImmediate(_gridObject);
        }

        private GameObject CreateTilemap(string name)
        {
            var tilemapObject = new GameObject(name, typeof(TilemapAuthoring));
            tilemapObject.transform.SetParent(_gridObject.transform);
            var tilemap = tilemapObject.GetComponent<TilemapAuthoring>();
            tilemap.intGrid = _intGrid;
            tilemap.renderingData.material = _material;
            return tilemapObject;
        }

        private Entity CreateIntGridEntity(Unity.Entities.Hash128 hash, Entity gridEntity)
        {
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, new IntGridData { Hash = hash, DebugName = "Test" });
            _world.EntityManager.SetComponentEnabled<IntGridData>(entity, false);
            _world.EntityManager.AddComponentData(entity, new TilemapTransform { GridEntity = gridEntity });
            _world.EntityManager.AddBuffer<IntGridInitialValueElement>(entity);
            return entity;
        }

        private Entity FindEntity<T>(Entity[] entities)
            where T : unmanaged, IComponentData
        {
            foreach (var entity in entities)
            {
                if (_world.EntityManager.HasComponent<T>(entity)) return entity;
            }

            Assert.Fail($"No baked entity contains {typeof(T).Name}.");
            return Entity.Null;
        }

        private Entity[] GetPreviewEntities()
        {
            var query = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<MosaicPaintingPreviewEntity>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab)
                .Build(_world.EntityManager);
            return query.ToEntityArray(Allocator.Temp).ToArray();
        }

        private static bool ContainsHash(NativeArray<IntGridData> data, Unity.Entities.Hash128 hash)
        {
            foreach (var value in data)
            {
                if (value.Hash == hash) return true;
            }

            return false;
        }
    }
}
