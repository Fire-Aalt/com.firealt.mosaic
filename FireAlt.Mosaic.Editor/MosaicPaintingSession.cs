using System;
using System.Collections.Generic;
using FireAlt.Mosaic.Authoring;
using UnityEditor.EditorTools;
using UnityEngine;

namespace FireAlt.Mosaic.Editor
{
    internal static class MosaicPaintingSession
    {
        public const int MIN_BRUSH_RADIUS = 0;
        public const int MAX_BRUSH_RADIUS = 8;

        private static int _brushRadius;

        public static event Action Changed;

        public static event Action<MosaicPaintingTarget, IReadOnlyCollection<Vector2Int>, short> CellsChanged;

        public static MosaicPaintingTarget Target { get; private set; }

        public static short Value { get; private set; }

        public static Color Color { get; private set; }

        public static int BrushRadius
        {
            get => _brushRadius;
            set => _brushRadius = Mathf.Clamp(value, MIN_BRUSH_RADIUS, MAX_BRUSH_RADIUS);
        }

        public static bool IsPainting => Target != null && Value > 0;

        public static void Select(MosaicPaintingTarget target, IntGridValueDefinition value)
        {
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
