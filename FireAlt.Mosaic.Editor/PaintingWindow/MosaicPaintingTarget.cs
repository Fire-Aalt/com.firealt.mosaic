using System;
using System.Collections.Generic;
using FireAlt.Core.Editor;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal readonly struct MosaicPaintingTargetId : IEquatable<MosaicPaintingTargetId>
    {
        public MosaicPaintingTargetId(EntityId gameObjectId, EntityId authoringId, int layerIndex)
        {
            GameObjectId = gameObjectId;
            AuthoringId = authoringId;
            LayerIndex = layerIndex;
        }

        public EntityId GameObjectId { get; }

        public EntityId AuthoringId { get; }

        public int LayerIndex { get; }

        public bool Equals(MosaicPaintingTargetId other)
        {
            return GameObjectId.Equals(other.GameObjectId) && AuthoringId.Equals(other.AuthoringId)
                && LayerIndex == other.LayerIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is MosaicPaintingTargetId other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = GameObjectId.GetHashCode();
                hashCode = (hashCode * 397) ^ AuthoringId.GetHashCode();
                return (hashCode * 397) ^ LayerIndex;
            }
        }
    }

    internal readonly struct MosaicPaintingRuntimeBinding
    {
        public MosaicPaintingRuntimeBinding(World world, Entity intGridEntity, Entity rendererEntity,
            Hash128 intGridHash, Hash128 rendererHash)
        {
            World = world;
            IntGridEntity = intGridEntity;
            RendererEntity = rendererEntity;
            IntGridHash = intGridHash;
            RendererHash = rendererHash;
        }

        public World World { get; }

        public Entity IntGridEntity { get; }

        public Entity RendererEntity { get; }

        public Hash128 IntGridHash { get; }

        public Hash128 RendererHash { get; }

        public bool IsCreated => World != null && World.IsCreated && IntGridEntity != Entity.Null
                                 && RendererEntity != Entity.Null && IntGridHash != default
                                 && RendererHash != default;
    }

    internal readonly struct MosaicPaintingVisibilityTarget
    {
        public MosaicPaintingVisibilityTarget(MosaicPaintingRuntimeBinding binding,
            EntityId originatingEntityId = default)
        {
            Binding = binding;
            OriginatingEntityId = originatingEntityId;
        }

        public MosaicPaintingRuntimeBinding Binding { get; }

        public EntityId OriginatingEntityId { get; }
    }

    internal sealed class MosaicPaintingTarget
    {
        private const string PAINTED_DATA = "_paintedData";
        private const string PAINTED_LAYERS = "_paintedLayers";
        private readonly List<IntGridValueDefinition> _entityValues = new();
        private readonly List<SerializedIntGridCell> _entityCells = new();
        private readonly List<SerializedIntGridRectangle> _entityRectangles = new();
        private readonly MosaicPaintingRuntimeBinding _binding;
        private readonly string _displayName;
        private readonly bool _isTerrain;

        public MosaicPaintingTarget(TilemapAuthoring owner, MosaicPaintingRuntimeBinding binding = default)
        {
            Owner = owner;
            _binding = binding;
            IntGrid = owner.intGrid;
            LayerIndex = 0;
            Grid = owner.GetComponentInParent<GridAuthoring>();
            SceneCullingMask = owner.gameObject.sceneCullingMask;
        }

        public MosaicPaintingTarget(TilemapTerrainAuthoring owner, IntGridDefinition intGrid, int layerIndex,
            MosaicPaintingRuntimeBinding binding = default)
        {
            Owner = owner;
            _binding = binding;
            IntGrid = intGrid;
            LayerIndex = layerIndex;
            Grid = owner.GetComponentInParent<GridAuthoring>();
            SceneCullingMask = owner.gameObject.sceneCullingMask;
        }

        public MosaicPaintingTarget(World world, Entity intGridEntity, Entity rendererEntity,
            string displayName, bool isTerrain, int layerIndex)
        {
            _displayName = displayName;
            _isTerrain = isTerrain;
            LayerIndex = layerIndex;

            var entityManager = world.EntityManager;
            var intGridData = entityManager.GetComponentData<IntGridData>(intGridEntity);
            var rendererHash = entityManager.GetComponentData<TilemapRendererData>(rendererEntity).MeshHash;
            _binding = new MosaicPaintingRuntimeBinding(world, intGridEntity, rendererEntity,
                intGridData.Hash, rendererHash);
            SceneCullingMask = InternalEditorRenderData.GetSceneCullingMask(entityManager, rendererEntity);

            foreach (var value in entityManager.GetBuffer<IntGridValueElement>(intGridEntity))
            {
                _entityValues.Add(new IntGridValueDefinition
                {
                    value = value.Value,
                    name = value.Name.ToString(),
                    color = value.Color,
                    texture = value.Texture.Value,
                });
            }

            var singletonQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<TilemapIntGridSingleton>());
            if (singletonQuery.IsEmpty) return;

            var layers = singletonQuery.GetSingleton<TilemapIntGridSingleton>().IntGridLayers;
            if (!layers.TryGetValue(_binding.IntGridHash, out var layer)) return;
            foreach (var cell in layer.IntGrid)
            {
                _entityCells.Add(new SerializedIntGridCell(new Vector2Int(cell.Key.x, cell.Key.y), cell.Value));
            }

            _entityCells.Sort((left, right) => Compare(left.Position, right.Position));
            IntGridCellPacking.Pack(_entityCells, _entityRectangles);
        }

        public MonoBehaviour Owner { get; }

        public IntGridDefinition IntGrid { get; }

        public GridAuthoring Grid { get; }

        public int LayerIndex { get; }

        public bool IsTerrain => Owner == null ? _isTerrain : Owner is TilemapTerrainAuthoring;

        public bool IsSubScene => Owner == null || Owner.gameObject.scene.isSubScene;

        public bool IsEntityTarget => Owner == null;

        public bool HasLoadedAuthoringScene => !IsEntityTarget && Owner != null
            && Owner.gameObject.scene.IsValid() && Owner.gameObject.scene.isLoaded;

        public bool IsPaintable => !IsEntityTarget && IsValid;

        public ulong SceneCullingMask { get; }

        public Hash128 IntGridHash => _binding.IntGridHash;

        public Hash128 RendererHash => _binding.RendererHash;

        public MosaicPaintingTargetId Id => new(GameObjectSourceId, SourceId, LayerIndex);

        public EntityId SourceId => Owner != null ? Owner.GetEntityId() : TryGetEntityGuid(out var guid)
            ? guid.OriginatingSubEntityId
            : default;

        public EntityId GameObjectSourceId => Owner != null ? Owner.gameObject.GetEntityId()
            : TryGetEntityGuid(out var guid) ? guid.OriginatingEntityId : default;

        public MosaicPaintingRuntimeBinding RuntimeBinding => _binding;

        public string DisplayName => IsEntityTarget ? _displayName : IsTerrain
            ? $"{Owner.name} / Layer {LayerIndex + 1} / {IntGrid?.name ?? "Missing IntGrid"}"
            : $"{Owner.name} / {IntGrid?.name ?? "Missing IntGrid"}";

        public string AdditionalValidationMessage { get; set; }

        public bool IsValid => (IsEntityTarget ? IsEntityValid() : IsAuthoringValid()) 
                               && string.IsNullOrEmpty(AdditionalValidationMessage);

        private RenderingData RenderingData => Owner switch
        {
            TilemapAuthoring tilemap => tilemap.renderingData,
            TilemapTerrainAuthoring terrain => terrain.renderingData,
            _ => null,
        };

        public IReadOnlyList<IntGridValueDefinition> Values => IsEntityTarget
            ? _entityValues
            : IntGrid != null ? IntGrid.intGridValues : Array.Empty<IntGridValueDefinition>();

        public IReadOnlyList<SerializedIntGridCell> Cells
        {
            get
            {
                if (IsEntityTarget) return _entityCells;
                if (Owner is TilemapAuthoring tilemap) return tilemap.PaintedCells;
                if (Owner is not TilemapTerrainAuthoring terrain) return Array.Empty<SerializedIntGridCell>();

                foreach (var layer in terrain.PaintedLayers)
                {
                    if (layer.IntGrid == IntGrid) return layer.Cells;
                }

                return Array.Empty<SerializedIntGridCell>();
            }
        }

        public MosaicPaintingVisibilityTarget VisibilityTarget => new(_binding);

        public bool TryGetValueDefinition(short value, out IntGridValueDefinition definition)
        {
            definition = null;
            if (!IsValid) return false;

            foreach (var candidate in Values)
            {
                if (candidate.value != value) continue;
                definition = candidate;
                return true;
            }

            return false;
        }

        public bool SetCell(Vector2Int position, short value)
        {
            return SetCells(new[] { position }, value);
        }

        public bool SetCells(IEnumerable<Vector2Int> positions, short value)
        {
            using var stroke = BeginStroke(value);
            return stroke.SetCells(positions);
        }

        internal MosaicPaintingStroke BeginStroke(short value)
        {
            return new MosaicPaintingStroke(this, value);
        }

        public bool TryGetCell(Vector2 mousePosition, out Vector2Int cell)
        {
            cell = default;
            if (!IsValid) return false;

            var worldRay = HandleUtility.GUIPointToWorldRay(mousePosition);
            var matrix = GetLocalToWorldMatrix().inverse;
            var ray = new Ray(matrix.MultiplyPoint(worldRay.origin), matrix.MultiplyVector(worldRay.direction));
            var tilemapTransform = GetTilemapTransform();
            var planeAxis = tilemapTransform.Swizzle == Swizzle.XZY ? 1 : 2;
            var direction = ray.direction[planeAxis];
            if (Mathf.Abs(direction) < 0.00001f) return false;

            var distance = -ray.origin[planeAxis] / direction;
            if (distance < 0f) return false;

            var local = ray.GetPoint(distance);
            var second = tilemapTransform.Swizzle == Swizzle.XZY ? local.z : local.y;
            var centerOffset = PaintedCellCenterOffset;
            cell = new Vector2Int(
                Mathf.FloorToInt((local.x / tilemapTransform.CellSize.x) - centerOffset + 0.5f),
                Mathf.FloorToInt((second / tilemapTransform.CellSize.y) - centerOffset + 0.5f));
            return true;
        }

        public void GetCellCorners(Vector2Int cell, Vector3[] corners, float normalOffset = 0.002f)
        {
            var tilemapTransform = GetTilemapTransform();
            var matrix = GetLocalToWorldMatrix();

            var min = new float2(cell.x, cell.y) + PaintedCellCenterOffset - 0.5f;
            var max = min + 1f;
            corners[0] = matrix.MultiplyPoint(MosaicUtils.ToWorldSpace(min, tilemapTransform));
            corners[1] = matrix.MultiplyPoint(MosaicUtils.ToWorldSpace(new float2(min.x, max.y), tilemapTransform));
            corners[2] = matrix.MultiplyPoint(MosaicUtils.ToWorldSpace(max, tilemapTransform));
            corners[3] = matrix.MultiplyPoint(MosaicUtils.ToWorldSpace(new float2(max.x, min.y), tilemapTransform));

            var normal = Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]).normalized * normalOffset;
            for (var i = 0; i < corners.Length; i++) corners[i] += normal;
        }

        public IReadOnlyList<SerializedIntGridRectangle> Rectangles
        {
            get
            {
                if (IsEntityTarget) return _entityRectangles;
                if (Owner is TilemapAuthoring tilemap) return tilemap.PaintedData.Rectangles;
                if (Owner is not TilemapTerrainAuthoring terrain) return Array.Empty<SerializedIntGridRectangle>();

                foreach (var layer in terrain.PaintedLayers)
                {
                    if (layer.IntGrid == IntGrid) return layer.PaintedData.Rectangles;
                }

                return Array.Empty<SerializedIntGridRectangle>();
            }
        }

        public int CellCount
        {
            get
            {
                if (IsEntityTarget) return _entityCells.Count;
                if (Owner is TilemapAuthoring tilemap) return tilemap.PaintedData.CellCount;
                if (Owner is not TilemapTerrainAuthoring terrain) return 0;

                foreach (var layer in terrain.PaintedLayers)
                {
                    if (layer.IntGrid == IntGrid) return layer.PaintedData.CellCount;
                }

                return 0;
            }
        }

        public Vector3 GetCellCenter(Vector2Int cell)
        {
            var center = new float2(cell.x, cell.y) + PaintedCellCenterOffset;
            return GetLocalToWorldMatrix().MultiplyPoint(MosaicUtils.ToWorldSpace(center, GetTilemapTransform()));
        }

        private float PaintedCellCenterOffset => IsDualGrid ? 0f : 0.5f;

        private bool IsDualGrid => IsEntityTarget
            ? _binding.World.EntityManager.GetComponentData<IntGridData>(_binding.IntGridEntity).DualGrid
            : IntGrid.useDualGrid;

        private bool IsAuthoringValid()
        {
            return HasLoadedAuthoringScene && Owner.isActiveAndEnabled && IntGrid != null && Grid != null
                   && RenderingData?.material != null;
        }

        private bool IsEntityValid()
        {
            if (!_binding.IsCreated) return false;
            var entityManager = _binding.World.EntityManager;
            return entityManager.Exists(_binding.IntGridEntity) && entityManager.Exists(_binding.RendererEntity)
                   && entityManager.HasComponent<IntGridData>(_binding.IntGridEntity)
                   && entityManager.HasComponent<TilemapTransform>(_binding.IntGridEntity)
                   && entityManager.HasComponent<TilemapRendererData>(_binding.RendererEntity)
                   && entityManager.HasComponent<LocalToWorld>(_binding.RendererEntity) && _entityValues.Count != 0;
        }

        private bool TryGetEntityGuid(out EntityGuid guid)
        {
            var entityManager = _binding.World.EntityManager;
            if (entityManager.Exists(_binding.RendererEntity)
                && entityManager.HasComponent<EntityGuid>(_binding.RendererEntity))
            {
                guid = entityManager.GetComponentData<EntityGuid>(_binding.RendererEntity);
                return true;
            }

            guid = default;
            return false;
        }

        private TilemapTransform GetTilemapTransform()
        {
            if (IsEntityTarget)
            {
                return _binding.World.EntityManager.GetComponentData<TilemapTransform>(_binding.IntGridEntity);
            }

            return new TilemapTransform
            {
                CellSize = Grid.CellSize,
                Swizzle = Grid.CellSwizzle,
                Orientation = RenderingData.orientation,
            };
        }

        private Matrix4x4 GetLocalToWorldMatrix()
        {
            if (!IsEntityTarget) return Owner.transform.localToWorldMatrix;

            var value = _binding.World.EntityManager.GetComponentData<LocalToWorld>(_binding.RendererEntity).Value;
            return new Matrix4x4(value.c0, value.c1, value.c2, value.c3);
        }

        private static int Compare(Vector2Int left, Vector2Int right)
        {
            var y = left.y.CompareTo(right.y);
            return y != 0 ? y : left.x.CompareTo(right.x);
        }

        internal bool TryGetMutableData(out SerializedIntGridData data)
        {
            data = null;
            if (!IsPaintable) return false;

            if (Owner is TilemapAuthoring tilemap)
            {
                data = tilemap.PaintedData;
                return true;
            }

            if (Owner is not TilemapTerrainAuthoring terrain) return false;

            foreach (var layer in terrain.MutablePaintedLayers)
            {
                if (layer.IntGrid == IntGrid)
                {
                    data = layer.PaintedData;
                    return true;
                }
            }

            return false;
        }

        internal static bool IsPaintedCellProperty(UnityEngine.Object target, string propertyPath)
        {
            if (target is TilemapAuthoring)
            {
                return propertyPath == PAINTED_DATA
                       || propertyPath.StartsWith($"{PAINTED_DATA}.", StringComparison.Ordinal);
            }

            if (target is not TilemapTerrainAuthoring
                || !propertyPath.StartsWith($"{PAINTED_LAYERS}.", StringComparison.Ordinal))
            {
                return false;
            }

            return propertyPath.EndsWith($".{PAINTED_DATA}", StringComparison.Ordinal)
                   || propertyPath.Contains($".{PAINTED_DATA}.", StringComparison.Ordinal);
        }

    }
}
