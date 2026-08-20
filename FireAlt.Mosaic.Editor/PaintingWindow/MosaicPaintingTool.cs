using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireAlt.Mosaic.Editor
{
    internal sealed class MosaicPaintingToolShortcutContext : IShortcutContext
    {
        public bool active => ToolManager.activeToolType == typeof(MosaicPaintingTool);
    }

    [EditorTool("Paint Mosaic IntGrid")]
    internal sealed class MosaicPaintingTool : EditorTool
    {
        private const int CONTROL_HINT = 0x4D4F5341;
        private const int MAX_STROKE_CELL_DELTA = 500;

        private readonly Vector3[] _corners = new Vector3[4];
        private readonly HashSet<Vector2Int> _brushCells = new();
        private GUIContent _toolbarIcon;
        private MosaicPaintingToolShortcutContext _shortcutContext;
        private int _controlId;
        private int _undoGroup = -1;
        private bool _activationPending;
        private bool _isAvailable;
        private bool _strokeActive;
        private bool _erase;
        private Vector2Int? _previousCell;
        private MosaicPaintingTarget.PaintStroke _paintStroke;

        public override GUIContent toolbarIcon => _toolbarIcon;

        private bool IsHotControl => GUIUtility.hotControl == _controlId;

        private void OnEnable()
        {
            _toolbarIcon = new GUIContent(EditorResources.MosaicPaintingToolIcon, "Paint Mosaic IntGrid values");
            _shortcutContext = new MosaicPaintingToolShortcutContext();
            ShortcutManager.RegisterContext(_shortcutContext);
            MosaicPaintingPreviewService.Refreshed += RefreshAvailability;
            RefreshAvailability();
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= OpenPaintingWindow;
            EditorApplication.delayCall -= ExitPainting;
            MosaicPaintingPreviewService.Refreshed -= RefreshAvailability;
            if (_shortcutContext != null) ShortcutManager.UnregisterContext(_shortcutContext);
            _shortcutContext = null;
        }

        public override bool IsAvailable()
        {
            return _isAvailable;
        }

        public override void OnActivated()
        {
            if (!MosaicPaintingSession.IsPainting)
            {
                _activationPending = true;
                EditorApplication.delayCall += OpenPaintingWindow;
            }

            SceneView.RepaintAll();
            MosaicPaintingSession.NotifyChanged();
        }

        public override void OnWillBeDeactivated()
        {
            EditorApplication.delayCall -= OpenPaintingWindow;
            _activationPending = false;
            EndStroke();
            SceneView.RepaintAll();
            MosaicPaintingSession.Clear();
        }

        private void OpenPaintingWindow()
        {
            try
            {
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool) && !MosaicPaintingSession.IsPainting)
                {
                    MosaicPaintingWindow.OpenAndSelectFirst();
                }
            }
            finally
            {
                _activationPending = false;
            }
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (window is not SceneView sceneView) return;

            var currentEvent = Event.current;
            if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.Escape)
            {
                EndStroke();
                currentEvent.Use();
                ExitPainting();
                return;
            }

            var target = MosaicPaintingSession.Target;
            if (target == null || !target.IsPaintable)
            {
                EndStroke();
                MosaicPaintingSession.Clear();
                if (_activationPending) return;
                ToolManager.RestorePreviousPersistentTool();
                return;
            }

            _controlId = GUIUtility.GetControlID(CONTROL_HINT, FocusType.Passive);
            if (currentEvent.type == EventType.Layout && !currentEvent.alt && !currentEvent.shift)
            {
                HandleUtility.AddDefaultControl(_controlId);
            }

            if (currentEvent.type == EventType.MouseMove) sceneView.Repaint();

            if (currentEvent.rawType == EventType.MouseUp && IsHotControl && currentEvent.button == (_erase ? 1 : 0))
            {
                EndStroke();
                if (currentEvent.type is not EventType.Ignore and not EventType.Used) currentEvent.Use();
                return;
            }

            if (!target.TryGetCell(currentEvent.mousePosition, out var cell)) return;
            DrawBrush(target, cell);

            if (currentEvent.alt || currentEvent.shift || currentEvent.button == 2) return;

            switch (currentEvent.type)
            {
                case EventType.MouseDown when (currentEvent.button == 0 || currentEvent.button == 1)
                                                   && HandleUtility.nearestControl == _controlId:
                {
                    _erase = currentEvent.button == 1;
                    Undo.IncrementCurrentGroup();
                    _undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName(_erase ? "Erase Mosaic IntGrid" : "Paint Mosaic IntGrid");
                    GUIUtility.hotControl = _controlId;
                    GUIUtility.keyboardControl = 0;
                    var value = _erase ? (short)0 : MosaicPaintingSession.Value;
                    _paintStroke = target.BeginStroke(value);
                    _strokeActive = true;
                    _previousCell = cell;
                    ApplyBrush(cell);
                    currentEvent.Use();
                    break;
                }
                case EventType.MouseDrag when _strokeActive && IsHotControl:
                    ApplyStroke(_previousCell ?? cell, cell);
                    _previousCell = cell;
                    currentEvent.Use();
                    break;
            }
        }

        private void EndStroke()
        {
            _paintStroke?.Dispose();
            _paintStroke = null;
            if (IsHotControl) GUIUtility.hotControl = 0;
            if (_undoGroup >= 0) Undo.CollapseUndoOperations(_undoGroup);

            _strokeActive = false;
            _previousCell = null;
            _undoGroup = -1;
            _brushCells.Clear();
            SceneView.RepaintAll();
        }

        private void ApplyStroke(Vector2Int start, Vector2Int end)
        {
            if (Mathf.Abs(end.x - start.x) > MAX_STROKE_CELL_DELTA
                || Mathf.Abs(end.y - start.y) > MAX_STROKE_CELL_DELTA)
            {
                ApplyBrush(end);
                return;
            }

            _brushCells.Clear();
            var current = start;
            var deltaX = Mathf.Abs(end.x - start.x);
            var deltaY = Mathf.Abs(end.y - start.y);
            var stepX = start.x < end.x ? 1 : -1;
            var stepY = start.y < end.y ? 1 : -1;
            var error = deltaX - deltaY;

            while (true)
            {
                AddBrushCells(current);
                if (current == end) break;

                var doubledError = error * 2;
                if (doubledError > -deltaY)
                {
                    error -= deltaY;
                    current.x += stepX;
                }

                if (doubledError < deltaX)
                {
                    error += deltaX;
                    current.y += stepY;
                }
            }

            ApplyCells();
        }

        private void ApplyBrush(Vector2Int center)
        {
            _brushCells.Clear();
            AddBrushCells(center);
            ApplyCells();
        }

        private void AddBrushCells(Vector2Int center)
        {
            var radius = MosaicPaintingSession.BrushRadius;
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    if (!IsWithinBrushRadius(x, y)) continue;
                    _brushCells.Add(center + new Vector2Int(x, y));
                }
            }
        }

        private void ApplyCells()
        {
            if (_paintStroke == null || !_paintStroke.SetCells(_brushCells)) return;

            var value = _erase ? (short)0 : MosaicPaintingSession.Value;
            MosaicPaintingSession.NotifyCellsChanged(_brushCells, value);
        }

        private void DrawBrush(MosaicPaintingTarget target, Vector2Int center)
        {
            var fill = MosaicPaintingSession.Color;
            fill.a = 0.18f;
            var outline = MosaicPaintingSession.Color;
            outline.a = 1f;

            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.LessEqual;
            var radius = MosaicPaintingSession.BrushRadius;
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    if (!IsWithinBrushRadius(x, y)) continue;
                    target.GetCellCorners(center + new Vector2Int(x, y), _corners, 0.006f);
                    Handles.DrawSolidRectangleWithOutline(_corners, fill, outline);
                }
            }

            Handles.zTest = previousZTest;
        }

        internal static bool IsWithinBrushRadius(int x, int y)
        {
            var radius = MosaicPaintingSession.BrushRadius;
            return (x * x) + (y * y) <= radius * radius;
        }

        [Shortcut("Mosaic/Exit Painting", typeof(MosaicPaintingToolShortcutContext), KeyCode.Escape)]
        internal static void ExitPainting()
        {
            MosaicPaintingSession.Clear();
            if (ToolManager.activeToolType == typeof(MosaicPaintingTool))
            {
                ToolManager.RestorePreviousPersistentTool();
            }
        }

        private void RefreshAvailability()
        {
            _isAvailable = MosaicPaintingWindow.HasTargets();
            var hidden = !_isAvailable;
            if (isHidden != hidden)
            {
                SetHidden(hidden);
                ToolManager.RefreshAvailableTools();
            }

            if (_isAvailable || ToolManager.activeToolType != typeof(MosaicPaintingTool)) return;
            EditorApplication.delayCall -= ExitPainting;
            EditorApplication.delayCall += ExitPainting;
        }
    }
}
