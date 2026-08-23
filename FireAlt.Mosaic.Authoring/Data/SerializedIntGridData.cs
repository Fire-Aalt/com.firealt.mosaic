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

    public readonly struct SerializedIntGridRectangle
    {
        public SerializedIntGridRectangle(Vector2Int position, Vector2Int size, short value)
        {
            Position = position;
            Size = size;
            Value = value;
        }

        public Vector2Int Position { get; }

        public Vector2Int Size { get; }

        public short Value { get; }

        public int CellCount => checked(Size.x * Size.y);
    }

    [Serializable]
    public sealed class SerializedIntGridData : ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector] private byte[] _bytes = Array.Empty<byte>();
        [NonSerialized] private List<SerializedIntGridCell> _cells;
        [NonSerialized] private List<SerializedIntGridRectangle> _rectangles;
        [NonSerialized] private int _cellCount;
        [NonSerialized] private bool _decoded;

        public IReadOnlyList<SerializedIntGridCell> Cells
        {
            get
            {
                EnsureCells();
                return _cells;
            }
        }

        internal IReadOnlyList<SerializedIntGridRectangle> Rectangles
        {
            get
            {
                EnsureRectangles();
                return _rectangles;
            }
        }

        internal int CellCount
        {
            get
            {
                EnsureRectangles();
                return _cellCount;
            }
        }

        internal IReadOnlyList<byte> Bytes => _bytes;

        internal List<SerializedIntGridCell> MutableCells
        {
            get
            {
                EnsureCells();
                return _cells;
            }
        }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            _cells = null;
            _rectangles = null;
            _cellCount = 0;
            _decoded = false;
        }

        internal void Collapse()
        {
            EnsureCells();
            _rectangles ??= new List<SerializedIntGridRectangle>();
            _bytes = IntGridCellPacking.PackAndEncode(_cells, _rectangles);
            _cellCount = _cells.Count;
            _decoded = true;
        }

        private void EnsureRectangles()
        {
            if (_decoded) return;

            _rectangles ??= new List<SerializedIntGridRectangle>();
            _cellCount = IntGridCellPacking.Decode(_bytes, _rectangles);
            _decoded = true;
        }

        private void EnsureCells()
        {
            if (_cells != null) return;

            EnsureRectangles();
            _cells = new List<SerializedIntGridCell>(_cellCount);
            IntGridCellPacking.Expand(_rectangles, _cells);
        }
    }

    [Serializable]
    public sealed class SerializedIntGridLayer
    {
        [SerializeField, HideInInspector] private IntGridDefinition _intGrid;
        [SerializeField, HideInInspector] private SerializedIntGridData _paintedData = new();

        public SerializedIntGridLayer(IntGridDefinition intGrid)
        {
            _intGrid = intGrid;
        }

        public IntGridDefinition IntGrid => _intGrid;

        public IReadOnlyList<SerializedIntGridCell> Cells => _paintedData.Cells;

        internal SerializedIntGridData PaintedData => _paintedData;

        internal void SetIntGrid(IntGridDefinition intGrid)
        {
            _intGrid = intGrid;
        }
    }
}
