using System;
using System.Collections.Generic;
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

        public static MosaicPaintingSelection Selection { get; private set; }

        public static MosaicPaintingTarget Target => Selection?.Anchor;

        public static short Value => Selection?.PrimaryValue ?? 0;

        public static Color Color => Selection?.Color ?? Color.white;

        public static int BrushSize
        {
            get => _brushSize;
            set => _brushSize = Mathf.Clamp(value, MIN_BRUSH_SIZE, MAX_BRUSH_SIZE);
        }

        public static int BrushRadius => _brushSize - 1;

        public static bool IsPainting => Selection != null;

        public static void Select(MosaicPaintingSelection selection)
        {
            if (selection == null || !selection.IsValid)
            {
                Clear();
                return;
            }

            Selection = selection;
            if (ToolManager.activeToolType != typeof(MosaicPaintingTool)) ToolManager.SetActiveTool<MosaicPaintingTool>();
            Changed?.Invoke();
        }

        public static void Clear()
        {
            Selection = null;
            Changed?.Invoke();
        }

        public static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public static void NotifyCellsChanged(MosaicPaintingTarget target,
            IReadOnlyCollection<Vector2Int> positions, short value)
        {
            CellsChanged?.Invoke(target, positions, value);
        }
    }
}
