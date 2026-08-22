using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingStroke : IDisposable
    {
        private const string POSITION = "_position";
        private const string VALUE = "_value";

        private readonly MosaicPaintingTarget _target;
        private readonly SerializedObject _serializedObject;
        private readonly SerializedProperty _cells;
        private readonly short _value;
        private bool _changed;

        public MosaicPaintingStroke(MosaicPaintingTarget target, short value)
        {
            _target = target;
            _value = value;
            if (!target.IsPaintable || target.Owner == null) return;

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
