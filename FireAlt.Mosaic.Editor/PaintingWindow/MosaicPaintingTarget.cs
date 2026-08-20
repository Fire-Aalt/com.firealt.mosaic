using System;
using System.Collections.Generic;
using FireAlt.Core.Editor;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace FireAlt.Mosaic.Editor
{
    internal readonly struct MosaicPaintingVisibilityTarget : IEquatable<MosaicPaintingVisibilityTarget>
    {
        public MosaicPaintingVisibilityTarget(Hash128 intGridHash, Hash128 rendererHash)
        {
            IntGridHash = intGridHash;
            RendererHash = rendererHash;
        }

        public Hash128 IntGridHash { get; }

        public Hash128 RendererHash { get; }

        public bool Equals(MosaicPaintingVisibilityTarget other)
        {
            return IntGridHash.Equals(other.IntGridHash) && RendererHash.Equals(other.RendererHash);
        }

        public override bool Equals(object obj)
        {
            return obj is MosaicPaintingVisibilityTarget other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (IntGridHash.GetHashCode() * 397) ^ RendererHash.GetHashCode();
        }
    }

    internal sealed class MosaicPaintingTarget
    {
        private const string PAINTED_CELLS = "_paintedCells";
        private const string PAINTED_LAYERS = "_paintedLayers";
        private const string INT_GRID = "_intGrid";
        private const string CELLS = "_cells";
        private const string POSITION = "_position";
        private const string VALUE = "_value";

        private readonly List<IntGridValueDefinition> _entityValues = new();
        private readonly List<SerializedIntGridCell> _entityCells = new();
        private readonly World _world;
        private readonly Entity _intGridEntity;
        private readonly Entity _rendererEntity;
        private readonly Hash128 _intGridHash;
        private readonly Hash128 _rendererHash;
        private readonly string _displayName;
        private readonly bool _isTerrain;

        public MosaicPaintingTarget(TilemapAuthoring owner)
        {
            Owner = owner;
            IntGrid = owner.intGrid;
            LayerIndex = 0;
            Grid = owner.GetComponentInParent<GridAuthoring>();
            SceneCullingMask = owner.gameObject.sceneCullingMask;
        }

        public MosaicPaintingTarget(TilemapTerrainAuthoring owner, IntGridDefinition intGrid, int layerIndex)
        {
            Owner = owner;
            IntGrid = intGrid;
            LayerIndex = layerIndex;
            Grid = owner.GetComponentInParent<GridAuthoring>();
            SceneCullingMask = owner.gameObject.sceneCullingMask;
        }

        public MosaicPaintingTarget(World world, Entity intGridEntity, Entity rendererEntity,
            string displayName, bool isTerrain, int layerIndex)
        {
            _world = world;
            _intGridEntity = intGridEntity;
            _rendererEntity = rendererEntity;
            _displayName = displayName;
            _isTerrain = isTerrain;
            LayerIndex = layerIndex;

            var entityManager = world.EntityManager;
            var intGridData = entityManager.GetComponentData<IntGridData>(intGridEntity);
            _intGridHash = intGridData.Hash;
            _rendererHash = entityManager.GetComponentData<TilemapRendererData>(rendererEntity).MeshHash;
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
            if (!layers.TryGetValue(_intGridHash, out var layer)) return;
            foreach (var cell in layer.IntGrid)
            {
                _entityCells.Add(new SerializedIntGridCell(new Vector2Int(cell.Key.x, cell.Key.y), cell.Value));
            }

            _entityCells.Sort((left, right) => Compare(left.Position, right.Position));
        }

        public MonoBehaviour Owner { get; }

        public IntGridDefinition IntGrid { get; }

        public GridAuthoring Grid { get; }

        public int LayerIndex { get; }

        public bool IsTerrain => _world != null ? _isTerrain : Owner is TilemapTerrainAuthoring;

        public bool IsSubScene => _world != null || Owner != null && Owner.gameObject.scene.isSubScene;

        public bool IsEntityTarget => _world != null;

        public bool HasLoadedAuthoringScene => !IsEntityTarget && Owner != null
            && Owner.gameObject.scene.IsValid() && Owner.gameObject.scene.isLoaded;

        public bool IsPaintable => !IsEntityTarget && IsValid;

        public ulong SceneCullingMask { get; }

        public Hash128 IntGridHash => _world != null ? _intGridHash : Owner switch
        {
            TilemapAuthoring tilemap => BakerUtils.GetHash(tilemap, IntGrid, tilemap.isGlobal, 0),
            TilemapTerrainAuthoring terrain => BakerUtils.GetHash(terrain, IntGrid, terrain.isGlobal, LayerIndex),
            _ => default,
        };

        public Hash128 RendererHash => _world != null ? _rendererHash
            : Owner is TilemapTerrainAuthoring terrain && terrain.intGridLayers.Count != 0
                ? BakerUtils.GetHash(terrain, terrain.intGridLayers[0], terrain.isGlobal, 0)
                : IntGridHash;

        public string Id => _world != null
            ? $"{_world.SequenceNumber}:{_intGridEntity.Index}:{_intGridEntity.Version}"
            : $"{GlobalObjectId.GetGlobalObjectIdSlow(Owner)}:{LayerIndex}";

        public string DisplayName => _world != null ? _displayName : IsTerrain
            ? $"{Owner.name} / Layer {LayerIndex + 1} / {IntGrid?.name ?? "Missing IntGrid"}"
            : $"{Owner.name} / {IntGrid?.name ?? "Missing IntGrid"}";

        public string AdditionalValidationMessage { get; set; }

        public bool IsValid => (_world != null ? IsEntityValid() : IsAuthoringValid()) 
                               && string.IsNullOrEmpty(AdditionalValidationMessage);

        private RenderingData RenderingData => Owner switch
        {
            TilemapAuthoring tilemap => tilemap.renderingData,
            TilemapTerrainAuthoring terrain => terrain.renderingData,
            _ => null,
        };

        public IReadOnlyList<IntGridValueDefinition> Values => _world != null
            ? _entityValues
            : IntGrid != null ? IntGrid.intGridValues : Array.Empty<IntGridValueDefinition>();

        public IReadOnlyList<SerializedIntGridCell> Cells
        {
            get
            {
                if (_world != null) return _entityCells;
                if (Owner is TilemapAuthoring tilemap) return tilemap.PaintedCells;
                if (Owner is not TilemapTerrainAuthoring terrain) return Array.Empty<SerializedIntGridCell>();

                foreach (var layer in terrain.PaintedLayers)
                {
                    if (layer.IntGrid == IntGrid) return layer.Cells;
                }

                return Array.Empty<SerializedIntGridCell>();
            }
        }

        public Vector3 WorldPosition => GetLocalToWorldMatrix().GetPosition();

        public MosaicPaintingVisibilityTarget VisibilityTarget => new(IntGridHash, RendererHash);

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

        internal PaintStroke BeginStroke(short value)
        {
            return new PaintStroke(this, value);
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

        private float PaintedCellCenterOffset => IsDualGrid ? 0f : 0.5f;

        private bool IsDualGrid => _world != null
            ? _world.EntityManager.GetComponentData<IntGridData>(_intGridEntity).DualGrid
            : IntGrid.useDualGrid;

        private bool IsAuthoringValid()
        {
            return HasLoadedAuthoringScene && Owner.isActiveAndEnabled && IntGrid != null && Grid != null
                   && RenderingData?.material != null;
        }

        private bool IsEntityValid()
        {
            if (!_world.IsCreated) return false;
            var entityManager = _world.EntityManager;
            return entityManager.Exists(_intGridEntity) && entityManager.Exists(_rendererEntity)
                   && entityManager.HasComponent<IntGridData>(_intGridEntity)
                   && entityManager.HasComponent<TilemapTransform>(_intGridEntity)
                   && entityManager.HasComponent<TilemapRendererData>(_rendererEntity)
                   && entityManager.HasComponent<LocalToWorld>(_rendererEntity) && _entityValues.Count != 0;
        }

        private TilemapTransform GetTilemapTransform()
        {
            if (_world != null) return _world.EntityManager.GetComponentData<TilemapTransform>(_intGridEntity);

            return new TilemapTransform
            {
                CellSize = Grid.CellSize,
                Swizzle = Grid.CellSwizzle,
                Orientation = RenderingData.orientation,
            };
        }

        private Matrix4x4 GetLocalToWorldMatrix()
        {
            if (_world == null) return Owner.transform.localToWorldMatrix;

            var value = _world.EntityManager.GetComponentData<LocalToWorld>(_rendererEntity).Value;
            return new Matrix4x4(value.c0, value.c1, value.c2, value.c3);
        }

        private static int Compare(Vector2Int left, Vector2Int right)
        {
            var y = left.y.CompareTo(right.y);
            return y != 0 ? y : left.x.CompareTo(right.x);
        }

        private SerializedProperty FindCellsProperty(SerializedObject serializedObject)
        {
            if (Owner is TilemapAuthoring) return serializedObject.FindProperty(PAINTED_CELLS);

            var layers = serializedObject.FindProperty(PAINTED_LAYERS);
            if (layers == null) return null;

            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                if (layer.FindPropertyRelative(INT_GRID).objectReferenceValue == IntGrid)
                {
                    return layer.FindPropertyRelative(CELLS);
                }
            }

            return null;
        }

        internal static bool IsPaintedCellProperty(UnityEngine.Object target, string propertyPath)
        {
            if (target is TilemapAuthoring)
            {
                return propertyPath == PAINTED_CELLS
                       || propertyPath.StartsWith($"{PAINTED_CELLS}.", StringComparison.Ordinal);
            }

            if (target is not TilemapTerrainAuthoring
                || !propertyPath.StartsWith($"{PAINTED_LAYERS}.", StringComparison.Ordinal))
            {
                return false;
            }

            return propertyPath.EndsWith($".{CELLS}", StringComparison.Ordinal)
                   || propertyPath.Contains($".{CELLS}.", StringComparison.Ordinal);
        }

        internal sealed class PaintStroke : IDisposable
        {
            private readonly MosaicPaintingTarget _target;
            private readonly SerializedObject _serializedObject;
            private readonly SerializedProperty _cells;
            private readonly short _value;
            private bool _changed;

            public PaintStroke(MosaicPaintingTarget target, short value)
            {
                _target = target;
                _value = value;
                if (!target.IsPaintable) return;
                if (target.Owner == null) return;

                _serializedObject = new SerializedObject(target.Owner);
                _serializedObject.Update();
                _cells = target.FindCellsProperty(_serializedObject);
                if (_cells != null)
                {
                    Undo.RecordObject(target.Owner, value == 0 ? "Erase Mosaic IntGrid" : "Paint Mosaic IntGrid");
                }
            }

            public bool SetCells(IEnumerable<Vector2Int> positions)
            {
                if (_cells == null) return false;

                var changed = false;
                foreach (var position in positions)
                {
                    var index = FindIndex(position, out var exists);
                    if (_value == 0)
                    {
                        if (!exists) continue;
                        _cells.DeleteArrayElementAtIndex(index);
                    }
                    else if (exists)
                    {
                        var valueProperty = _cells.GetArrayElementAtIndex(index).FindPropertyRelative(VALUE);
                        if (valueProperty.intValue == _value) continue;
                        valueProperty.intValue = _value;
                    }
                    else
                    {
                        _cells.InsertArrayElementAtIndex(index);
                        var cell = _cells.GetArrayElementAtIndex(index);
                        cell.FindPropertyRelative(POSITION).vector2IntValue = position;
                        cell.FindPropertyRelative(VALUE).intValue = _value;
                    }

                    changed = true;
                }

                if (!changed) return false;
                _serializedObject.ApplyModifiedProperties();
                _changed = true;
                return true;
            }

            public void Dispose()
            {
                if (!_changed || _target.Owner == null) return;

                if (PrefabUtility.IsPartOfPrefabInstance(_target.Owner))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(_target.Owner);
                }
                EditorUtility.SetDirty(_target.Owner);
                if (_target.Owner.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(_target.Owner.gameObject.scene);
                }
            }

            private int FindIndex(Vector2Int position, out bool exists)
            {
                var min = 0;
                var max = _cells.arraySize - 1;
                while (min <= max)
                {
                    var index = (min + max) / 2;
                    var candidate = _cells.GetArrayElementAtIndex(index).FindPropertyRelative(POSITION).vector2IntValue;
                    var comparison = Compare(candidate, position);
                    if (comparison == 0)
                    {
                        exists = true;
                        return index;
                    }

                    if (comparison < 0) min = index + 1;
                    else max = index - 1;
                }

                exists = false;
                return min;
            }
        }
    }
}
