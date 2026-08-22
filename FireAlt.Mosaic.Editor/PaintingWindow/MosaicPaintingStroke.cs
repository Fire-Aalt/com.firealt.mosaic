using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingStroke : IDisposable
    {
        private readonly MosaicPaintingTarget _target;
        private readonly List<SerializedIntGridCell> _cells;
        private readonly Dictionary<Vector2Int, int> _cellIndices;
        private readonly List<Vector2Int> _changedCells = new();
        private readonly short _value;
        private bool _changed;

        public MosaicPaintingStroke(MosaicPaintingTarget target, short value)
        {
            _target = target;
            _value = value;
            if (!target.TryGetMutableCells(out var cells)) return;

            _cells = cells;
            _cellIndices = new Dictionary<Vector2Int, int>(_cells.Count);
            for (var i = 0; i < _cells.Count; i++)
            {
                _cellIndices.Add(_cells[i].Position, i);
            }
        }

        public IReadOnlyList<Vector2Int> ChangedCells => _changedCells;

        public bool SetCells(IEnumerable<Vector2Int> positions)
        {
            _changedCells.Clear();
            if (_cells == null || _target.Owner == null) return false;

            foreach (var position in positions)
            {
                if (_value == 0)
                {
                    EraseCell(position);
                }
                else
                {
                    PaintCell(position);
                }
            }

            return _changedCells.Count != 0;
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

        private void PaintCell(Vector2Int position)
        {
            if (_cellIndices.TryGetValue(position, out var index))
            {
                if (_cells[index].Value == _value) return;
                RecordUndo();
                _cells[index] = new SerializedIntGridCell(position, _value);
            }
            else
            {
                RecordUndo();
                _cellIndices.Add(position, _cells.Count);
                _cells.Add(new SerializedIntGridCell(position, _value));
            }

            _changedCells.Add(position);
        }

        private void EraseCell(Vector2Int position)
        {
            if (!_cellIndices.TryGetValue(position, out var index)) return;

            RecordUndo();
            var lastIndex = _cells.Count - 1;
            if (index != lastIndex)
            {
                var movedCell = _cells[lastIndex];
                _cells[index] = movedCell;
                _cellIndices[movedCell.Position] = index;
            }

            _cells.RemoveAt(lastIndex);
            _cellIndices.Remove(position);
            _changedCells.Add(position);
        }

        private void RecordUndo()
        {
            if (_changed) return;

            Undo.RegisterCompleteObjectUndo(_target.Owner,
                _value == 0 ? "Erase Mosaic IntGrid" : "Paint Mosaic IntGrid");
            _changed = true;
        }
    }
}
