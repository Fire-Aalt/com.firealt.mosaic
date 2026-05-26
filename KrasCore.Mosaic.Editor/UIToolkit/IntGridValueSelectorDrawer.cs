using System;
using System.Reflection;
using FireAlt.Core.Editor;
using FireAlt.Mosaic.Authoring;
using FireAlt.Mosaic.Data;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireAlt.Mosaic.Editor
{
    public static class IntGridValueSelectorDrawer
    {
        public static VisualElement Create(FieldInfo fieldInf, SerializedProperty property)
        {
            var root = new VisualElement { name = "IntGridValueSelector_Root" };
            root.styleSheets.Add(EditorResources.StyleSheet);

            var selectorContainer = new VisualElement { name = "IntGridValueSelector_Container" };
            
            root.Add(selectorContainer);
            
            void Refresh()
            {
                var owner = SerializationUtils.GetParentObject(property);
                if (fieldInf.GetValue(owner) is not IntGridValueSelector selectorObj)
                {
                    throw new Exception("Selector is null");
                }
                var intGrid = selectorObj.intGrid;
                
                EnsureCellsCount(selectorContainer, intGrid);

                for (int i = 0; i < intGrid.intGridValues.Count; i++)
                {
                    var val =  intGrid.intGridValues[i];
                    CreateButton(owner, fieldInf, i, val.texture, val.color, val.name, selectorObj);
                }
                
                CreateButton(owner, fieldInf, intGrid.intGridValues.Count, EditorResources.AnyTexture, Color.white, "Any Value/No Value", selectorObj);
            }
            
            void CreateButton(object owner, FieldInfo field, int i, Texture texture, Color color, string name, IntGridValueSelector selectorObj)
            {
                var button = selectorContainer[i] as Button;
                
                var icon = button[0];
                var text = button[1] as Label;

                icon.style.backgroundImage = StyleKeyword.None;
                icon.style.backgroundColor = StyleKeyword.None;
                text.text = "";
                
                if (texture != null)
                {
                    icon.style.backgroundImage = texture as Texture2D;
                }
                else
                {
                    icon.style.backgroundColor = color;
                }
                text.text = name;
                
                var buttonData = new ClickData
                {
                    Index = i,
                    Root = selectorContainer,
                    Selector = selectorObj
                };
                
                button.RegisterCallback<ClickEvent, ClickData>(Clicked, buttonData);
                
                if (selectorObj.value == 0 && i == 0)
                {
                    Clicked(null, buttonData);
                    selectorObj.value = selectorObj.intGrid.intGridValues[0].value;
                    field.SetValue(owner, selectorObj);
                }
            }

            root.RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            root.TrackPropertyValue(property, _ => Refresh());
            Refresh();

            return root;
        }


        private struct ClickData
        {
            public int Index;
            public VisualElement Root;
            public IntGridValueSelector Selector;
        }
        
        private static void Clicked(ClickEvent clickEvent, ClickData clickData)
        {
            for (int i = 0; i < clickData.Root.childCount; i++)
            {
                clickData.Root[i].style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.15f));
            }

            var values = clickData.Selector.intGrid.intGridValues;
            
            clickData.Root[clickData.Index].style.backgroundColor = Color.cadetBlue;
            
            clickData.Selector.value = clickData.Index == values.Count
                ? RuleGridConsts.AnyIntGridValue
                : values[clickData.Index].value;
        }
        
        private static void EnsureCellsCount(VisualElement cellsMatrix, IntGridDefinition cellsCount)
        {
            var buttonsCount = cellsCount.intGridValues.Count + 1;
            if (cellsMatrix.childCount == buttonsCount)
            {
                return;
            }
            
            cellsMatrix.Clear();
            
            for (int i = 0; i < buttonsCount; i++)
            {
                var button = new Button { name = $"Button_{i}" };
                var icon = new VisualElement { name = "Icon" };
                var text = new Label { name = "Text" };
                
                button.Add(icon);
                button.Add(text);
                cellsMatrix.Add(button);
            }
        }
    }
}