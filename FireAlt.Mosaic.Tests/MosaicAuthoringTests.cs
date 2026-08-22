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
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Transforms;

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

        private MosaicPaintingTarget CreateEntityPaintingTarget(float3 position,
            Unity.Entities.Hash128? targetHash = null)
        {
            var intGridHash = targetHash ?? new Unity.Entities.Hash128(1u, 0u, 0u, 0u);
            var rendererHash = new Unity.Entities.Hash128(2u, 0u, 0u, 0u);
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponentData(entity, new IntGridData
            {
                Hash = intGridHash,
                DebugName = "Entity IntGrid",
            });
            _world.EntityManager.AddComponentData(entity, new TilemapTransform
            {
                CellSize = 1f,
                Swizzle = Swizzle.XZY,
            });
            _world.EntityManager.AddComponentData(entity, new TilemapRendererData { MeshHash = rendererHash });
            _world.EntityManager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(position) });
            _world.EntityManager.AddBuffer<IntGridValueElement>(entity).Add(new IntGridValueElement
            {
                Value = 1,
                Name = "Solid",
                Color = Color.red,
            });

            return new MosaicPaintingTarget(_world, entity, entity, "Entity IntGrid", false, 0);
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

        [Test]
        public void PaintingTarget_AuthoringTargetWritesSerializedCells()
        {
            var tilemap = CreateTilemap("Authoring Tilemap").GetComponent<TilemapAuthoring>();
            var target = new MosaicPaintingTarget(tilemap);

            Assert.IsTrue(target.IsPaintable);
            Assert.IsTrue(target.SetCell(new Vector2Int(2, 3), 1));
            Assert.AreEqual(1, tilemap.PaintedCells.Count);
            Assert.AreEqual(new Vector2Int(2, 3), tilemap.PaintedCells[0].Position);
            Assert.AreEqual(1, tilemap.PaintedCells[0].Value);
        }

        [Test]
        public void PaintingTarget_EntityTargetIsReadOnly()
        {
            var target = CreateEntityPaintingTarget(float3.zero);

            Assert.IsTrue(target.IsValid);
            Assert.IsFalse(target.IsPaintable);
            Assert.IsFalse(target.SetCell(Vector2Int.zero, 1));
            Assert.AreEqual(0, target.Cells.Count);
        }

        [Test]
        public void Initialization_SameBakedSourceIgnoresBakeNamespace()
        {
            var grid = _gridObject.GetComponent<GridAuthoring>();
            var other = CreateTilemap("Different Authoring Source").GetComponent<TilemapAuthoring>();
            var objectId = _gridObject.GetEntityId();
            var componentId = grid.GetEntityId();
            var first = new EntityGuid(objectId, componentId, 1u, 0u);
            var secondBake = new EntityGuid(objectId, componentId, 2u, 0u);
            var differentSubEntity = new EntityGuid(objectId, componentId, 2u, 1u);
            var differentSource = new EntityGuid(other.gameObject.GetEntityId(), other.GetEntityId(), 2u, 0u);

            Assert.IsTrue(MosaicInitializationSystem.IsSameBakedSource(first, secondBake));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, differentSubEntity));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, differentSource));
            Assert.IsFalse(MosaicInitializationSystem.IsSameBakedSource(first, EntityGuid.Null));
        }

        [Test]
        public void Initialization_DeadSceneOwnerIsStale()
        {
            var sceneEntity = _world.EntityManager.CreateEntity();
            var target = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddSharedComponent(target, new SceneTag { SceneEntity = sceneEntity });

            Assert.IsFalse(MosaicInitializationSystem.IsStaleSceneEntity(_world.EntityManager, target));

            _world.EntityManager.DestroyEntity(sceneEntity);

            Assert.IsTrue(MosaicInitializationSystem.IsStaleSceneEntity(_world.EntityManager, target));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PaintingWindow_DisabledAuthoringTargetIsNotDiscovered(bool terrain, bool disableGameObject)
        {
            MonoBehaviour authoring;
            MosaicPaintingTarget target;
            if (terrain)
            {
                var terrainObject = new GameObject("Disabled Terrain", typeof(TilemapTerrainAuthoring));
                terrainObject.transform.SetParent(_gridObject.transform);
                var terrainAuthoring = terrainObject.GetComponent<TilemapTerrainAuthoring>();
                terrainAuthoring.intGridLayers.Add(_intGrid);
                terrainAuthoring.renderingData.material = _material;
                authoring = terrainAuthoring;
                target = new MosaicPaintingTarget(terrainAuthoring, _intGrid, 0);
            }
            else
            {
                authoring = CreateTilemap("Disabled Tilemap").GetComponent<TilemapAuthoring>();
                target = new MosaicPaintingTarget((TilemapAuthoring)authoring);
            }

            Assert.IsTrue(target.IsPaintable);
            if (disableGameObject) authoring.gameObject.SetActive(false);
            else authoring.enabled = false;
            Assert.IsFalse(target.IsPaintable);

            var targets = new List<MosaicPaintingTarget>();
            MosaicPaintingPreviewService.AddAuthoringTargets(targets, StageUtility.GetCurrentStageHandle());

            Assert.IsFalse(targets.Any(target => ReferenceEquals(target.Owner, authoring)));
        }

        [Test]
        public void PaintingWindow_OutsideAuthoringTargetDoesNotSuppressMatchingEntityTarget()
        {
            var tilemap = CreateTilemap("Disabled Tilemap").GetComponent<TilemapAuthoring>();
            var hash = new MosaicPaintingTarget(tilemap).IntGridHash;
            tilemap.enabled = false;

            var targets = new List<MosaicPaintingTarget> { CreateEntityPaintingTarget(float3.zero, hash) };
            var inactiveHashes = new HashSet<Unity.Entities.Hash128>();
            MosaicPaintingPreviewService.AddAuthoringTargets(
                targets, StageUtility.GetCurrentStageHandle(), inactiveHashes);
            MosaicPaintingWindow.RemoveEntityTargetsShadowedByAuthoring(targets, inactiveHashes);

            Assert.AreEqual(1, targets.Count);
            Assert.IsTrue(targets[0].IsEntityTarget);
        }

        [Test]
        public void PaintingWindow_EntityTargetRequiresActiveSubSceneGuid()
        {
            var sceneGuid = new Unity.Entities.Hash128(3u, 0u, 0u, 0u);
            var entity = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddSharedComponent(entity, new SceneSection { SceneGUID = sceneGuid });
            var subSceneGuids = new HashSet<Unity.Entities.Hash128>();

            Assert.IsFalse(MosaicPaintingWindow.IsSubSceneEntity(_world.EntityManager, entity, subSceneGuids));

            subSceneGuids.Add(sceneGuid);

            Assert.IsTrue(MosaicPaintingWindow.IsSubSceneEntity(_world.EntityManager, entity, subSceneGuids));
        }

        [Test]
        public void PaintingPreview_ContextPrefabHidesOnlyMatchingSubSceneRenderer()
        {
            var rendererHash = new Unity.Entities.Hash128(4u, 0u, 0u, 0u);
            var entityManager = _world.EntityManager;
            var originatingEntityId = _gridObject.GetEntityId();
            var subSceneRenderer = entityManager.CreateEntity();
            entityManager.AddComponentData(subSceneRenderer, new TilemapRendererData { MeshHash = rendererHash });
            entityManager.AddComponentData(subSceneRenderer,
                new EntityGuid(originatingEntityId, default, 0u, 0u));
            entityManager.AddSharedComponent(subSceneRenderer, new SceneSection
            {
                SceneGUID = new Unity.Entities.Hash128(5u, 0u, 0u, 0u),
            });
            InternalEditorRenderData.SetSceneCullingMask(entityManager, subSceneRenderer, 11);

            var prefabRenderer = entityManager.CreateEntity();
            entityManager.AddComponentData(prefabRenderer, new TilemapRendererData { MeshHash = rendererHash });
            InternalEditorRenderData.SetSceneCullingMask(entityManager, prefabRenderer, 22);

            var preview = new MosaicPaintingPreview();
            var noTargets = new List<MosaicPaintingVisibilityTarget>();
            var contextTargets = new List<MosaicPaintingContextVisibilityTarget>
            {
                new(new MosaicPaintingVisibilityTarget(rendererHash, rendererHash), originatingEntityId),
            };

            preview.SetVisibility(_world, noTargets, contextTargets, false);

            Assert.AreEqual(0,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, subSceneRenderer));
            Assert.AreEqual(22,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, prefabRenderer));

            preview.SetVisibility(_world, noTargets, new List<MosaicPaintingContextVisibilityTarget>(), false);

            Assert.AreEqual(11,
                InternalEditorRenderData.GetSceneCullingMask(entityManager, subSceneRenderer));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaintingWindow_RawCellsSortBackToFront(bool orthographic)
        {
            var far = new MosaicPaintingTarget(CreateTilemap("Far").GetComponent<TilemapAuthoring>());
            var near = new MosaicPaintingTarget(CreateTilemap("Near").GetComponent<TilemapAuthoring>());
            far.Owner.transform.position = Vector3.zero;
            near.Owner.transform.position = new Vector3(10f, 2f, 0f);

            var cameraObject = new GameObject("Sorting Camera", typeof(Camera));
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = orthographic;
                camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));

                var cameraPosition = camera.transform.position;
                var cameraForward = camera.transform.forward;
                var farCell = new MosaicPaintingWindow.RawCell(
                    far, new SerializedIntGridCell(Vector2Int.zero, 1), cameraPosition, cameraForward, 0);
                var nearCell = new MosaicPaintingWindow.RawCell(
                    near, new SerializedIntGridCell(new Vector2Int(-10, 0), 1), cameraPosition, cameraForward, 1);

                Assert.Less(MosaicPaintingWindow.CompareRawCells(farCell, nearCell), 0);
                Assert.Greater(MosaicPaintingWindow.CompareRawCells(nearCell, farCell), 0);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
