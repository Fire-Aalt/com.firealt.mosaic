using System;
using System.Collections.Generic;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    [Serializable]
    public struct SerializedIntGridCell : IComparable<SerializedIntGridCell>
    {
        [SerializeField] private Vector2Int _position;
        [SerializeField] private short _value;

        public SerializedIntGridCell(Vector2Int position, short value)
        {
            _position = position;
            _value = value;
        }

        public Vector2Int Position => _position;

        public short Value => _value;

        public int CompareTo(SerializedIntGridCell other)
        {
            var y = _position.y.CompareTo(other._position.y);
            return y != 0 ? y : _position.x.CompareTo(other._position.x);
        }
    }

    [Serializable]
    public sealed class SerializedIntGridLayer
    {
        [SerializeField, HideInInspector] private IntGridDefinition _intGrid;
        [SerializeField, HideInInspector] private List<SerializedIntGridCell> _cells = new();

        public SerializedIntGridLayer(IntGridDefinition intGrid)
        {
            _intGrid = intGrid;
        }

        public IntGridDefinition IntGrid => _intGrid;

        public IReadOnlyList<SerializedIntGridCell> Cells => _cells;

        internal List<SerializedIntGridCell> MutableCells => _cells;

        internal void SetIntGrid(IntGridDefinition intGrid)
        {
            _intGrid = intGrid;
        }
    }
}
