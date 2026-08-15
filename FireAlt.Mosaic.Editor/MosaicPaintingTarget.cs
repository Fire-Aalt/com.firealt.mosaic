using System;
using System.Collections.Generic;
using System.Linq;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingTarget
    {
        private const string PAINTED_CELLS = "_paintedCells";
        private const string PAINTED_LAYERS = "_paintedLayers";
        private const string INT_GRID = "_intGrid";
        private const string CELLS = "_cells";
        private const string POSITION = "_position";
        private const string VALUE = "_value";

        public MosaicPaintingTarget(TilemapAuthoring owner)
        {
            Owner = owner;
            IntGrid = owner.intGrid;
            LayerIndex = 0;
            Grid = owner.GetComponentInParent<GridAuthoring>();
        }

        public MosaicPaintingTarget(TilemapTerrainAuthoring owner, IntGridDefinition intGrid, int layerIndex)
        {
            Owner = owner;
            IntGrid = intGrid;
            LayerIndex = layerIndex;
            Grid = owner.GetComponentInParent<GridAuthoring>();
        }

        public MonoBehaviour Owner { get; }

        public IntGridDefinition IntGrid { get; }

        public GridAuthoring Grid { get; }

        public int LayerIndex { get; }

        public bool IsTerrain => Owner is TilemapTerrainAuthoring;

        public bool IsSubScene => Owner != null && Owner.gameObject.scene.isSubScene;

        public string Id => $"{GlobalObjectId.GetGlobalObjectIdSlow(Owner)}:{LayerIndex}";

        public string DisplayName => IsTerrain
            ? $"{Owner.name} / Layer {LayerIndex + 1} / {IntGrid?.name ?? "Missing IntGrid"}"
            : $"{Owner.name} / {IntGrid?.name ?? "Missing IntGrid"}";

        public string AdditionalValidationMessage { get; set; }

        public bool IsValid => Owner != null && IntGrid != null && Grid != null && RenderingData?.material != null
                               && string.IsNullOrEmpty(AdditionalValidationMessage);

        public string ValidationMessage
        {
            get
            {
                if (Owner == null) return "The tilemap owner no longer exists.";
                if (IntGrid == null) return "No IntGridDefinition is assigned.";
                if (Grid == null) return "No parent GridAuthoring was found.";
                if (RenderingData == null || RenderingData.material == null) return "No rendering material is assigned.";
                if (!string.IsNullOrEmpty(AdditionalValidationMessage)) return AdditionalValidationMessage;
                return string.Empty;
            }
        }

        public RenderingData RenderingData => Owner switch
        {
            TilemapAuthoring tilemap => tilemap.renderingData,
            TilemapTerrainAuthoring terrain => terrain.renderingData,
            _ => null,
        };

        public IReadOnlyList<SerializedIntGridCell> Cells
        {
            get
            {
                if (Owner is TilemapAuthoring tilemap) return tilemap.PaintedCells;
                if (Owner is not TilemapTerrainAuthoring terrain) return Array.Empty<SerializedIntGridCell>();

                foreach (var layer in terrain.PaintedLayers)
                {
                    if (layer.IntGrid == IntGrid) return layer.Cells;
                }

                return Array.Empty<SerializedIntGridCell>();
            }
        }

        public bool HasEntityResults => IntGrid != null && IntGrid.ruleGroups.Any(group =>
            group != null && group.rules.Any(rule => rule.TileEntities != null && rule.TileEntities.Count != 0));

        public bool TryGetValueDefinition(short value, out IntGridValueDefinition definition)
        {
            definition = null;
            if (IntGrid == null) return false;

            foreach (var candidate in IntGrid.intGridValues)
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
            var matrix = Owner.transform.worldToLocalMatrix;
            var ray = new Ray(matrix.MultiplyPoint(worldRay.origin), matrix.MultiplyVector(worldRay.direction));
            var planeAxis = Grid.CellSwizzle == Swizzle.XZY ? 1 : 2;
            var direction = ray.direction[planeAxis];
            if (Mathf.Abs(direction) < 0.00001f) return false;

            var distance = -ray.origin[planeAxis] / direction;
            if (distance < 0f) return false;

            var local = ray.GetPoint(distance);
            var second = Grid.CellSwizzle == Swizzle.XZY ? local.z : local.y;
            cell = new Vector2Int(
                Mathf.FloorToInt(local.x / Grid.CellSize.x),
                Mathf.FloorToInt(second / Grid.CellSize.y));
            return true;
        }

        public void GetCellCorners(Vector2Int cell, Vector3[] corners, float normalOffset = 0.002f)
        {
            var tilemapTransform = new TilemapTransform
            {
                CellSize = Grid.CellSize,
                Swizzle = Grid.CellSwizzle,
                Orientation = RenderingData.orientation,
            };

            var min = new float2(cell.x, cell.y);
            var max = min + 1f;
            corners[0] = Owner.transform.TransformPoint(MosaicUtils.ToWorldSpace(min, tilemapTransform));
            corners[1] = Owner.transform.TransformPoint(MosaicUtils.ToWorldSpace(new float2(min.x, max.y), tilemapTransform));
            corners[2] = Owner.transform.TransformPoint(MosaicUtils.ToWorldSpace(max, tilemapTransform));
            corners[3] = Owner.transform.TransformPoint(MosaicUtils.ToWorldSpace(new float2(max.x, min.y), tilemapTransform));

            var normal = Vector3.Cross(corners[1] - corners[0], corners[3] - corners[0]).normalized * normalOffset;
            for (var i = 0; i < corners.Length; i++) corners[i] += normal;
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

            private static int Compare(Vector2Int left, Vector2Int right)
            {
                var y = left.y.CompareTo(right.y);
                return y != 0 ? y : left.x.CompareTo(right.x);
            }
        }
    }
}
