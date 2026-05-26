using System;
using System.Reflection;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public class IntGridMatrixView : VisualElement
    {
        private readonly float _padding;
        private readonly IntGridDefinition _intGridDefinition;
        
        private readonly VisualElement _matrixContainer;
        private readonly VisualElement _readOnlyOverlay;

        private readonly VisualElement _centerIcon;
        private readonly VisualElement _cellsContainer;
        
        private SerializedProperty _property;
        
        public IntGridMatrixView(bool isReadonly, IntGridDefinition intGridDefinition, float padding = 0.005f)
        {
            _intGridDefinition = intGridDefinition;
            _padding = padding;
            
            name = "IntGridMatrix_Root";
            styleSheets.Add(EditorResources.StyleSheet);

            _matrixContainer = new VisualElement { name = "IntGridMatrix_Container" };
            
            _centerIcon = new VisualElement
            {
                name = "IntGridMatrix_CenterIcon",
                pickingMode = PickingMode.Ignore,
                style =
                {
                    backgroundImage = new StyleBackground(EditorResources.MatrixCenterTexture as Texture2D)
                }
            };

            _cellsContainer = new VisualElement { name = "CellsContainer" };
            _matrixContainer.Add(_cellsContainer);
            _matrixContainer.Add(_centerIcon);

            if (isReadonly)
            {
                _readOnlyOverlay = new VisualElement { name = "ReadOnlyOverlay" };
                _matrixContainer.Add(_readOnlyOverlay);
            }
            
            Add(_matrixContainer);
        }

        private void Refresh()
        {
            var matrixObj = (IntGridMatrix)_property.boxedValue;
            
            var length = matrixObj.singleGridMatrix.Length;
            var n = (int)Mathf.Sqrt(length);

            if (n * n != length)
            {
                throw new Exception("Matrix cannot be drawn. The collection size must be square.");
            }

            var currentValues = matrixObj.GetCurrentMatrix(_intGridDefinition);
            var currentSize = matrixObj.GetCurrentSize(_intGridDefinition);
            
            var size = contentRect.width;
            
            _matrixContainer.style.width = size;
            _matrixContainer.style.height = size;

            size = _matrixContainer.contentRect.width;
            
            if (_readOnlyOverlay != null)
            {
                _readOnlyOverlay.style.width = size;
                _readOnlyOverlay.style.height = size;
            }
            
            var padding = Mathf.Max(1, size * _padding);
            var paddingCount = _intGridDefinition.useDualGrid ? n : n - 1;
            var sizeNoPadding = size - padding * paddingCount; 
            var cellSize = sizeNoPadding / n;

            _centerIcon.style.width = cellSize;
            _centerIcon.style.height = cellSize;
            _centerIcon.style.left = (size - cellSize) * 0.5f;
            _centerIcon.style.top = (size - cellSize) * 0.5f;

            var cellCount = currentSize * currentSize;
            EnsureCellsCount(_cellsContainer, cellCount);

            // Draw each cell
            for (int i = 0; i < currentSize; i++)
            {
                for (int j = 0; j < currentSize; j++)
                {
                    var cellIndex = i * currentSize + j;
                    var cell = _cellsContainer[cellIndex];
                    
                    var x = j * (cellSize + padding);
                    var y = i * (cellSize + padding);

                    var finalCellSize = new Vector2(cellSize, cellSize);
                    
                    if (_intGridDefinition.useDualGrid)
                    {
                        x -= cellSize * 0.5f;
                        y -= cellSize * 0.5f;
                        x = Mathf.Max(x, 0f);
                        y = Mathf.Max(y, 0f);

                        if ((i == 0 && j == 0) || (i == currentSize - 1 && j == currentSize - 1) ||
                            (i == 0 && j == currentSize - 1) || (i == currentSize - 1 && j == 0))
                        {
                            finalCellSize.x /= 2f;
                            finalCellSize.y /= 2f;
                        }
                        else if (i == 0 || i == currentSize - 1)
                        {
                            finalCellSize.y /= 2f;
                        }
                        else if (j == 0 || j == currentSize - 1)
                        {
                            finalCellSize.x /= 2f;
                        }
                    }
                    
                    cell.style.width = finalCellSize.x;
                    cell.style.height = finalCellSize.y;
                    cell.style.left = x;
                    cell.style.top = y;

                    DrawCell(cell, currentValues[cellIndex], size);
                }
            }
        }
        
        public void Bind(SerializedProperty property)
        {
            _property = property;
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            this.TrackPropertyValue(property, _ => Refresh());
            Refresh();
        }
        
        private static void EnsureCellsCount(VisualElement cellsMatrix, int cellsCount)
        {
            if (cellsMatrix.childCount == cellsCount)
            {
                return;
            }
            
            cellsMatrix.Clear();
            
            for (int i = 0; i < cellsCount; i++)
            {
                var cell = new VisualElement { name = $"Cell_{i}" };
                cell.AddToClassList("int-grid-matrix-cell");
                cellsMatrix.Add(cell);
                
                var cellIcon = new VisualElement { name = "CellIcon" };
                cellIcon.AddToClassList("int-grid-matrix-cell-icon");
                cell.Add(cellIcon);
                
                var notIcon = new VisualElement { name = "NotIcon" };
                notIcon.AddToClassList("int-grid-matrix-cell-icon");
                cell.Add(notIcon);
            }
        }

        private void DrawCell(VisualElement cell, IntGridValue value, float size)
        {
            var cellIcon = cell[0];
            var notIcon = cell[1];
            
            cellIcon.style.display = DisplayStyle.None;
            notIcon.style.display = DisplayStyle.None;
            
            if (value == 0)
            {
                return;
            }
            
            if (Mathf.Abs(value) == RuleGridConsts.AnyIntGridValue)
            {
                DrawIconWithBorders(cellIcon, EditorResources.AnyTexture, Color.white, size);
            }
            else
            {
                var def = IntGridValueToDefinition(value);
                if (def != null)
                {
                    if (def.texture == null)
                    {
                        DrawCellColor(cellIcon, def.color, size);
                    }
                    else
                    {
                        DrawIconWithBorders(cellIcon, def.texture, def.color, size);
                    }
                }
            }
            
            if (value < 0)
            {
                DrawIconWithBorders(notIcon, EditorResources.NotTexture, Color.red, size);
            }
        }

        private static void DrawCellColor(VisualElement icon, Color color, float size)
        {
            DrawIconWithBorders(icon, null, color, size);
            icon.style.backgroundColor = color;
        }

        private static void DrawIconWithBorders(VisualElement icon, Texture texture, Color borderColor, float size)
        {
            var borderSize = size * 0.005f;

            icon.style.backgroundColor = Color.clear;

            icon.style.borderBottomWidth = borderSize;
            icon.style.borderTopWidth = borderSize;
            icon.style.borderLeftWidth = borderSize;
            icon.style.borderRightWidth = borderSize;
            icon.style.borderBottomColor = borderColor;
            icon.style.borderTopColor = borderColor;
            icon.style.borderLeftColor = borderColor;
            icon.style.borderRightColor = borderColor;

            icon.style.display = DisplayStyle.Flex;
            
            icon.style.backgroundImage = texture != null 
                ? new StyleBackground(texture as Texture2D) 
                : StyleKeyword.None;
        }
        
        private IntGridValueDefinition IntGridValueToDefinition(IntGridValue v)
        {
            var key = Mathf.Abs(v);
            _intGridDefinition.IntGridValuesDict.TryGetValue(key, out var def);
            return def;
        }
    }
}