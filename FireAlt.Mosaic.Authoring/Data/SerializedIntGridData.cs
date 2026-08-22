using System;
using System.Collections.Generic;
using UnityEngine;

namespace FireAlt.Mosaic.Authoring
{
    [Serializable]
    public struct SerializedIntGridCell
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
