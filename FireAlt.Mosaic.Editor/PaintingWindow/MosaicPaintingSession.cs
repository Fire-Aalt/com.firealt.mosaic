using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using UnityEditor.EditorTools;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal static class MosaicPaintingSession
    {
        public const int MIN_BRUSH_SIZE = 1;
        public const int MAX_BRUSH_SIZE = 10;

        private static int _brushSize = MIN_BRUSH_SIZE;

        public static event Action Changed;

        public static event Action<MosaicPaintingTarget, IReadOnlyCollection<Vector2Int>, short> CellsChanged;

        public static MosaicPaintingTarget Target { get; private set; }

        public static short Value { get; private set; }

        public static Color Color { get; private set; }

        public static int BrushSize
        {
            get => _brushSize;
            set => _brushSize = Mathf.Clamp(value, MIN_BRUSH_SIZE, MAX_BRUSH_SIZE);
        }

        public static int BrushRadius => _brushSize - 1;

        public static bool IsPainting => Target != null && Value > 0;

        public static void Select(MosaicPaintingTarget target, IntGridValueDefinition value)
        {
            if (target == null || !target.IsPaintable)
            {
                Clear();
                return;
            }

            Target = target;
            Value = value.value;
            Color = value.color;
            if (ToolManager.activeToolType != typeof(MosaicPaintingTool)) ToolManager.SetActiveTool<MosaicPaintingTool>();
            Changed?.Invoke();
        }

        public static void Clear()
        {
            Target = null;
            Value = 0;
            Changed?.Invoke();
        }

        public static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public static void NotifyCellsChanged(IReadOnlyCollection<Vector2Int> positions, short value)
        {
            CellsChanged?.Invoke(Target, positions, value);
        }
    }
}
