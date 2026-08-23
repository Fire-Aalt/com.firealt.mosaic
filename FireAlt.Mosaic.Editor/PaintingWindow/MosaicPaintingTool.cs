using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireAlt.Mosaic.Editor
{
    [EditorTool("Paint Mosaic IntGrid")]
    internal sealed class MosaicPaintingTool : EditorTool
    {
        private const int CONTROL_HINT = 0x4D4F5341;
        private const int MAX_STROKE_CELL_DELTA = 500;
        private const float VISIBLE_FILL_ALPHA = 0.18f;
        private const float VISIBLE_OUTLINE_ALPHA = 1f;
        private const float OCCLUDED_ALPHA_MULTIPLIER = 0.35f;

        private readonly Vector3[] _corners = new Vector3[4];
        private readonly HashSet<Vector2Int> _brushCells = new();
        private GUIContent _toolbarIcon;
        private int _controlId;
        private int _undoGroup = -1;
        private bool _activationPending;
        private bool _isAvailable;
        private bool _strokeActive;
        private bool _erase;
        private Vector2Int? _previousCell;
        private Vector2Int? _rectangleStart;
        private Vector2Int _rectangleEnd;
        private MosaicPaintingSelectionStroke _paintStroke;

        public override GUIContent toolbarIcon => _toolbarIcon;

        private bool IsHotControl => GUIUtility.hotControl == _controlId;

        private void OnEnable()
        {
            _toolbarIcon = new GUIContent(EditorResources.MosaicPaintingToolIcon, "Paint Mosaic IntGrid values");
            MosaicPaintingController.SnapshotChanged += RefreshAvailability;
            EditorApplication.delayCall += RefreshAvailability;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= RefreshAvailability;
            EditorApplication.delayCall -= OpenPaintingWindow;
            EditorApplication.delayCall -= ExitPainting;
            MosaicPaintingController.SnapshotChanged -= RefreshAvailability;
        }

        public override bool IsAvailable()
        {
            return _isAvailable;
        }

        public override void OnActivated()
        {
            if (!MosaicPaintingController.IsPainting)
            {
                _activationPending = true;
                EditorApplication.delayCall += OpenPaintingWindow;
            }

            SceneView.RepaintAll();
            MosaicPaintingController.NotifyChanged();
        }

        public override void OnWillBeDeactivated()
        {
            EditorApplication.delayCall -= OpenPaintingWindow;
            _activationPending = false;
            EndStroke();
            SceneView.RepaintAll();
            MosaicPaintingController.ClearSelection();
        }

        private void OpenPaintingWindow()
        {
            try
            {
                if (ToolManager.activeToolType == typeof(MosaicPaintingTool) && !MosaicPaintingController.IsPainting)
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

            var selection = MosaicPaintingController.Selection;
            var target = selection?.Anchor;
            if (selection == null || !selection.IsValid || target == null)
            {
                EndStroke();
                MosaicPaintingController.ClearSelection();
                if (_activationPending) return;
                ToolManager.RestorePreviousPersistentTool();
                return;
            }

            _controlId = GUIUtility.GetControlID(CONTROL_HINT, FocusType.Passive);
            if (currentEvent.type == EventType.Layout && !currentEvent.shift)
            {
                HandleUtility.AddDefaultControl(_controlId);
            }

            if (_strokeActive && !IsHotControl)
            {
                EndStroke();
                return;
            }

            if (currentEvent.type == EventType.MouseMove) sceneView.Repaint();

            var hasCell = target.TryGetCell(currentEvent.mousePosition, out var cell);
            if (currentEvent.rawType == EventType.MouseUp && IsHotControl && currentEvent.button == (_erase ? 1 : 0))
            {
                if (_rectangleStart.HasValue)
                {
                    if (hasCell) _rectangleEnd = cell;
                    ApplyRectangle();
                }

                EndStroke();
                if (currentEvent.type is not EventType.Ignore and not EventType.Used) currentEvent.Use();
                return;
            }

            if (!hasCell) return;

            if (_rectangleStart.HasValue && currentEvent.type == EventType.MouseDrag && IsHotControl)
            {
                _rectangleEnd = cell;
                sceneView.Repaint();
            }

            DrawPreview(target, cell, currentEvent.alt && !currentEvent.shift && !_strokeActive);

            if (_rectangleStart.HasValue)
            {
                if (currentEvent.type == EventType.MouseDrag && IsHotControl) currentEvent.Use();
                return;
            }

            if (currentEvent.shift || currentEvent.button == 2) return;

            switch (currentEvent.type)
            {
                case EventType.MouseDown when (currentEvent.button == 0 || currentEvent.button == 1)
                                                   && HandleUtility.nearestControl == _controlId:
                {
                    _erase = currentEvent.button == 1;
                    var rectangle = currentEvent.alt;
                    Undo.IncrementCurrentGroup();
                    _undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName(GetUndoName(rectangle, _erase));
                    GUIUtility.hotControl = _controlId;
                    GUIUtility.keyboardControl = 0;
                    if (!selection.TryBeginStroke(_erase, out _paintStroke))
                    {
                        EndStroke();
                        MosaicPaintingController.ClearSelection();
                        currentEvent.Use();
                        break;
                    }

                    _strokeActive = true;
                    if (rectangle)
                    {
                        _rectangleStart = cell;
                        _rectangleEnd = cell;
                    }
                    else
                    {
                        _previousCell = cell;
                        ApplyBrush(cell);
                    }

                    currentEvent.Use();
                    break;
                }
                case EventType.MouseDrag when _strokeActive && IsHotControl && !currentEvent.alt:
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
            _rectangleStart = null;
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
            var radius = MosaicPaintingController.BrushRadius;
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
            _paintStroke?.SetCells(_brushCells);
        }

        private void ApplyRectangle()
        {
            if (!_rectangleStart.HasValue) return;

            _brushCells.Clear();
            if (TryAddRectangleCells(_rectangleStart.Value, _rectangleEnd, _brushCells)) ApplyCells();
        }

        private void DrawPreview(MosaicPaintingTarget target, Vector2Int cell, bool rectangleModifier)
        {
            _brushCells.Clear();
            if (_rectangleStart.HasValue)
            {
                if (!TryAddRectangleCells(_rectangleStart.Value, _rectangleEnd, _brushCells))
                    _brushCells.Add(_rectangleStart.Value);
            }
            else if (rectangleModifier)
            {
                _brushCells.Add(cell);
            }
            else
            {
                AddBrushCells(cell);
            }

            DrawGhostCells(target);
        }

        private void DrawGhostCells(MosaicPaintingTarget target)
        {
            var fill = MosaicPaintingController.Color;
            fill.a = VISIBLE_FILL_ALPHA;
            var outline = MosaicPaintingController.Color;
            outline.a = VISIBLE_OUTLINE_ALPHA;
            var occludedFill = fill;
            occludedFill.a *= OCCLUDED_ALPHA_MULTIPLIER;
            var occludedOutline = outline;
            occludedOutline.a *= OCCLUDED_ALPHA_MULTIPLIER;

            var previousZTest = Handles.zTest;
            try
            {
                DrawGhostPass(target, occludedFill, occludedOutline, CompareFunction.Greater);
                DrawGhostPass(target, fill, outline, CompareFunction.LessEqual);
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
        }

        private void DrawGhostPass(MosaicPaintingTarget target, Color fill, Color outline, CompareFunction zTest)
        {
            Handles.zTest = zTest;
            foreach (var cell in _brushCells)
            {
                target.GetCellCorners(cell, _corners, 0.006f);
                Handles.DrawSolidRectangleWithOutline(_corners, fill, outline);
            }
        }

        internal static bool TryAddRectangleCells(Vector2Int start, Vector2Int end, ISet<Vector2Int> cells)
        {
            if (start == end) return false;

            var minX = Mathf.Min(start.x, end.x);
            var maxX = Mathf.Max(start.x, end.x);
            var minY = Mathf.Min(start.y, end.y);
            var maxY = Mathf.Max(start.y, end.y);
            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++) cells.Add(new Vector2Int(x, y));
            }

            return true;
        }

        private static string GetUndoName(bool rectangle, bool erase)
        {
            if (rectangle) return erase ? "Clear Mosaic IntGrid Rectangle" : "Fill Mosaic IntGrid Rectangle";
            return erase ? "Erase Mosaic IntGrid" : "Paint Mosaic IntGrid";
        }

        internal static bool IsWithinBrushRadius(int x, int y)
        {
            var radius = MosaicPaintingController.BrushRadius;
            return (x * x) + (y * y) <= radius * radius;
        }

        internal static void ExitPainting()
        {
            MosaicPaintingController.ClearSelection();
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
